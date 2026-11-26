"""将固定 OPUS-MT Flax 数据重建为同一 HF 快照的 Safetensors。"""

import argparse
import hashlib
from pathlib import Path

import msgpack
import numpy as np
import torch
from safetensors.torch import save_file
from transformers import MarianConfig, MarianMTModel

FLAX_SHA256 = "de82e23a535e25896ebdf433ec2b72706a428fb8e47bb87ef72531426edee8c3"
TARGET_SHA256 = "ff99aaf052d1db882cfbc1dc8dba7d0978a30b128409721a6f8b979532893034"
POSITIONS = ("model.encoder.embed_positions.weight", "model.decoder.embed_positions.weight")
TIED = {"lm_head.weight", "model.encoder.embed_tokens.weight", "model.decoder.embed_tokens.weight"}


def flatten(tree, keys=()):
    for key, value in tree.items():
        parts = (*keys, key)
        if isinstance(value, dict):
            yield from flatten(value, parts)
            continue
        if not isinstance(value, msgpack.ExtType) or value.code != 1:
            raise ValueError(f"Flax 张量未知：{parts}")
        shape, dtype, raw = msgpack.unpackb(value.data, raw=False)
        if dtype != "float32":
            raise ValueError(f"Flax 张量 dtype 未匹配：{parts}")
        array = np.frombuffer(raw, dtype="<f4").reshape(shape)
        if key == "kernel":
            array = array.T
        if key in ("kernel", "scale", "embedding"):
            parts = parts[:-1] + ("weight",)
        yield ".".join(parts), torch.from_numpy(np.array(array, order="C", copy=True))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("checkpoint", type=Path)
    args = parser.parse_args()
    root = args.checkpoint
    source = (root / "flax_model.msgpack").read_bytes()
    if hashlib.sha256(source).hexdigest() != FLAX_SHA256:
        raise ValueError("Flax 文件摘要与发布权重不符。")
    nested = msgpack.unpackb(source, raw=False, max_bin_len=len(source))
    del source
    weights = dict(flatten(nested))
    del nested

    config = MarianConfig.from_pretrained(root, local_files_only=True)
    layout = MarianMTModel(config).state_dict()
    if layout.keys() - weights.keys() != set(POSITIONS).union(TIED) or weights.keys() - layout.keys():
        raise ValueError("参数名称与 Marian 权重格式不匹配。")
    for key in POSITIONS:
        weights[key] = layout[key].clone()
    if any(tensor.shape != layout[key].shape for key, tensor in weights.items()):
        raise ValueError("参数形状与 Marian 权重格式不匹配。")

    target = root / "model.safetensors"
    temporary = target.with_name("model.safetensors.converted.part")
    if target.exists() or temporary.exists():
        raise FileExistsError(f"输出文件已存在：{target} 或 {temporary}")
    save_file(weights, temporary, metadata={"format": "pt"})
    with temporary.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != TARGET_SHA256:
            raise ValueError(f"重建文件完整摘要未匹配：{temporary}")
    temporary.rename(target)


if __name__ == "__main__":
    main()
