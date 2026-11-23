"""识别本地漫画快照并翻译其中的日语对白。"""

import argparse
import contextlib
import http.client
import json
from pathlib import Path
import re
import secrets
import socket
import subprocess
import sys
import tempfile
import threading
import time


OCR_PROVIDER = "DmlExecutionProvider"
MAX_PIXELS = 40_000_000
MAX_INPUT_BYTES = 160 * 1024 * 1024
MAX_REGIONS = 120
SERVER_STATUS_TIMEOUT = 45
TRANSLATION_TIMEOUT = 25
KANA = re.compile(r"[\u3041-\u3096\u30a1-\u30fa]")


class WorkerError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def read_image(path):
    from PIL import Image
    import numpy as np

    try:
        if path.stat().st_size > MAX_INPUT_BYTES:
            raise WorkerError(4, "输入图像文件过大")
        with Image.open(path, formats=["PNG"]) as image:
            if image.width * image.height > MAX_PIXELS:
                raise WorkerError(4, "输入图像像素超出处理范围")
            if "transparency" in image.info or image.mode in {"RGBA", "LA"}:
                foreground = image.convert("RGBA")
                background = Image.new("RGBA", foreground.size, "white")
                image = Image.alpha_composite(background, foreground)
            return np.ascontiguousarray(np.asarray(image.convert("RGB"))[:, :, ::-1])
    except (OSError, ValueError, Image.DecompressionBombError):
        raise WorkerError(4, "无法读取输入 PNG") from None


def ensure_bundle(bundle, ocr_only):
    required = [bundle / "models" / name for name in ("det.onnx", "rec.onnx", "rec-dict.txt")]
    if not ocr_only:
        required.extend((bundle / "llama" / "llama-server.exe",
                         bundle / "models" / "Hy-MT2-1.8B-Q4_K_M.gguf"))
    if any(not path.is_file() for path in required):
        raise WorkerError(3, "本地翻译运行时或模型文件缺失")


def create_ocr_session(ort, model_path, profile_prefix):
    options = ort.SessionOptions()
    options.enable_mem_pattern = False
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.intra_op_num_threads = 1
    options.log_severity_level = 3
    options.enable_profiling = True
    options.profile_file_prefix = str(profile_prefix)
    session = ort.InferenceSession(str(model_path), options, providers=[OCR_PROVIDER])
    if OCR_PROVIDER not in session.get_providers():
        raise WorkerError(5, "ONNX Runtime 未启用 DirectML")
    session.disable_fallback()
    return session


def profile_gpu_nodes(session):
    path = Path(session.end_profiling())
    with path.open(encoding="utf-8") as profile:
        return sum(event.get("args", {}).get("provider") == OCR_PROVIDER for event in json.load(profile))


def detect_regions(cv, np, image, session):
    height, width = image.shape[:2]
    scale = min(1.0, 1536 / max(height, width))
    input_height = max(32, round(height * scale / 32) * 32)
    input_width = max(32, round(width * scale / 32) * 32)
    pixels = cv.resize(image, (input_width, input_height), interpolation=cv.INTER_AREA).astype(np.float32)
    pixels *= 1.0 / 255.0
    pixels -= np.array([0.485, 0.456, 0.406], dtype=np.float32)
    pixels /= np.array([0.229, 0.224, 0.225], dtype=np.float32)
    data = np.ascontiguousarray(pixels.transpose(2, 0, 1)[None])
    probability_map = session.run(None, {session.get_inputs()[0].name: data})[0][0, 0]
    mask = (probability_map > 0.3).astype(np.uint8)
    contours, _ = cv.findContours(mask, cv.RETR_LIST, cv.CHAIN_APPROX_SIMPLE)

    boxes = []
    for contour in contours:
        center, size, angle = cv.minAreaRect(contour)
        if min(size) < 3:
            continue
        x, y, w, h = cv.boundingRect(contour)
        local_mask = np.zeros((h, w), dtype=np.uint8)
        cv.drawContours(local_mask, [contour], -1, 1, cv.FILLED, offset=(-x, -y))
        if cv.mean(probability_map[y:y + h, x:x + w], local_mask)[0] < 0.5:
            continue

        # DB 检测轮廓通常位于文字内侧，按面积与周长向外扩展。
        thickness = 1.4 * size[0] * size[1] / (size[0] + size[1])
        points = cv.boxPoints((center, (size[0] + thickness, size[1] + thickness), angle))
        points *= np.array([width / probability_map.shape[1], height / probability_map.shape[0]])
        points[:, 0] = np.clip(points[:, 0], 0, width - 1)
        points[:, 1] = np.clip(points[:, 1], 0, height - 1)
        boxes.append(points)
        if len(boxes) > MAX_REGIONS:
            raise WorkerError(5, "页面文字区域过多")
    return boxes


def crop_region(cv, np, image, points):
    # boxPoints 保持相邻顶点的顺序，先把起点移到左上角。
    points = np.roll(points, -int(np.argmin(points.sum(axis=1))), axis=0)
    top_left, top_right, bottom_right, bottom_left = points
    width = max(2, round(max(np.linalg.norm(top_right - top_left), np.linalg.norm(bottom_right - bottom_left))))
    height = max(2, round(max(np.linalg.norm(bottom_left - top_left), np.linalg.norm(bottom_right - top_right))))
    corners = np.array([[0, 0], [width - 1, 0], [width - 1, height - 1], [0, height - 1]], dtype=np.float32)
    matrix = cv.getPerspectiveTransform(points, corners)
    return cv.warpPerspective(image, matrix, (width, height), borderMode=cv.BORDER_REPLICATE)


def merge_vertical_punctuation(cv, np, boxes):
    bounds = [cv.boundingRect(box) for box in boxes]
    merged = list(boxes)
    consumed = set()
    for fragment_index, (fx, fy, fw, fh) in enumerate(bounds):
        candidates = []
        for index, (x, y, width, height) in enumerate(bounds):
            gap = fy - (y + height)
            if (height > width * 1.6 and max(fw, fh) < width * 0.65
                    and x <= fx + fw / 2 <= x + width and -fh < gap < width * 0.6):
                candidates.append((abs(gap), index))
        if candidates:
            _, index = min(candidates)
            points = np.concatenate((merged[index], boxes[fragment_index]))
            merged[index] = cv.boxPoints(cv.minAreaRect(points))
            consumed.add(fragment_index)
    return [box for index, box in enumerate(merged) if index not in consumed]


def recognize_crop(cv, np, crop, session, characters):
    model_input = session.get_inputs()[0]
    expected_height = model_input.shape[2] if isinstance(model_input.shape[2], int) else 48
    expected_width = model_input.shape[3] if isinstance(model_input.shape[3], int) else None
    height, width = crop.shape[:2]
    content_width = min(expected_width or 1536, max(1, round(expected_height * width / height)))
    input_width = expected_width or max(64, ((content_width + 31) // 32) * 32)
    resized = cv.resize(crop, (content_width, expected_height), interpolation=cv.INTER_LINEAR)
    pixels = np.zeros((1, 3, expected_height, input_width), dtype=np.float32)
    pixels[0, :, :, :content_width] = resized.transpose(2, 0, 1).astype(np.float32) / 127.5 - 1.0
    probabilities = session.run(None, {model_input.name: pixels})[0][0]
    if probabilities.shape[-1] != len(characters):
        raise WorkerError(5, "OCR 字典与识别模型不匹配")

    text = []
    confidences = []
    previous = 0
    for position, token in enumerate(probabilities.argmax(axis=1)):
        if token and token != previous:
            text.append(characters[token])
            confidences.append(float(probabilities[position, token]))
        previous = token
    return "".join(text).strip(), sum(confidences) / len(confidences) if confidences else 0.0


def lay_out_vertical_glyphs(cv, np, crop):
    # 竖排日语的字形通常仍是正立。用行投影寻找字间空白，并将完整字形横向排列。
    height, width = crop.shape[:2]
    gray = cv.cvtColor(crop, cv.COLOR_BGR2GRAY)
    polarity = cv.THRESH_BINARY_INV if np.median(gray[:, [0, -1]]) > 127 else cv.THRESH_BINARY
    _, ink = cv.threshold(gray, 0, 255, polarity | cv.THRESH_OTSU)
    row_ink = np.count_nonzero(ink[:, width // 8:max(width // 8 + 1, width * 7 // 8)], axis=1)
    rows = np.flatnonzero(row_ink >= max(1, width // 20))
    if not len(rows):
        return None
    separators = np.flatnonzero(np.diff(rows) > max(1, width // 10)) + 1
    groups = np.split(rows, separators)
    if not 2 <= len(groups) <= min(32, height * 2 / width):
        return None
    if any(group[-1] - group[0] > width * 1.3 for group in groups):
        return None

    cell_height = max(width, max(int(group[-1] - group[0] + 3) for group in groups))
    background = 255 if polarity == cv.THRESH_BINARY_INV else 0
    horizontal = np.full((cell_height, width * len(groups), 3), background, dtype=crop.dtype)
    for index, group in enumerate(groups):
        glyph = crop[max(0, group[0] - 1):min(height, group[-1] + 2)]
        top = (cell_height - glyph.shape[0]) // 2
        horizontal[top:top + glyph.shape[0], index * width:(index + 1) * width] = glyph
    return horizontal


def recognize_line(cv, np, crop, session, characters):
    height, width = crop.shape[:2]
    if height <= width * 1.5:
        return recognize_crop(cv, np, crop, session, characters)

    rotated = cv.rotate(crop, cv.ROTATE_90_COUNTERCLOCKWISE)
    primary = recognize_crop(cv, np, rotated, session, characters)
    if primary[1] >= 0.85 and KANA.search(primary[0]):
        return primary
    alternatives = [primary]
    alternatives.append(recognize_crop(cv, np, cv.rotate(rotated, cv.ROTATE_180), session, characters))
    horizontal_glyphs = lay_out_vertical_glyphs(cv, np, crop)
    if horizontal_glyphs is not None:
        alternatives.append(recognize_crop(cv, np, horizontal_glyphs, session, characters))

    # 只在置信度相近时让有假名的候选优先，保留纯汉字的日语对白。
    return max(alternatives, key=lambda item: item[1] + (0.06 if KANA.search(item[0]) else 0.0))


def read_order(regions):
    bands = []
    for region in sorted(regions, key=lambda box: (box["y"], -box["x"])):
        if bands:
            anchor = bands[-1][0]
            same_row = region["y"] - anchor["y"] <= max(12, min(region["height"], anchor["height"]) * 0.5)
            if same_row:
                bands[-1].append(region)
                continue
        bands.append([region])
    return [region for band in bands for region in sorted(band, key=lambda box: (-box["x"], box["y"]))]


def read_page(image, models, temporary):
    try:
        import cv2 as cv
        import numpy as np
        import onnxruntime as ort
    except (ImportError, OSError):
        raise WorkerError(3, "本地 OCR 依赖缺失") from None

    if OCR_PROVIDER not in ort.get_available_providers():
        raise WorkerError(5, "此运行时没有 DirectML GPU 支持")
    try:
        cv.setNumThreads(1)
        detector = create_ocr_session(ort, models / "det.onnx", temporary / "det")
        boxes = merge_vertical_punctuation(cv, np, detect_regions(cv, np, image, detector))
        det_gpu = profile_gpu_nodes(detector)
        if not det_gpu:
            raise WorkerError(5, "未检测到文字检测模型在 DirectML 上运行")

        regions = []
        rec_gpu = 0
        if boxes:
            recognizer = create_ocr_session(ort, models / "rec.onnx", temporary / "rec")
            chars = [""] + (models / "rec-dict.txt").read_text(encoding="utf-8-sig").splitlines() + [" "]
            for points in boxes:
                crop = crop_region(cv, np, image, points)
                text, confidence = recognize_line(cv, np, crop, recognizer, chars)
                if text and confidence >= 0.50:
                    x, y, w, h = cv.boundingRect(points)
                    regions.append({"x": x, "y": y, "width": w, "height": h,
                                    "sourceText": text, "translatedText": ""})
            rec_gpu = profile_gpu_nodes(recognizer)
            if not rec_gpu:
                raise WorkerError(5, "未检测到文字识别模型在 DirectML 上运行")
        return read_order(regions), {"detection": det_gpu, "recognition": rec_gpu}
    except WorkerError:
        raise
    except Exception:
        raise WorkerError(5, "DirectML OCR 模型加载或推理失败") from None


class LocalTranslator:
    def __init__(self, bundle):
        self.bundle = bundle
        self.api_key = secrets.token_urlsafe(24)
        self.device = None
        self.offloaded = 0
        self.process = None
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            self.port = listener.getsockname()[1]

    def __enter__(self):
        executable = self.bundle / "llama" / "llama-server.exe"
        model = self.bundle / "models" / "Hy-MT2-1.8B-Q4_K_M.gguf"
        command = [str(executable), "--model", str(model), "--alias", "hymt2",
                   "--host", "127.0.0.1", "--port", str(self.port), "--api-key", self.api_key,
                   "--offline", "--no-ui", "--no-context-shift", "--ctx-size", "2048", "--verbosity", "4",
                   "--parallel", "1", "--n-gpu-layers", "all", "--device", "Vulkan0", "--jinja"]
        try:
            self.process = subprocess.Popen(command, cwd=executable.parent, stdin=subprocess.DEVNULL,
                                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                            text=True, encoding="utf-8", errors="replace",
                                            creationflags=subprocess.CREATE_NO_WINDOW)
            threading.Thread(target=self._read_device_evidence, daemon=True).start()
            self._wait_until_ready()
            return self
        except BaseException:
            self._stop()
            raise

    def __exit__(self, *_):
        self._stop()

    def _read_device_evidence(self):
        for line in self.process.stdout:
            match = re.search(r"offloaded\s+(\d+)/\d+\s+layers to GPU", line, re.IGNORECASE)
            if match:
                self.offloaded = int(match.group(1))
            if "using device Vulkan0" in line:
                self.device = "Vulkan0"

    def _wait_until_ready(self):
        deadline = time.monotonic() + SERVER_STATUS_TIMEOUT
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                break
            try:
                if self._request("GET", "/health", timeout=1)[0] == 200 and self.device and self.offloaded:
                    return
            except PermissionError:
                raise WorkerError(6, "无法连接本地翻译回环服务") from None
            except (OSError, http.client.HTTPException):
                pass
            time.sleep(0.15)
        raise WorkerError(6, "本地翻译模型未能在 Vulkan GPU 上启动")

    def _request(self, method, path, body=None, timeout=TRANSLATION_TIMEOUT):
        # 固定回环地址，避免环境代理把文字或图像内容发送给其他服务。
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=timeout)
        try:
            headers = {"Authorization": f"Bearer {self.api_key}", "Content-Type": "application/json"}
            data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None
            connection.request(method, path, data, headers)
            response = connection.getresponse()
            return response.status, response.read(64_001)
        finally:
            connection.close()

    def translate(self, original):
        prompt = ("将以下日语漫画对白忠实翻译为简体中文，保留语气、措辞和标点。"
                  "只输出译文，不要额外解释。\n\n" + original)
        request = {"model": "hymt2", "messages": [{"role": "user", "content": prompt}],
                   "max_tokens": 256, "temperature": 0.15, "top_k": 20, "top_p": 0.6,
                   "min_p": 0.0, "repeat_penalty": 1.05, "stream": False}
        try:
            status, data = self._request("POST", "/v1/chat/completions", request)
            if status != 200 or len(data) > 64_000:
                raise WorkerError(6, "本地翻译模型返回无效响应")
            choice = json.loads(data)["choices"][0]
            content = choice["message"]["content"].strip()
            if not content or choice.get("finish_reason") == "length":
                raise WorkerError(6, "本地翻译结果为空或被截断")
            return content
        except (OSError, http.client.HTTPException, ValueError, KeyError, IndexError, AttributeError, TypeError):
            raise WorkerError(6, "本地翻译模型调用失败") from None

    def _stop(self):
        if self.process is None:
            return
        if self.process.poll() is None:
            with contextlib.suppress(OSError):
                self.process.terminate()
                try:
                    self.process.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(timeout=3)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", type=Path, required=True)
    parser.add_argument("--backend-dir", type=Path, required=True)
    parser.add_argument("--source", choices=["ja"], default="ja")
    parser.add_argument("--target", choices=["zh-Hans"], default="zh-Hans")
    parser.add_argument("--ocr-only", action="store_true")
    arguments = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="strict", newline="\n")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace", newline="\n")
    started = time.monotonic()
    try:
        bundle = arguments.backend_dir.resolve()
        ensure_bundle(bundle, arguments.ocr_only)
        try:
            image = read_image(arguments.image)
        except (ImportError, OSError):
            raise WorkerError(3, "本地图像解码依赖缺失") from None
        with tempfile.TemporaryDirectory(prefix="translation-", dir=arguments.image.resolve().parent) as profile_dir:
            regions, gpu_nodes = read_page(image, bundle / "models", Path(profile_dir))
        elapsed_ocr = round((time.monotonic() - started) * 1000)
        translation_backend = None
        offloaded_layers = 0
        if regions and not arguments.ocr_only:
            with LocalTranslator(bundle) as translator:
                for region in regions:
                    region["translatedText"] = translator.translate(region["sourceText"])
                translation_backend = translator.device
                offloaded_layers = translator.offloaded

        height, width = image.shape[:2]
        result = {"schemaVersion": 1, "width": width, "height": height, "ocrProvider": OCR_PROVIDER,
                  "translationBackend": translation_backend, "regions": regions,
                  "diagnostics": {"ocrGpuNodes": gpu_nodes, "translationGpuLayers": offloaded_layers,
                                  "ocrMilliseconds": elapsed_ocr,
                                  "totalMilliseconds": round((time.monotonic() - started) * 1000)}}
        print(json.dumps(result, ensure_ascii=False, separators=(",", ":")))
        return 0
    except WorkerError as error:
        print(error, file=sys.stderr)
        return error.code
    except (OSError, subprocess.SubprocessError):
        print("无法启动本地翻译进程或访问其工作目录", file=sys.stderr)
        return 6


if __name__ == "__main__":
    sys.exit(main())
