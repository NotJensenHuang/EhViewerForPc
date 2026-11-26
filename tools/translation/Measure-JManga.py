"""以作者固定版本的 CPU ONNX 后端评测 JMangaTranslator-Fast v1。"""

import argparse
import hashlib
import json
import runpy
import sys
from pathlib import Path
from time import perf_counter
from types import ModuleType

read_cases = runpy.run_path(str(Path(__file__).with_name("Measure-Seq2Seq.py")))["read_cases"]
REVISION = "151cebac786f262a9144fc109ac5341a94c3da12"
FILES = {
    "LICENSE": "0d9d283f516a63323e077517c9391342f4ae597fa255feb22f6ceaac3a0fb00a",
    "LICENSE-weights.md": "9ee6f7c873aaa7e8b23f05de57ef86aa708e9c7aaec3bb2fbc4d3fb1d6fe7e34",
    "config.json": "ab228ba6b618451afc89536757e27ea92b3e4cbb32590e8ef4e0d883ad072331",
    "jmt_fast/__init__.py": "1f85948ef47cd6e21e72b2ce91b80e104cd1710f28708b137b03e4476c5a956d",
    "jmt_fast/backends.py": "d56931fe17413305679c9e8c24b11910a0b37242739eebd9e6969ee69689bb55",
    "jmt_fast/common.py": "4cd5f769f8aa2e80d0cd77cc3c68cb85d6e355045a53b71ca04051844b054d15",
    "joint.model": "80fb4fe8bc69594428abcd022d3582a9baa55b5aa01afef4e112df6b0b6dab47",
    "onnx/decoder_fp32.onnx": "332f9091cfa4bf6ea9e0cc3957e9d0f9b637225b8af8edea5dd0f6fa96100e54",
    "onnx/encoder_fp32.onnx": "b75e8d96f17d824c71bb5ada0490363acc3951b522c745298aadaabc0a9edf92",
    "onnx/host_fp32.npz": "6a8a6031e7e2b5ef5710cccba53cd0f6ec5c8ba40ac75264e45abe6c03245a37",
    "tokenizer/tokenizer.json": "a560fa0ae86b3f7a9a8ce8a7897c0173f572ad40bbaddd5ef0b4ff4dee693483",
    "vocab.json": "3743a18281fa1a21a4b59edaa039dd56e55475beaa91aabec7b3d9eee4461e64",
}


def load_backend(root, threads):
    source = {}
    for relative, expected in FILES.items():
        path = root / relative
        with path.open("rb") as stream:
            if path.suffix == ".py":
                source[relative] = stream.read()
                digest = hashlib.sha256(source[relative]).hexdigest()
            else:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if digest != expected:
            raise ValueError(f"文件摘要不符：{path}")

    # 加载校验过的源字节，避免模型目录中的缓存字节码参与导入。
    package = ModuleType("jmt_fast")
    package.__path__ = []
    sys.modules[package.__name__] = package
    for name, file in (("common", "common.py"), ("backends", "backends.py")):
        module = ModuleType(f"jmt_fast.{name}")
        module.__package__ = package.__name__
        sys.modules[module.__name__] = module
        relative = f"jmt_fast/{file}"
        exec(compile(source[relative], str(root / relative), "exec"), module.__dict__)
    relative = "jmt_fast/__init__.py"
    exec(compile(source[relative], str(root / relative), "exec"), package.__dict__)
    backend = package.load(root, backend="onnx", threads=threads)
    backend.text.tok.no_truncation()
    return backend


def translate(backend, text, numpy):
    ids = backend.text.encode(text)
    if len(ids) > backend.Lmax:
        return "", "input_too_long"
    arrays = backend.enc.run(None, {"ids": numpy.asarray([ids], dtype=numpy.int64)})
    cross = dict(zip(("ck0", "cv0", "ck1", "cv1"), arrays, strict=True))
    tokens = backend.loop(cross, len(ids))
    translation = backend.text.decode(tokens)
    limit = min(3 * len(ids) + 10, backend.rel.cfg["max_tgt"], backend.loop.T + 1)
    if len(tokens) == limit:
        return translation, "truncated"
    return translation, "ok" if translation.strip() else "empty"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--cases", type=Path, default=Path(__file__).with_name("quality-cases.tsv"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--threads", type=int, default=6)
    args = parser.parse_args()
    if args.threads < 1:
        parser.error("--threads 必须是正整数")

    import numpy
    import onnxruntime

    cases = read_cases(args.cases)
    cases_sha256 = hashlib.sha256(args.cases.read_bytes()).hexdigest()
    backend = load_backend(args.checkpoint.resolve(strict=True), args.threads)
    completed = 0
    with args.output.open("x", encoding="utf-8", newline="\r\n") as output:
        for case in cases:
            status, translation, elapsed = "unsupported_direction", "", None
            if (case["source_lang"], case["target_lang"]) == ("ja", "zh-CN"):
                status = "unsupported_context"
                if case["context_mode"] == "single":
                    started = perf_counter()
                    translation, status = translate(backend, case["source"], numpy)
                    elapsed = round((perf_counter() - started) * 1000, 3)
                    completed += status == "ok"
            print(json.dumps({
                "case_id": case["case_id"], "model": "muscgab/JMangaTranslator-Fast",
                "revision": REVISION, "device": "cpu", "threads": args.threads,
                "onnxruntime": onnxruntime.__version__, "cases_sha256": cases_sha256,
                "source_lang": case["source_lang"], "target_lang": case["target_lang"],
                "context_mode": case["context_mode"], "output": translation,
                "status": status, "latency_ms": elapsed,
            }, ensure_ascii=False), file=output, flush=True)
    if completed == 0:
        raise SystemExit("没有成功翻译的样本。")


if __name__ == "__main__":
    main()
