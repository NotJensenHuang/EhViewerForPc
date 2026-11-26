"""以离线文本样本对照候选模型。主程序不依赖此工具。"""

import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
from time import perf_counter
from types import ModuleType

LANGUAGES = {
    "ja": ("ja", "jpn_Jpan", "ja"),
    "zh-CN": ("zh", "zho_Hans", "zh"),
    "zh-TW": ("zh", "zho_Hant", "zh"),
    "en": ("en", "eng_Latn", "en"),
    "ko": ("ko", "kor_Hang", "ko"),
    "fr": ("fr", "fra_Latn", "fr"),
    "de": ("de", "deu_Latn", "de"),
    "es": ("es", "spa_Latn", "es"),
}


def read_cases(path):
    required = {"case_id", "source_lang", "target_lang", "context_mode", "context", "source", "reference"}
    with path.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream, delimiter="\t")
        if not required.issubset(reader.fieldnames or []):
            raise ValueError("语料缺少必要的 TSV 列")
        cases = list(reader)
    if not cases:
        raise ValueError("语料没有样本")
    ids = set()
    for case in cases:
        case_id = case["case_id"]
        if None in case or any(case[key] is None for key in required):
            raise ValueError(f"语料行的列数错误：{case_id}")
        if not case_id or case_id in ids or not case["source"]:
            raise ValueError(f"缺少源文本或区域 ID 重复：{case_id}")
        if case["context_mode"] not in ("single", "page", "cross-page"):
            raise ValueError(f"未知上下文范围：{case_id}")
        ids.add(case_id)
    return cases


def route(family, case):
    source, target = case["source_lang"], case["target_lang"]
    if case["context_mode"] != "single":
        return None, "unsupported_context"
    if source not in LANGUAGES or target not in LANGUAGES or source == target:
        return None, "unsupported_direction"
    if family in ("opus-en-zh", "nano-sakura"):
        required_source = "en" if family == "opus-en-zh" else "ja"
        return ((source, target), None) if (source, target) == (required_source, "zh-CN") else (
            None, "unsupported_direction"
        )
    index = {"m2m100": 0, "nllb": 1, "madlad": 2}[family]
    if family != "nllb" and "zh-TW" in (source, target):
        return None, "unsupported_script"
    return (LANGUAGES[source][index], LANGUAGES[target][index]), None


def parameters(family, tokenizer, codes, source):
    source_code, target_code = codes
    options = {}
    if family in ("m2m100", "nllb"):
        tokenizer.src_lang = source_code
        options["forced_bos_token_id"] = (
            tokenizer.get_lang_id(target_code)
            if family == "m2m100"
            else tokenizer.convert_tokens_to_ids(target_code)
        )
        if options["forced_bos_token_id"] in (None, tokenizer.unk_token_id):
            raise ValueError(f"目标语言令牌不存在：{target_code}")
    elif family == "opus-en-zh":
        marker = ">>cmn_Hans<<"
        if marker not in tokenizer.supported_language_codes:
            raise ValueError("Marian 分词器缺少简体中文目标标记")
        source = f"{marker} {source}"
    elif family == "nano-sakura":
        source += "<eos>"
    elif family == "madlad":
        source = f"<2{target_code}> {source}"
    return source, options


def load_madlad_q4k(checkpoint, torch, transformers):
    import gguf

    path = Path(checkpoint) / "model-q4k.gguf"
    expected = "ea6e5531a3e95213c7f0635988d119e078a655c09306e47851e15d4c0c3f9c37"
    with path.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != expected:
            raise ValueError("MADLAD Q4K 权重与已核验发布件不匹配。")

    config = transformers.T5Config.from_pretrained(checkpoint, local_files_only=True)
    with torch.device("meta"):
        model = transformers.T5ForConditionalGeneration(config)
    model.generation_config = transformers.GenerationConfig.from_pretrained(
        checkpoint, local_files_only=True
    )
    reader = gguf.GGUFReader(path)
    parameters = dict(model.named_parameters(remove_duplicate=False))
    with torch.no_grad():
        for tensor in reader.tensors:
            name = tensor.name
            shape = parameters[name].shape
            if shape != tuple(int(x) for x in reversed(tensor.shape)):
                raise ValueError(f"MADLAD 张量形状与 T5 参数不匹配：{name}")
            host = torch.empty(shape, dtype=torch.bfloat16, device="cpu")
            rows = min(2048, shape[0])
            for first in range(0, shape[0], rows):
                last = min(first + rows, shape[0])
                weights = gguf.dequantize(tensor.data[first:last].copy(), tensor.tensor_type)
                host[first:last].copy_(torch.from_numpy(weights))
            owner, attribute = name.rsplit(".", 1)
            setattr(model.get_submodule(owner), attribute, torch.nn.Parameter(host, requires_grad=False))
        # 谷歌的 GGUF 发布件只保存一次共享的输入嵌入。
        embedding = model.decoder.embed_tokens.weight
        model.shared.weight = embedding
        model.encoder.embed_tokens.weight = embedding
    if any(parameter.is_meta for parameter in model.parameters()):
        raise ValueError("MADLAD Q4K 发布件缺少 T5 参数。")
    return model


def load_nanosakura(checkpoint, transformers):
    root = Path(checkpoint)
    path = root / "model.safetensors"
    expected = "0e6324344ad76994b607f45b2dc2d352bf0cb8d1f268b0fb6faed8b127d1764a"
    with path.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != expected:
            raise ValueError("NanoSakura 权重与已核验发布件不匹配。")

    source_path = root / "modeling_mamba2_s2s.py"
    source = source_path.read_bytes()
    expected_source = "cd5a7f766cf46c7d1b3cf06ec0c05d73211b62ba8288a860040df7c4529b7527"
    if hashlib.sha256(source).hexdigest() != expected_source:
        raise ValueError("NanoSakura 推理源码与审核版本不匹配。")

    data_digests = {
        "config.json": "a011bfb44b9039de277f601520bd86791df76749584cb0fa2cde1db70c57702c",
        "tokenizer.json": "8cb01fbff721c47daa66f433080b18b3ec7c866630da7dcc72ef3f4d14d25c3c",
    }
    for file, expected_digest in data_digests.items():
        if hashlib.sha256((root / file).read_bytes()).hexdigest() != expected_digest:
            raise ValueError(f"NanoSakura 配套文件与已核验发布件不匹配：{file}")

    # 使用已审核的源码字节和显式类，无需启用 Transformers 的远程代码自动加载。
    module = ModuleType("nanosakura_pinned")
    exec(compile(source, str(source_path), "exec"), module.__dict__)
    model = module.Mamba2Seq2SeqForConditionalGeneration.from_pretrained(
        checkpoint, local_files_only=True, use_safetensors=True
    )
    tokenizer = transformers.PreTrainedTokenizerFast(
        tokenizer_file=str(root / "tokenizer.json"), bos_token="<bos>", eos_token="<eos>",
        pad_token="<pad>", unk_token="<unk>",
    )
    if tokenizer.encode("<eos>", add_special_tokens=False) != [model.config.eos_token_id]:
        raise ValueError("NanoSakura 分词器的句尾标记与模型不匹配。")
    return model, tokenizer


def run(args):
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    import torch
    import transformers
    from transformers import AutoModelForSeq2SeqLM, AutoTokenizer

    cases = read_cases(args.cases)
    cases_sha256 = hashlib.sha256(args.cases.read_bytes()).hexdigest()
    torch.set_num_threads(args.threads)
    checkpoint = str(args.checkpoint.resolve(strict=True))
    model_options = dict(local_files_only=True, trust_remote_code=False)
    if args.family == "nano-sakura":
        model, tokenizer = load_nanosakura(checkpoint, transformers)
    else:
        tokenizer = AutoTokenizer.from_pretrained(checkpoint, **model_options)
        if args.madlad_q4k:
            model = load_madlad_q4k(checkpoint, torch, transformers)
        else:
            model = AutoModelForSeq2SeqLM.from_pretrained(
                checkpoint, use_safetensors=True, **model_options
            )
    model.eval()
    expected = {"opus-en-zh": "marian", "m2m100": "m2m_100", "nllb": "m2m_100", "madlad": "t5", "nano-sakura": "mamba2_s2s"}
    if model.config.model_type != expected[args.family]:
        raise ValueError(f"实际模型架构不匹配：{model.config.model_type}")
    dtype = str(next(model.parameters()).dtype)
    completed = 0
    with args.output.open("x", encoding="utf-8", newline="\r\n") as output, torch.inference_mode():
        for case in cases:
            codes, error = route(args.family, case)
            result = {
                "case_id": case["case_id"], "model": args.model_id, "revision": args.revision,
                "family": args.family, "device": "cpu", "threads": args.threads,
                "cases_sha256": cases_sha256, "dtype": dtype,
                "quantization": "Q4_K to bfloat16" if args.madlad_q4k else None,
                "torch": torch.__version__, "transformers": transformers.__version__,
                "max_new_tokens": args.max_new_tokens, "num_beams": args.num_beams,
                "source_lang": case["source_lang"], "target_lang": case["target_lang"],
                "context_mode": case["context_mode"], "output": "", "status": error or "ok",
            }
            if codes is not None:
                started = perf_counter()
                source, options = parameters(args.family, tokenizer, codes, case["source"])
                inputs = tokenizer(source, return_tensors="pt", return_token_type_ids=False)
                tokens = model.generate(
                    **inputs, **options, max_new_tokens=args.max_new_tokens, do_sample=False,
                    num_beams=args.num_beams, forced_eos_token_id=None,
                )[0]
                text = tokenizer.decode(tokens, skip_special_tokens=True).strip()
                eos = model.generation_config.eos_token_id
                endings = eos if isinstance(eos, list) else [eos]
                result.update(output=text, latency_ms=round((perf_counter() - started) * 1000, 3))
                if int(tokens[-1]) not in endings:
                    result["status"] = "truncated"
                elif not text:
                    result["status"] = "empty"
                else:
                    completed += 1
            print(json.dumps(result, ensure_ascii=False), file=output, flush=True)
    if completed == 0:
        raise SystemExit("没有成功翻译的样本，请检查语言方向和推理上限。")


def positive_int(value):
    number = int(value)
    if number < 1:
        raise argparse.ArgumentTypeError("请输入正整数")
    return number


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--family", choices=("opus-en-zh", "m2m100", "nllb", "madlad", "nano-sakura"), required=True)
    cli.add_argument("--model-id", required=True)
    cli.add_argument("--revision", required=True)
    cli.add_argument("--checkpoint", type=Path, required=True)
    cli.add_argument("--cases", type=Path, default=Path(__file__).with_name("quality-cases.tsv"))
    cli.add_argument("--output", type=Path, required=True)
    cli.add_argument("--max-new-tokens", type=positive_int, default=128)
    cli.add_argument("--num-beams", type=positive_int, default=4)
    cli.add_argument("--threads", type=positive_int, default=6)
    cli.add_argument("--madlad-q4k", action="store_true", help="将谷歌 Q4K 权重解量化到 CPU bfloat16")
    args = cli.parse_args()
    if args.madlad_q4k and args.family != "madlad":
        cli.error("--madlad-q4k 只适用于 --family madlad")
    run(args)


if __name__ == "__main__":
    main()
