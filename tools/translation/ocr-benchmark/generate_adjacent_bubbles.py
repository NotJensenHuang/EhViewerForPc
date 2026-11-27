"""生成六张可独立核对的紧邻低对比气泡，不读取真实漫画。"""
import argparse
import os
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("destination", type=Path)
    args = cli.parse_args()
    args.destination.mkdir(parents=True, exist_ok=True)
    font_path = Path(os.environ["WINDIR"]) / "Fonts/YuGothM.ttc"
    font = ImageFont.truetype(str(font_path), 48)
    for gap in (2, 4, 8):
        for stroke in (140, 175):
            canvas = Image.new("RGB", (1130, 1598), (200, 195, 185))
            paint = ImageDraw.Draw(canvas)
            for left, right in ((132, 274), (274 + gap, 412 + gap)):
                paint.ellipse((left * 2, 244, right * 2, 680), fill=(252, 249, 243),
                              outline=(stroke, stroke, stroke), width=3)
            for x, text in ((244, "別の声です"), (274 + gap + 6, "隣の声です")):
                for i, char in enumerate(text):
                    paint.text((x * 2, (159 + i * 25) * 2), char, font=font, fill="#242424")
            canvas.save(args.destination / f"gap{gap}-stroke{stroke}.png")


if __name__ == "__main__":
    main()
