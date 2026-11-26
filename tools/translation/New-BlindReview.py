"""从同批候选的 JSONL 结果生成匿名人工评审表。"""

import argparse
import csv
import json
import random
import runpy
from pathlib import Path

read_cases = runpy.run_path(str(Path(__file__).with_name("Measure-Seq2Seq.py")))["read_cases"]
RATINGS = (
    "meaning_25", "context_20", "naturalness_15", "voice_10",
    "terminology_10", "adult_context_10", "sfx_5", "omission_addition_5", "review_notes",
)


def read_results(path, case_ids):
    results = {}
    with path.open(encoding="utf-8-sig") as stream:
        for line in stream:
            row = json.loads(line)
            case_id = row["case_id"]
            if case_id in results or case_id not in case_ids:
                raise ValueError(f"{path} 中样本 ID 重复或未知：{case_id}")
            results[case_id] = row
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cases", type=Path, default=Path(__file__).with_name("quality-cases.tsv"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--seed", type=int, required=True)
    parser.add_argument("results", nargs="+", type=Path)
    args = parser.parse_args()

    cases = read_cases(args.cases)
    case_ids = {row["case_id"] for row in cases}
    candidate_results = [(path, read_results(path, case_ids)) for path in args.results]
    rng = random.Random(args.seed)
    args.output.mkdir(parents=True)
    with (args.output / "review.tsv").open("x", encoding="utf-8-sig", newline="") as review, (
        args.output / "private-key.jsonl"
    ).open("x", encoding="utf-8", newline="\r\n") as key:
        fields = tuple(cases[0]) + ("blind_id", "status", "output") + RATINGS
        writer = csv.DictWriter(review, fieldnames=fields, delimiter="\t")
        writer.writeheader()
        for case in cases:
            case_id = case["case_id"]
            shuffled = candidate_results.copy()
            rng.shuffle(shuffled)
            for i, (path, results) in enumerate(shuffled, 1):
                result = results.get(case_id, {})
                blind_id = f"{case_id}/{i}"
                writer.writerow({
                    **case, "blind_id": blind_id, "status": result.get("status", "missing"),
                    "output": result.get("output", ""),
                })
                print(json.dumps({
                    "blind_id": blind_id, "file": str(path), "model": result.get("model") or result.get("model_id"),
                    "revision": result.get("revision"),
                }, ensure_ascii=False), file=key)


if __name__ == "__main__":
    main()
