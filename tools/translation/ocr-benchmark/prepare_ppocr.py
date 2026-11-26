"""Prepare the pinned PP-OCRv6 model layout for the C# experiment."""
import argparse
import shutil
from pathlib import Path


def wrap_half_interface(source, destination):
    import onnx
    from onnx import TensorProto, helper

    model = onnx.load(source)
    graph = model.graph
    [input_value] = graph.input
    [output_value] = graph.output
    if any(value.type.tensor_type.elem_type != TensorProto.FLOAT16 for value in (input_value, output_value)):
        raise ValueError("Unexpected PP-OCRv6 FP16 model interface")
    input_name, output_name = input_value.name, output_value.name
    input_value.type.tensor_type.elem_type = TensorProto.FLOAT
    output_value.type.tensor_type.elem_type = TensorProto.FLOAT
    for node in graph.node:
        for i, name in enumerate(node.input):
            if name == input_name:
                node.input[i] = "benchmark_input_fp16"
            elif name == output_name:
                node.input[i] = "benchmark_output_fp16"
        for i, name in enumerate(node.output):
            if name == output_name:
                node.output[i] = "benchmark_output_fp16"
    graph.node.insert(0, helper.make_node(
        "Cast", [input_name], ["benchmark_input_fp16"], to=TensorProto.FLOAT16))
    graph.node.append(helper.make_node(
        "Cast", ["benchmark_output_fp16"], [output_name], to=TensorProto.FLOAT))
    # Nodes and weights within the published graph remain in their original types.
    onnx.checker.check_model(model)
    onnx.save(model, destination)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("candidate", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--half-recognizer", action="store_true")
    parser.add_argument("--baseline-recognizer", type=Path)
    args = parser.parse_args()
    if args.half_recognizer and args.baseline_recognizer:
        parser.error("Select at most one recognizer")
    args.output.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(args.candidate / "det/manga_det.onnx", args.output / "det.onnx")
    if args.baseline_recognizer:
        source, dictionary = args.baseline_recognizer / "rec.onnx", args.baseline_recognizer / "rec-dict.txt"
    else:
        source, dictionary = args.candidate / "rec/manga_rec.onnx", args.candidate / "ppocrv6_dict.txt"
    if args.half_recognizer:
        wrap_half_interface(args.candidate / "rec/manga_rec_fp16.onnx", args.output / "rec.onnx")
    else:
        shutil.copyfile(source, args.output / "rec.onnx")
    shutil.copyfile(dictionary, args.output / "rec-dict.txt")


if __name__ == "__main__":
    main()
