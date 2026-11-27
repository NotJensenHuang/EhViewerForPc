"""用 BubblePop 气泡实例给现有 OCR 的文字列分组，原图只读。"""

import argparse
import hashlib
import json
from pathlib import Path
from time import perf_counter

import cv2
import numpy as np
import onnxruntime as ort

DIGEST = "6a3973677f55b4cac1618714e0565af5f41c9a2158068353abd98e115de4e319"


def bbox(points):
    points = np.asarray(points)
    return [round(float(x), 2) for x in [*points.min(axis=0), *points.max(axis=0)]]


def mask_support(labels, polygon):
    shape = np.zeros(labels.shape, dtype=np.uint8)
    cv2.fillPoly(shape, [np.rint(polygon).astype(np.int32)], 1)
    count = int(shape.sum())
    if not count:
        return {}
    histogram = np.bincount(labels[shape != 0])
    return {int(label): round(float(overlap) / count, 4)
            for label, overlap in enumerate(histogram) if label and overlap}


def assignments(labels, bubbles, baseline):
    result = baseline["Result"]
    found = {}
    details = []
    for item in result["RawDetections"]:
        polygon = np.array([[point["X"], point["Y"]] for point in item["Polygon"]])
        x1, y1, x2, y2 = bbox(polygon)
        supports = mask_support(labels, polygon)
        matches = [(label, ratio) for label, ratio in supports.items() if label in bubbles and ratio >= 0.5]
        chosen = max(matches, key=lambda pair: pair[1], default=(None, 0))[0]
        details.append({"id": item["Id"], "box": [x1, y1, x2, y2],
                        "bubble": chosen, "mask_support": supports})
        if chosen is not None:
            found.setdefault(chosen, []).append(((x1 + x2) / 2, item["Id"]))

    groups = []
    observations = {item["Id"]: item for item in result["Observations"]}
    for label, columns in sorted(found.items()):
        # 此处仅演示检测到的日文竖排列，混排与整页气泡次序需另行判定。
        ids = [i for _, i in sorted(columns, reverse=True)
               if observations.get(i, {}).get("Direction") == 1]
        if ids:
            groups.append({"bubble": label, "vertical_right_to_left": ids})
    return {"existing_region_source_ids": [r["SourceRegionIds"] for r in result["Regions"]],
            "columns": details, "vertical_groups": groups}


def evaluate(model, source, baseline):
    with model.open("rb") as stream:
        checksum = hashlib.file_digest(stream, "sha256").hexdigest()
    if checksum != DIGEST:
        raise ValueError("BubblePop ONNX SHA-256 与已验证模型不匹配")
    width, height = 768, 1024
    gray = cv2.imdecode(np.frombuffer(source.read_bytes(), dtype=np.uint8), cv2.IMREAD_GRAYSCALE)
    if gray is None:
        raise ValueError("无法解码本地图片")
    h, w = gray.shape
    if [w, h] != [baseline["Result"]["Width"], baseline["Result"]["Height"]]:
        raise ValueError("OCR 基线与图片尺寸不同")
    scale = min(width / w, height / h)
    rw, rh = max(1, round(w * scale)), max(1, round(h * scale))
    left, top = (width - rw) // 2, (height - rh) // 2
    tensor = np.full((height, width), 255, dtype=np.uint8)
    tensor[top:top + rh, left:left + rw] = cv2.resize(
        gray, (rw, rh), interpolation=cv2.INTER_AREA if scale < 1 else cv2.INTER_LINEAR)
    tensor = ((tensor.astype(np.float32) / 255 - np.float32(0.449)) / np.float32(0.226))[None, :, :, None]
    options = ort.SessionOptions()
    options.intra_op_num_threads = 6
    started = perf_counter()
    session = ort.InferenceSession(str(model), options, providers=["CPUExecutionProvider"])
    load_ms = round((perf_counter() - started) * 1000, 3)
    times = []
    for _ in range(3):
        started = perf_counter()
        output = session.run(None, {session.get_inputs()[0].name: tensor})[0]
        times.append(round((perf_counter() - started) * 1000, 3))
    if output.shape != (1, height, width, 3):
        raise ValueError(f"BubblePop 输出形状不符：{output.shape}")
    logits = output[0, top:top + rh, left:left + rw, :]
    probability = 1 / (1 + np.exp(-np.clip(logits, -60, 60)))
    mask, score = (cv2.resize(probability[:, :, i], (w, h)) for i in (0, 2))
    n, labels, stats, _ = cv2.connectedComponentsWithStats(np.uint8(mask >= 0.5), connectivity=8)
    bubbles = {}
    for label in range(1, n):
        x, y, bw, bh, area = map(int, stats[label])
        if area < 60:
            continue
        region = labels == label
        confidence = float((score[region].mean() + mask[region].mean()) / 2)
        if confidence >= 0.6:
            bubbles[label] = {"bounds": [x, y, x + bw, y + bh], "area": area,
                              "confidence": round(confidence, 4)}
    return {"image_size": [w, h], "sha256": checksum, "bytes": model.stat().st_size,
            "ort_version": ort.__version__, "provider": session.get_providers()[0], "load_ms": load_ms,
            "inference_ms": times, "bubbles": bubbles, **assignments(labels, bubbles, baseline)}


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("model", type=Path)
    cli.add_argument("source", type=Path)
    cli.add_argument("baseline", type=Path)
    cli.add_argument("output", type=Path)
    args = cli.parse_args()
    baseline = json.loads(args.baseline.read_text(encoding="utf-8-sig").splitlines()[0])
    report = evaluate(args.model, args.source, baseline)
    with args.output.open("x", encoding="utf-8", newline="\r\n") as output:
        json.dump(report, output, ensure_ascii=False, indent=2)
        print(file=output)


if __name__ == "__main__":
    main()
