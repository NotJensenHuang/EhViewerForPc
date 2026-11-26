"""Generate annotated synthetic pages and score local OCR results."""
import argparse
import json
import math
import os
import statistics
import unicodedata
from pathlib import Path

CASES = (
    ("horizontal", "meiryo.ttc", 36, "扉を開けないで", 0),
    ("vertical", "YuGothM.ttc", 33, "まだここにいる", 0),
    ("sfx", "meiryob.ttc", 62, "ドキッ", 0),
    ("furigana", "YuGothM.ttc", 15, "こもれび", 0),
    ("small", "msgothic.ttc", 14, "続きは明日ね", 0),
    ("inclined", "meiryo.ttc", 42, "田中さん", 22),
    ("italic", "simkai.ttf", 38, "もう遅いよ", -9),
    ("mixed", "msyh.ttc", 35, "你好カフェABC", 0),
    ("external", "YuGothB.ttc", 24, "ここまでです", 0),
    ("english", "segoeui.ttf", 35, "Listen to me", 0),
    ("korean", "malgun.ttf", 35, "조금 더 기다려", 0),
    ("chinese", "msyh.ttc", 35, "出发的时候到了", 0),
    ("french", "segoeui.ttf", 37, "À tout à l'heure", 0),
    ("reverse", "meiryo.ttc", 36, "また会えたね", 0),
    ("low-contrast", "YuGothM.ttc", 25, "どうしたの", 0),
)


def generate(root):
    from PIL import Image, ImageDraw, ImageFont

    pages = root / "pages"
    crops = root / "crops"
    pages.mkdir(parents=True, exist_ok=True)
    crops.mkdir(exist_ok=True)
    fonts = Path(os.environ["WINDIR"]) / "Fonts"
    truth = {}
    for page in range(20):
        canvas = Image.new("RGB", (1360, 1040), "white")
        regions = []
        for i, (kind, font_name, size, sentence, angle) in enumerate(CASES):
            ink = "white" if kind == "reverse" else "#999999" if kind == "low-contrast" else "black"
            bg = "#292929" if kind == "reverse" else "#dddddd" if kind == "low-contrast" else "white"
            suffix = str(page + 1) if kind in {"english", "mixed", "chinese"} else ("ね", "よ", "な", "")[page % 4] if kind in {"horizontal", "vertical", "external", "low-contrast"} else ""
            sentence += suffix
            font_size = size + ((page // 4) - 2)
            font = ImageFont.truetype(str(fonts / font_name), font_size)
            glyphs = Image.new("RGB", (400, 180), bg)
            drawing = ImageDraw.Draw(glyphs)
            if kind == "vertical":
                glyphs = Image.new("RGB", (95, 380), bg)
                drawing = ImageDraw.Draw(glyphs)
                for j, character in enumerate(sentence):
                    drawing.text((15, 12 + j * (font_size + 2)), character, font=font, fill=ink)
            else:
                drawing.text((15, 28), sentence, font=font, fill=ink)
            if angle:
                from PIL import ImageChops
                content = ImageChops.difference(glyphs, Image.new("RGB", glyphs.size, bg)).getbbox()
                glyphs = glyphs.crop((content[0] - 5, content[1] - 5, content[2] + 5, content[3] + 5))
                glyphs = glyphs.rotate(angle, expand=True, resample=Image.Resampling.BICUBIC, fillcolor=bg)
            from PIL import ImageChops
            bbox = ImageChops.difference(glyphs, Image.new("RGB", glyphs.size, bg)).getbbox()
            tight = glyphs.crop((bbox[0] - 5, bbox[1] - 5, bbox[2] + 5, bbox[3] + 5))
            # Use a separate tall column for the vertical region to avoid overlap.
            if kind == "vertical":
                x, y = 1240 + page % 3, 48
            else:
                j = i - (i > 1)
                x = 25 + (j % 3) * 397 + (page % 3) * 7
                y = 25 + (j // 3) * 201 + (page % 4) * 9
            if kind == "horizontal":
                bubble = ImageDraw.Draw(canvas)
                bubble.rounded_rectangle((x - 16, y - 12, x + tight.width + 16, y + tight.height + 12), 18, outline="black", width=2)
            canvas.paste(tight, (x, y))
            file = f"{page + 1:02}-{i + 1:02}.png"
            tight.save(crops / file)
            regions.append({
                "file": file,
                "text": sentence,
                "kind": kind,
                "direction": 1 if kind == "vertical" else 2 if kind == "inclined" else 0,
                "box": [x + 5, y + 5, x + tight.width - 5, y + tight.height - 5],
            })
        name = f"{page + 1:02}.png"
        canvas.save(pages / name)
        truth[name] = regions
    (root / "truth.json").write_text(json.dumps(truth, ensure_ascii=False, indent=2), encoding="utf-8")


def intersection_over_union(a, b):
    intersection = max(0, min(a[2], b[2]) - max(a[0], b[0])) * max(0, min(a[3], b[3]) - max(a[1], b[1]))
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - intersection
    return intersection / union if union else 0


def iou_target(observation, target):
    points = observation["Polygon"]
    xs, ys = [p["X"] for p in points], [p["Y"] for p in points]
    return intersection_over_union((min(xs), min(ys), max(xs) + 1, max(ys) + 1), target["box"])


def normalized(text):
    return unicodedata.normalize("NFKC", text).strip()


def distance(a, b):
    row = list(range(len(b) + 1))
    for i, ac in enumerate(a, 1):
        next_row = [i]
        for j, bc in enumerate(b, 1):
            next_row.append(min(row[j] + 1, next_row[-1] + 1, row[j - 1] + (ac != bc)))
        row = next_row
    return row[-1]


def score(truth_path, predictions_path, crops):
    truth = json.loads(truth_path.read_text(encoding="utf-8"))
    results = {}
    for line in predictions_path.read_text(encoding="utf-8-sig").splitlines():
        result = json.loads(line)
        if result["File"] in results:
            raise ValueError(f"Duplicate prediction: {result['File']}")
        results[result["File"]] = result

    rows = []
    false_positives = 0
    times = []
    for file, targets in truth.items():
        if crops:
            for target in targets:
                result = results[target["file"]]
                rows.append((target, result["Text"], None, True, False))
                times.append(result["Milliseconds"])
            continue

        result = results[file]
        observations = result["Result"]["Observations"]
        times.append(result["Milliseconds"])
        candidates = sorted(
            ((iou_target(o, t), i, j) for i, o in enumerate(observations) for j, t in enumerate(targets)),
            reverse=True,
        )
        matched_observations, matched_targets = set(), set()
        for ratio, i, j in candidates:
            if ratio < 0.5:
                break
            if i in matched_observations or j in matched_targets:
                continue
            o = observations[i]
            rows.append((targets[j], o["FinalText"], o["OriginalText"], True, o["RequiresReview"]))
            rows[-1][0]["directionMatch"] = o["Direction"] == targets[j]["direction"]
            matched_observations.add(i)
            matched_targets.add(j)
        false_positives += len(observations) - len(matched_observations)
        rows.extend((t, "", None, False, False) for j, t in enumerate(targets) if j not in matched_targets)

    def summarize(group):
        matches = [row for row in group if row[3]]
        edits = lambda row, text: distance(normalized(row[0]["text"]), normalized(text))
        count_chars = lambda entries: sum(len(normalized(row[0]["text"])) for row in entries)
        chars = count_chars(matches)
        auto_exact = sum(not review and normalized(t["text"]) == normalized(s) for t, s, _, hit, review in matches)
        return {
            "regions": len(group),
            "matched": len(matches),
            "matchedCer": sum(edits(row, row[1]) for row in matches) / chars if chars else None,
            "firstPassCer": (sum(edits(row, row[2]) for row in matches) / chars
                             if chars and not crops else None),
            "matchedWer": (sum(distance(normalized(row[0]["text"]).split(), normalized(row[1]).split()) for row in matches)
                           / sum(len(normalized(row[0]["text"]).split()) for row in matches) if chars else None),
            "pageCer": sum(edits(row, "" if row[4] else row[1]) for row in group) / count_chars(group),
            "exact": auto_exact,
            "requiresReview": sum(row[4] for row in group),
            "directionsCorrect": sum(row[0].get("directionMatch", False) for row in matches) if not crops else None,
        }

    aggregate = summarize(rows)
    aggregate["detectionRecall"] = aggregate["matched"] / len(rows) if not crops else None
    aggregate["detectionPrecision"] = (aggregate["matched"] / (aggregate["matched"] + false_positives)
                                       if aggregate["matched"] + false_positives and not crops else None)
    aggregate["falsePositives"] = false_positives if not crops else None
    kinds = sorted(set(row[0]["kind"] for row in rows))
    metrics = {
        "all": aggregate,
        "kinds": {kind: summarize([row for row in rows if row[0]["kind"] == kind]) for kind in kinds},
        "timingsMs": {"first": times[0], "mean": statistics.mean(times), "p50": statistics.median(times),
                      "p95": sorted(times)[math.ceil(len(times) * 0.95) - 1]},
    }
    print(json.dumps(metrics, indent=2))


def main():
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)
    generator = commands.add_parser("generate")
    generator.add_argument("output", type=Path)
    scorer = commands.add_parser("score")
    scorer.add_argument("truth", type=Path)
    scorer.add_argument("results", type=Path)
    scorer.add_argument("--crops", action="store_true")
    args = parser.parse_args()
    if args.command == "generate":
        generate(args.output)
    else:
        score(args.truth, args.results, args.crops)


if __name__ == "__main__":
    main()
