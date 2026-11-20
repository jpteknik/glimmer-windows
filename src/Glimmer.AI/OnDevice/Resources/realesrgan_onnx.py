#!/usr/bin/env python3
"""Converts Real-ESRGAN's compact general model (realesr-general-x4v3, BSD-3-Clause,
https://github.com/xinntao/Real-ESRGAN) to ONNX for Glimmer's on-device upscale on Windows.

New for the Windows port; the counterpart of the Mac's scripts/convert-models/realesrgan.py (same
network definition, ONNX instead of Core ML).

    python3 realesrgan_onnx.py <realesr-general-x4v3.pth> <output dir> [<golden json>]

Needs torch, onnx, onnxruntime and numpy. Writes RealESRGAN.onnx: input "input" (1×3×H×W RGB, 0…1),
output "output" (1×3×4H×4W), H and W free (Glimmer runs it on 256 px tiles). The weights are stored as
float16, as in the Mac's model, with a Cast to float32 in front of every use: ONNX Runtime folds the casts
once when it loads the model, so the network itself runs in float32.

Checks, printed at the end: the ONNX model (run by ONNX Runtime) against the PyTorch model with the
original float32 weights, on a random 256 px tile and on the formula picture the C# test uses. With a
third argument, writes that picture's PyTorch output (sampled) as the C# test's golden values.
"""
import json
import sys

import numpy as np
import onnx
import onnxruntime as ort
import torch
import torch.nn.functional as F
from onnx import helper, numpy_helper
from torch import nn


class SRVGGNetCompact(nn.Module):
    """The architecture from Real-ESRGAN (realesrgan/archs/srvgg_arch.py)."""

    def __init__(self, num_in_ch=3, num_out_ch=3, num_feat=64, num_conv=32, upscale=4):
        super().__init__()
        self.upscale = upscale
        self.body = nn.ModuleList()
        self.body.append(nn.Conv2d(num_in_ch, num_feat, 3, 1, 1))
        self.body.append(nn.PReLU(num_parameters=num_feat))
        for _ in range(num_conv):
            self.body.append(nn.Conv2d(num_feat, num_feat, 3, 1, 1))
            self.body.append(nn.PReLU(num_parameters=num_feat))
        self.body.append(nn.Conv2d(num_feat, num_out_ch * upscale * upscale, 3, 1, 1))
        self.upsampler = nn.PixelShuffle(upscale)

    def forward(self, x):
        out = x
        for layer in self.body:
            out = layer(out)
        out = self.upsampler(out)
        return out + F.interpolate(x, scale_factor=self.upscale, mode="nearest")


def formula_picture(w, h):
    """The C# test builds the same picture: rgb(x, y, c) = 0.5 + 0.4 sin(0.37 x + 0.23 y + 2.1 c), plus a
    hard-edged square, so the network sees both smooth gradients and an edge."""
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    img = np.zeros((3, h, w), dtype=np.float64)
    for c in range(3):
        img[c] = 0.5 + 0.4 * np.sin(0.37 * x + 0.23 * y + 2.1 * c)
    square = (x >= 12) & (x < 22) & (y >= 6) & (y < 16)
    for c, v in enumerate((0.9, 0.12, 0.16)):
        img[c][square] = v
    return img.astype(np.float32)


def to_half_weights(model_path):
    """float32 initializers → float16 initializers, each read through a Cast back to float32."""
    model = onnx.load(model_path)
    graph = model.graph
    casts = []
    replaced = {}
    for init in list(graph.initializer):
        if init.data_type != onnx.TensorProto.FLOAT:
            continue
        values = numpy_helper.to_array(init)
        if values.size < 16:  # shapes and scales stay as they are
            continue
        half_name = init.name + "_fp16"
        graph.initializer.remove(init)
        graph.initializer.append(numpy_helper.from_array(values.astype(np.float16), half_name))
        casts.append(helper.make_node("Cast", [half_name], [init.name], to=onnx.TensorProto.FLOAT, name=init.name + "_cast"))
        replaced[init.name] = half_name
    for node in reversed(casts):
        graph.node.insert(0, node)
    onnx.checker.check_model(model)
    return model, len(replaced)


def main():
    weights, out_dir = sys.argv[1], sys.argv[2]
    golden = sys.argv[3] if len(sys.argv) > 3 else None
    torch.manual_seed(0)
    model = SRVGGNetCompact()
    state = torch.load(weights, map_location="cpu", weights_only=True)
    model.load_state_dict(state.get("params_ema", state.get("params", state)))
    model.eval()

    example = torch.rand(1, 3, 64, 64)
    fp32_path = f"{out_dir}/RealESRGAN-fp32.onnx"
    torch.onnx.export(model, (example,), fp32_path, input_names=["input"], output_names=["output"], opset_version=17,
                      dynamic_axes={"input": {2: "height", 3: "width"}, "output": {2: "height4", 3: "width4"}}, dynamo=False)
    half, count = to_half_weights(fp32_path)
    half.doc_string = ("Real-ESRGAN realesr-general-x4v3 (BSD-3-Clause, Xintao Wang, github.com/xinntao/Real-ESRGAN v0.2.5.0), "
                       "converted for Glimmer: 4x upscale, input 1x3xHxW RGB 0..1, float16 weights computed in float32.")
    onnx.save(half, f"{out_dir}/RealESRGAN.onnx")
    print(f"{count} weight tensors stored as float16")

    def compare(name, x):
        with torch.no_grad():
            expected = model(torch.from_numpy(x)[None]).numpy()
        for label, path in (("float32 weights", fp32_path), ("float16 weights (bundled)", f"{out_dir}/RealESRGAN.onnx")):
            session = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
            got = session.run(["output"], {"input": x[None]})[0]
            d = np.abs(expected - got)
            print(f"{name}, {label}: max |ONNX Runtime - PyTorch| = {d.max():.6f}, mean = {d.mean():.7f} (output range {expected.min():.3f}…{expected.max():.3f})")
        return expected[0]

    tile = torch.rand(3, 256, 256).numpy()
    compare("random 256 px tile", tile)
    picture = formula_picture(40, 24)
    expected = compare("formula picture 40×24", picture)

    if golden:
        # Sampled output values: 1500 positions from a fixed linear congruential sequence.
        c, h, w = expected.shape
        seed = 12345
        samples = []
        for _ in range(1500):
            seed = (seed * 1103515245 + 12345) % (1 << 31)
            i = seed % (c * h * w)
            samples.append([int(i), float(expected.reshape(-1)[i])])
        with open(golden, "w") as f:
            json.dump({
                "about": "PyTorch output of SRVGGNetCompact with the original realesr-general-x4v3 float32 weights, "
                         "for the formula picture of realesrgan_onnx.py (40x24, sampled). Made by "
                         "src/Glimmer.AI/OnDevice/Resources/realesrgan_onnx.py.",
                "width": 40, "height": 24, "outputShape": [c, h, w],
                "mean": [float(expected[k].mean()) for k in range(c)],
                "samples": samples,
            }, f, indent=0)
        print(f"wrote {golden}")


if __name__ == "__main__":
    main()
