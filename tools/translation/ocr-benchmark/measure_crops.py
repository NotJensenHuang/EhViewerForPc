"""Run local crop recognizers against the identical PNG crop directory."""
import argparse
import hashlib
import json
import os
import re
import time
from pathlib import Path

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"


# Author-published reference files at the fixed revisions documented in the OCR report.
BABERU_SOURCE = "64d42f4c94c4b2b609c996616e8c518e4c552988a14c5237eb10e6c656093e8e"
HAYAI_SOURCE = "9d53345fcecce1d1d35bf636c377419dcad1b833de5e6a777da4c1425f81c798"


def reference_runner(path, expected_hash, function):
    source = path.read_bytes()
    if hashlib.sha256(source).hexdigest() != expected_hash:
        raise ValueError(f"Reference code failed SHA-256 verification: {path}")
    scope = {"__name__": "pinned_ocr_reference", "__file__": str(path)}
    exec(compile(source, path, "exec"), scope)
    return scope[function]


def baberu(models):
    cls = reference_runner(models / "onnx_infer.py", BABERU_SOURCE, "BaberuOnnxOCR")
    return cls(models / "onnx", models / "tokenizer", vision="vision_int4.onnx")


def hayai(models):
    cls = reference_runner(models / "inference_onnx.py", HAYAI_SOURCE, "HayaiONNXPipeline")
    pipeline = cls(models, precision="int4", providers=["CPUExecutionProvider"], num_threads=1)
    return pipeline.ocr


def manga_ocr(models):
    import torch
    from tokenizers import Tokenizer
    from transformers import ViTImageProcessor, VisionEncoderDecoderModel

    torch.set_num_threads(1)
    processor = ViTImageProcessor.from_pretrained(models, local_files_only=True)
    tokenizer = Tokenizer.from_file(str(models / "tokenizer.json"))
    model = VisionEncoderDecoderModel.from_pretrained(
        models, local_files_only=True, use_safetensors=True).eval()

    @torch.inference_mode()
    def recognize(image):
        pixels = processor(image.convert("L").convert("RGB"), return_tensors="pt").pixel_values
        prediction = model.generate(pixels, max_length=300)[0].tolist()
        text = "".join(tokenizer.decode(prediction, skip_special_tokens=True).split())
        return re.sub(r"[・.]{2,}", lambda match: "." * len(match[0]), text.replace("…", "..."))

    return recognize


def ppocr(models):
    import numpy as np
    import onnxruntime as ort
    from PIL import Image

    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    options.inter_op_num_threads = 1
    session = ort.InferenceSession(str(models / "rec.onnx"), options, providers=["CPUExecutionProvider"])
    input_name = session.get_inputs()[0].name
    vocab = [""] + (models / "rec-dict.txt").read_text(encoding="utf-8").splitlines() + [" "]

    def recognize(image):
        if image.height > image.width:
            image = image.transpose(Image.Transpose.ROTATE_90)
        width = max(16, min(1536, round(48 * image.width / image.height)))
        resized = image.resize((width, 48), Image.Resampling.BILINEAR)
        bgr = np.asarray(resized, dtype=np.float32)[:, :, ::-1] / 127.5 - 1
        [values] = session.run(None, {input_name: bgr.transpose(2, 0, 1)[None]})
        if values.shape[2] != len(vocab):
            raise ValueError("Recognizer and dictionary mismatch")
        ids = np.argmax(values[0], axis=-1)
        return "".join(vocab[i] for position, i in enumerate(ids)
                       if i and (position == 0 or i != ids[position - 1])).strip()

    return recognize


def main():
    import sys
    from PIL import Image

    sys.stdout.reconfigure(encoding="utf-8")
    builders = {"baberu": baberu, "hayai": hayai, "manga-ocr": manga_ocr, "ppocr": ppocr}
    parser = argparse.ArgumentParser()
    parser.add_argument("candidate", choices=builders)
    parser.add_argument("model_directory", type=Path)
    parser.add_argument("crop_directory", type=Path)
    args = parser.parse_args()
    recognize = builders[args.candidate](args.model_directory.resolve())
    crops = sorted(args.crop_directory.glob("*.png"))
    if not crops:
        parser.error("The crop directory has no PNGs")
    for crop in crops:
        start = time.perf_counter()
        with Image.open(crop) as image:
            text = recognize(image.convert("RGB"))
        print(json.dumps({"File": crop.name, "Text": text,
                          "Milliseconds": (time.perf_counter() - start) * 1000}, ensure_ascii=False))


if __name__ == "__main__":
    main()
