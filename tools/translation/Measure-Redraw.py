"""生成带干净底图和字形掩膜的重绘对照，输出各算法的像素误差。"""

import argparse
import json
from pathlib import Path

import cv2
import numpy as np


def make_page(complex_background):
    base = np.full((240, 400), 255, np.uint8)
    if complex_background:
        base[:] = np.linspace(140, 240, base.shape[1], dtype=np.uint8)
        for y in (50, 95, 145, 190):
            cv2.line(base, (0, y), (399, y + 24), 25, 2, cv2.LINE_AA)
        cv2.ellipse(base, (130, 125), (80, 72), 0, 0, 360, 42, 3, cv2.LINE_AA)
    else:
        cv2.ellipse(base, (200, 120), (130, 75), 0, 0, 360, 20, 3, cv2.LINE_AA)

    mask = np.zeros_like(base)
    cv2.putText(mask, "SFX!", (116, 128), cv2.FONT_HERSHEY_DUPLEX, 2, 255, 3, cv2.LINE_AA)
    image = np.rint(base * (1.0 - mask / 255.0)).astype(np.uint8)
    binary = cv2.dilate((mask > 0).astype(np.uint8) * 255, np.ones((3, 3), np.uint8))
    return base, image, binary


def metrics(base, result, mask):
    inside = mask > 0
    differences = cv2.absdiff(base, result)
    return {
        "mask_pixels": int(np.count_nonzero(inside)),
        "mask_mae": round(float(differences[inside].mean()), 3),
        "outside_changed_pixels": int(np.count_nonzero(differences[~inside])),
        "whole_page_mae": round(float(differences.mean()), 3),
    }


def measure(base, image, mask):
    ys, xs = np.nonzero(mask)
    rectangle = image.copy()
    rectangle[ys.min():ys.max() + 1, xs.min():xs.max() + 1] = 255
    return {
        "original_with_text": image,
        "rectangle": rectangle,
        "telea": cv2.inpaint(image, mask, 3, cv2.INPAINT_TELEA),
        "navier_stokes": cv2.inpaint(image, mask, 3, cv2.INPAINT_NS),
    }


def save_image(path, image):
    ok, encoded = cv2.imencode(".png", image)
    if not ok:
        raise OSError(f"无法编码 PNG：{path}")
    with path.open("xb") as stream:
        stream.write(encoded.tobytes())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True)
    report = {"opencv": cv2.__version__, "mask": "oracle-dilated-3x3", "radius": 3, "pages": {}}
    for name, complex_background in (("bubble", False), ("linework", True)):
        base, image, mask = make_page(complex_background)
        values = measure(base, image, mask)
        report["pages"][name] = {key: metrics(base, value, mask) for key, value in values.items()}
        for key, value in {"clean": base, "mask": mask, **values}.items():
            save_image(args.output / f"{name}-{key}.png", value)
    with (args.output / "report.json").open("x", encoding="utf-8", newline="\r\n") as stream:
        json.dump(report, stream, indent=2)
        print(file=stream)


if __name__ == "__main__":
    main()
