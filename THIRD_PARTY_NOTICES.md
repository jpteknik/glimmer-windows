# Third-party notices

Glimmer for Windows is a port of [Glimmer Native](https://github.com/fltman/glimmer-native) by Anders Bjarby
(MIT, see `LICENSE`). Everything it ships or downloads is permissively licensed.

## Code and libraries in the app

| Component | Licence | Where |
|---|---|---|
| Glimmer Native (Swift original this port is based on, commit `6d3ef0c`) | MIT, © 2026 Anders Bjarby | the whole port |
| LZFSE reference implementation, https://github.com/lzfse/lzfse (commit `e634ca5`) | BSD-3-Clause, © 2015–2016 Apple Inc. | `src/Glimmer.Lzfse` (ported to C#; notice in each file) |
| SkiaSharp / HarfBuzzSharp (.NET bindings) | MIT, © Microsoft Corporation | NuGet packages |
| Skia | BSD-3-Clause, © Google LLC | inside SkiaSharp's native library |
| HarfBuzz | MIT ("Old MIT"), © HarfBuzz authors | inside HarfBuzzSharp's native library |
| ONNX Runtime (`Microsoft.ML.OnnxRuntime`) | MIT, © Microsoft Corporation | NuGet package |
| ONNX Runtime WebGPU execution provider (`Microsoft.ML.OnnxRuntime.EP.WebGpu` 0.6.0), Real-ESRGAN on the graphics card | MIT, © Microsoft Corporation | NuGet package; `onnxruntime_providers_webgpu.dll` in the app |
| Dawn and Tint (WebGPU and its shader compiler) | BSD-3-Clause, © 2017–2023 The Dawn & Tint Authors | inside `onnxruntime_providers_webgpu.dll`; listed in the package's `ThirdPartyNotices.txt` (byte for byte ONNX Runtime 1.32.0's) |
| DirectX Shader Compiler (`dxcompiler.dll` 1.8.2502.8, commit `b4711839e`) | University of Illinois/NCSA Open Source License (the LLVM Release License), © 2003–2015 University of Illinois at Urbana-Champaign, with the third-party code its `LICENSE.TXT` names under their own notices (miniz, Unicode's ConvertUTF, an MD5 placed in the public domain, strlcpy); the GPL-2 and other entries there are marked build and test dependencies only | in the WebGPU package (Windows). The package's notices don't list it; the licence is `LICENSE.TXT` of github.com/microsoft/DirectXShaderCompiler at that commit |
| System.Numerics.Tensors | MIT, © .NET Foundation | NuGet package |
| .NET runtime and WPF (self-contained build) | MIT, © .NET Foundation | bundled by `dotnet publish` |
| mingw-w64 runtime (start-up and support code, statically linked) | the mingw-w64 runtime licences: a ZPL 2.1-style overall notice with BSD-style and public-domain parts; their full text, which a binary distribution must carry, is `src/Glimmer.Thumbnails/COPYING.MinGW-w64-runtime.txt` | inside `GlimmerThumbnails-*.dll`, the File Explorer thumbnail handler |
| LLVM runtime libraries (libc++, libunwind, compiler-rt; statically linked) | Apache-2.0 WITH LLVM-exception, © LLVM Project | inside `GlimmerThumbnails-*.dll` |

Not shipped, on purpose: the preview Direct3D 12 Agility SDK (`D3D12Core.dll`, `d3d12SDKLayers.dll`) that the WebGPU
package also carries. It only activates in Windows Developer Mode, so the app leaves it out and uses the system's
Direct3D 12.

## Models

**Bundled:**

| Model | Licence | Notes |
|---|---|---|
| Real-ESRGAN `realesr-general-x4v3` (Xintao Wang et al.) | BSD-3-Clause | Converted from the official `.pth` (SHA-256 `8dc7edb9…`) to ONNX by `src/Glimmer.AI/OnDevice/Resources/realesrgan_onnx.py`; embedded in `Glimmer.AI.dll` (2,448,168 bytes). |

**Downloaded only after the person asks for the feature**, from a pinned Hugging Face revision, every file checked
against its SHA-256 (the register is `src/Glimmer.AI/Requirements.cs`), stored in `%LOCALAPPDATA%\Glimmer\Models`:

| Feature | Model | Licence | Source (repository @ commit) |
|---|---|---|---|
| Magic Select, Select Sky | SAM 2.1 Hiera Tiny (Meta), 8-bit ONNX | Apache-2.0 | `onnx-community/sam2.1-hiera-tiny-ONNX` @ `814a0666` |
| Lens Blur depth, Select Sky | Depth Anything V2 Small, fp16 ONNX | Apache-2.0 | `onnx-community/depth-anything-v2-small` @ `4472b736` |
| Remove Background, Select Subject | IS-Net general-use (Qin et al., DIS), fp16 ONNX | Apache-2.0 (weights); MIT (repository) | `imgly/isnet-general-onnx` @ `440dea96` |
| Select People (finding people) | RT-DETRv2 R18, 8-bit ONNX | Apache-2.0 | `onnx-community/rtdetr_v2_r18vd-ONNX` @ `936f90b6` |
| Select People (outlines) | MODNet, fp16 ONNX | Apache-2.0 | `Xenova/modnet` @ `fa2fa546` |
| Smart layer names | MobileNetV4 conv small (timm, ImageNet-1k), fp16 ONNX | Apache-2.0 | `onnx-community/mobilenetv4_conv_small.e2400_r224_in1k` @ `3ba07f12` |

Not used, on purpose: RMBG-1.4/2.0 (non-commercial), Depth Anything V2 Base/Large (non-commercial), and an ISNet
repository tagged AGPL.

## Test data (not shipped)

The PSD tests can be pointed at the MIT-licensed test files of psd-tools (`GLIMMER_PSD_SAMPLES`); they are not part
of this repository.
