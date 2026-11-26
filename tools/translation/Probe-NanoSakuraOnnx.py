"""验证 NanoSakura 微型同结构的 ONNX 编码器和显式循环缓存。"""

import argparse
import hashlib
from pathlib import Path
from types import ModuleType
import onnxruntime
import torch

cli = argparse.ArgumentParser(description=__doc__)
cli.add_argument("reviewed_source", type=Path)
cli.add_argument("output", type=Path)
args = cli.parse_args()
src = args.reviewed_source
root = args.output
root.mkdir(parents=True)
data = src.read_bytes()
if hashlib.sha256(data).hexdigest() != "cd5a7f766cf46c7d1b3cf06ec0c05d73211b62ba8288a860040df7c4529b7527":
    raise ValueError("NanoSakura 推理源码与审核版本不匹配。")
module = ModuleType("nanosakura_pinned")
exec(compile(data, str(src), "exec"), module.__dict__)
torch.manual_seed(20261126)
config = module.Mamba2Seq2SeqConfig(vocab_size=16, d_model=16, num_heads=2, head_dim=8,
    enc_layers=1, dec_layers=1, d_state=4, ssm_head_dim=4, conv_kernel=4, decoder_start_token_id=1)
model = module.Mamba2Seq2SeqForConditionalGeneration(config).eval()


class Encode(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.encoder = model.encoder

    def forward(self, source, mask):
        return self.encoder(source, attention_mask=mask).last_hidden_state


class DecodeStep(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, token, memory, mask, conv, ssm):
        layer = self.model.decoder.layers[0].cross_attn
        k = layer.k_proj(memory).view(1, -1, layer.H, layer.Dh).transpose(1, 2)
        v = layer.v_proj(memory).view(1, -1, layer.H, layer.Dh).transpose(1, 2)
        past = ([(k, v)], [{"conv": conv, "ssm": ssm}])
        output = self.model(decoder_input_ids=token, attention_mask=mask, encoder_outputs=(memory,),
                            past_key_values=past, return_dict=True)
        states = output.past_key_values[1][0]
        return output.logits, states["conv"], states["ssm"]


def raw(tensor):
    return tensor.detach().numpy()


pt_encoder, pt_decoder = Encode(model), DecodeStep(model)
source = torch.tensor([[4, 6, 2]])
mask = torch.ones_like(source)
token = torch.tensor([[1]])
with torch.inference_mode():
    memory = pt_encoder(source, mask)
    layer = model.decoder.layers[0].ssm
    conv = torch.zeros((1, layer.K - 1, layer.conv_dim))
    ssm = torch.zeros((1, layer.Hh, layer.N, layer.P))
    encfile = root / "nanosakura-smoke-encoder.onnx"
    decfile = root / "nanosakura-smoke-decoder.onnx"
    # PyTorch 2.0 导出器缺少原子注意力算子，测试等价的基础算子图。
    import torch.nn.functional as F
    original_attention = F.scaled_dot_product_attention

    def attention(query, key, value, attn_mask=None):
        scores = torch.matmul(query, key.transpose(-1, -2)) * (config.head_dim ** -0.5)
        if attn_mask is not None:
            scores = scores.masked_fill(~attn_mask, float("-inf"))
        return torch.matmul(torch.softmax(scores, dim=-1), value)

    F.scaled_dot_product_attention = attention
    try:
        torch.onnx.export(
            pt_encoder, (source, mask), encfile, input_names=["source", "mask"],
            output_names=["memory"], opset_version=17,
            dynamic_axes={name: {1: "length"} for name in ("source", "mask", "memory")},
        )
        inames = ["token", "memory", "mask", "conv", "ssm"]
        torch.onnx.export(
            pt_decoder, (token, memory, mask, conv, ssm), decfile, input_names=inames,
            output_names=["logits", "next_conv", "next_ssm"], opset_version=17,
            dynamic_axes={name: {1: "length"} for name in ("memory", "mask")},
        )
    finally:
        F.scaled_dot_product_attention = original_attention
    session = onnxruntime.SessionOptions()
    session.intra_op_num_threads = 1
    enc = onnxruntime.InferenceSession(str(encfile), session, providers=["CPUExecutionProvider"])
    dec = onnxruntime.InferenceSession(str(decfile), session, providers=["CPUExecutionProvider"])
    for ids in ([[4, 6, 2]], [[5, 7, 8, 2]]):
        source = torch.tensor(ids)
        mask = torch.ones_like(source)
        memory = pt_encoder(source, mask)
        onnx_memory = enc.run(None, {"source":raw(source),"mask":raw(mask)})[0]
        torch.testing.assert_close(torch.from_numpy(onnx_memory), memory, rtol=1e-5, atol=1e-6)
        states, onnx_states = (conv,ssm), (raw(conv), raw(ssm))
        for token in (torch.tensor([[1]]), torch.tensor([[2]])):
            out = pt_decoder(token, memory, mask, *states)
            actual = dec.run(None,dict(zip(inames,(raw(token),onnx_memory,raw(mask),*onnx_states))))
            for left, right in zip(out,actual):
                torch.testing.assert_close(left, torch.from_numpy(right), rtol=1e-5, atol=1e-6)
            states, onnx_states = out[1:], actual[1:]
        print("ONNX CPU encoder + 2 cached decoder steps matched", source.shape, flush=True)