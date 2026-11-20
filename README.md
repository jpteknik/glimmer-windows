# Glimmer for Windows

A native Windows port of [Glimmer Native](https://github.com/fltman/glimmer-native) by Anders Bjarby: the AI-first,
layer-based image editor, originally for macOS in Swift, Metal and SwiftUI. This port is C# on .NET 10 with a WPF
interface and is based on the Mac version's commit `6d3ef0c` ("Release 0.3.1").

The Windows port is made and maintained by Johan Pålsson, who is responsible for it: questions and problems with
Glimmer for Windows go to him, not to Anders Bjarby.

It keeps the Mac version's architecture, file formats and behaviour: the tile-based document model, the 26 blend
modes, 14 adjustment layers, layer styles, smart objects and smart filters, masks and clipping, the brush and
retouching tools, selections, paths, text, liquify, the 14 filters, Photoshop files with layers, `.aips` projects
from Glimmer for the web, RAW development, the AI features through OpenRouter and the other model services, the
assistant with its planner, the on-device features, Batch Process, and the local API for agents.

## Install

`Glimmer-<version>-<rid>-setup.exe` installs Glimmer for the current user, with no administrator rights:

- the app and its console companion in `%LOCALAPPDATA%\Programs\Glimmer`, and a Start menu shortcut;
- that folder on the user's `PATH`, so `glimmer` works in terminals opened afterwards;
- `.glimmer` documents opening with Glimmer, with their pictures in File Explorer, and Glimmer under "Open with" for
  pictures (the installed app registers them itself, as Settings › General does);
- an entry under Settings › Apps whose Uninstall takes all of this back. Documents, settings and downloaded models
  (`%LOCALAPPDATA%\Glimmer`) are kept.

Installing over an installed Glimmer updates it; a running Glimmer is asked to close first. `/S` installs (and, on
`Uninstall.exe` in the install folder, uninstalls) without asking anything. The files aren't signed yet, so Windows
SmartScreen may warn before the first start (*More info › Run anyway*).

Glimmer also runs without being installed: `Glimmer-<version>-<rid>.exe` and its `.com` work from any folder.

## Requirements

- Windows 10 (1809 or later) or Windows 11, x64 or ARM64. Everything is self-contained: no .NET runtime needs to be
  installed.
- Optional, from the Microsoft Store: *HEIF Image Extensions* (opening HEIC photos; together with *HEVC Video
  Extensions* also saving them) and *Raw Image Extension* (camera RAW files). Text recognition uses the Windows OCR
  language packs that are installed.

## Command line

The Mac's Shortcuts actions, run by the same headless code as Batch Process. `Glimmer.com`, the console companion
shipped next to `Glimmer.exe`, makes them behave like any console program: in `cmd.exe` and PowerShell the word
`Glimmer` finds the `.com` before the `.exe`, so a command waits, prints in the console (or into a file or pipe it is
redirected to, as UTF-8) and returns the exit status. Ctrl+C stops it.

```
Glimmer --auto-enhance <input> <output> [--format png|jpeg|heic|tiff|webp]
Glimmer --remove-background <input> <output>
Glimmer --apply-preset <preset> <input> <output>
Glimmer --apply-filter <filter> <input> <output>
Glimmer --run-action <saved action> <input> <output>
Glimmer --export <image or .glimmer> <output>
Glimmer --help
```

Exit codes: 0 done, 1 failed, 2 usage error. `Glimmer photo.jpg` (or `Glimmer` alone) opens the window and returns at
once. Installed, the pair is on `PATH` as `Glimmer.exe` and `Glimmer.com`. Uninstalled, the `.com` starts the `.exe`
with its own name (`Glimmer-0.1.0-win-x64.com` starts `Glimmer-0.1.0-win-x64.exe`), and the bare word `Glimmer` needs
the pair renamed to `Glimmer.com` and `Glimmer.exe` (in the current folder PowerShell wants `.\Glimmer`). Without the
companion, `start /wait Glimmer.exe …` (in PowerShell `Start-Process -Wait`) waits for the windowed program and gives
its exit code.

## How it differs from the Mac version

**Documents.** A `.glimmer` document is a folder on the Mac. Windows has no folder that behaves as a file, so here a
document is one ZIP file with the same contents. Glimmer for Windows opens both forms (and the ZIP Finder makes of a
package), and **File › Save a Copy for Mac…** writes the folder form. Saving is explicit (Ctrl+S), with crash recovery
copies in `%LOCALAPPDATA%\Glimmer\Recovery`.

**Rendering.** Everything the Mac computes with Metal is computed on the CPU, checked against the Mac's own reference
images (all 59 pass the original's colour-difference criterion). It is fast for normal photos; very large documents,
zoomed-out rotated views and big filters are slower than on Apple silicon.

**On-device features.** Apple's Vision and Core ML are replaced by ONNX Runtime and the following models, downloaded
only when a feature first needs them: SAM 2.1 Tiny (Magic Select, Select Sky), Depth Anything V2 Small (Lens Blur,
Select Sky), IS-Net (Remove Background, Select Subject), RT-DETRv2 + MODNet (Select People) and MobileNetV4 (layer
names). Real-ESRGAN is built in, as on the Mac. It runs on the graphics card through ONNX Runtime's WebGPU provider
(Direct3D 12) when that works on the computer, and on the CPU otherwise, which any failure on the graphics card falls
back to by itself (on the CPU, 4K at 2× takes about two minutes; `GLIMMER_GPU=0` keeps it there). ONNX Runtime's
telemetry is switched off before the provider loads. Text recognition and face detection use Windows' own APIs. The
batch rating's "aesthetics" is a technical score (exposure, tonal range, clipping, colour), not Apple's learned one.

**HEIC.** Saving HEIC uses Windows' own encoder, which needs both *HEIF Image Extensions* and *HEVC Video Extensions*
from the Microsoft Store; on a computer without them HEIC is not offered (Export, Batch Process, the command line and
the agent API all ask the same check). Transparency is kept when the encoder keeps alpha and is otherwise flattened
over white; a Display P3 picture whose colour profile the encoder won't store is refused with a message rather than
written with the wrong colours.

**RAW development.** Windows decodes the camera file (its *Raw Image Extension*) and Glimmer develops exposure, white
balance, shadows and contrast on the picture as shot; highlight recovery, sharpness, noise reduction and lens
correction are not offered. The white balance a picture was shot with comes from the file itself for DNG (its colour
matrices and as-shot neutral, as the DNG specification defines it and as Apple's decoder reads it), from Windows' RAW
codec for other formats when the codec reports it, and is otherwise taken as daylight (6500 K).

**File Explorer thumbnails.** Where the Mac has QuickLook, Explorer shows a `.glimmer` document's picture through a
small native thumbnail handler (`src/Glimmer.Thumbnails`) that reads the preview every saved document already carries.
It is registered for the current user together with the file type (by the installer, or by Settings › General ›
*Make Glimmer the default for .glimmer files*) and taken back with it; Explorer runs it in a process of its own.
Documents in the Mac's folder form, and ZIPs made by Finder (which compresses the preview), show no thumbnail.

**Agent API.** As on the Mac: loopback only, on port 9001 or the next free port in 9101–9119; Help › Agent Access
shows the address and the guide.

**Not available on Windows:** Apple Intelligence routing in the omnibar (Windows has no built-in language model for
it, so requests go to the assistant, as on a Mac without Apple Intelligence); the sibling apps Flimmer and Jämmer
(separate Mac apps); automatic updates (Sparkle on the Mac; there is no Windows release feed to update from yet).

## Build and test

Needs the .NET 10 SDK. Everything except the window builds and tests on Linux too; the WPF app builds anywhere
(`EnableWindowsTargeting`) but runs only on Windows.

```sh
dotnet build Glimmer.slnx --artifacts-path <build folder>
dotnet test Glimmer.slnx --artifacts-path <build folder>
```

The app, its console companion and the installer, each one self-contained file, go into `dist\`
(`Glimmer-<version>-<rid>.exe`, `.com` and `-setup.exe`):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1            # win-x64; -Rid win-arm64 for ARM
```

or `scripts/publish.sh` from WSL/Linux. The installer is built twice: without its payload it is the uninstaller that
goes into the install folder, and with `GlimmerPayloadDir` it carries the app, the companion and that uninstaller.

Two parts are made by tools of their own and kept in the repository, so building Glimmer needs only the .NET SDK:

- the icons (`src/Glimmer.App/Glimmer.ico` for the app and the companion, `src/Glimmer.Setup/GlimmerSetup.ico` for the
  installer), from the original's artwork, by `dotnet run design/icon/make_icons.cs -- <repo folder>`;
- the thumbnail handler's DLLs (`src/Glimmer.App/Shell`) and the Linux build of its ZIP reader that the tests load
  (`tests/Glimmer.Core.Tests/Native`), by `LLVM_MINGW=<folder> src/Glimmer.Thumbnails/build.sh` with
  [llvm-mingw](https://github.com/mstorsjo/llvm-mingw), after a change in `src/Glimmer.Thumbnails`.

**No test spends money or reaches the network.** The test processes start with ONNX Runtime's telemetry switched
off. Opt-in suites: `GLIMMER_LIVE_AI=1` (real OpenRouter calls), `GLIMMER_MODELS_DIR=<folder>` (the downloaded
on-device models), `GLIMMER_PSD_SAMPLES=<folder>` (Photoshop files), `GLIMMER_BENCH=1` (benchmarks),
`GLIMMER_PERF=1` (the Mac suite's 8k speed budgets; run them alone, since a whole solution run puts several test
assemblies on the CPU at once), `GLIMMER_GPU_TEST=1` (Real-ESRGAN on the graphics card against the CPU; on Linux it
needs a Vulkan driver, which Mesa's lavapipe provides on the CPU).

Not in git (`.gitignore`): build output, `dist\` (published files are build results) and `samples\`, the folder of
test pictures for trying the app by hand.

## Projects

| Project | What it holds | Mac original |
|---|---|---|
| `src/Glimmer.Core` | document model, tiles, undo, the `.glimmer` format, commands, text layout | GlimmerCore |
| `src/Glimmer.Lzfse` | LZFSE codec (tiles stay compatible with the Mac's files) | Apple's Compression framework |
| `src/Glimmer.Render` | compositor, blend modes, adjustments, styles, filters, brush, liquify, canvas | GlimmerRender (Metal) |
| `src/Glimmer.Tools` | tool ids, brush settings, flood fill | GlimmerTools |
| `src/Glimmer.Heal` | Heal Selection | GlimmerHeal |
| `src/Glimmer.AI` | OpenRouter and other services, pipelines, assistant/agent, on-device analysis and models | GlimmerAI |
| `src/Glimmer.Formats` | image import/export, `.aips`, Photoshop files, RAW development, linked files | parts of GlimmerCore/GlimmerRender |
| `src/Glimmer.Editor` | the editor without a window: session, history, tools, opening/saving, AI controller, agent server, batch, command line | App/ (logic) |
| `src/Glimmer.App` | the WPF app | App/ (views) |
| `src/Glimmer.Console` | `Glimmer.com`, the console companion for the command line | new (the Mac has Shortcuts instead) |
| `src/Glimmer.Thumbnails` | the File Explorer thumbnail handler (C/C++, a COM DLL for x64 and ARM64) | the QuickLook extensions |
| `src/Glimmer.Setup` | the installer and uninstaller (per user, no administrator rights) | the Mac's disk image |

`docs/PORTING.md` has the conventions the port follows; `docs/UPSTREAM-NOTES.md` lists bugs found in the Mac
version while porting it; `THIRD_PARTY_NOTICES.md` lists every library and model with its licence.

## Status

The libraries are covered by their ported test suites, run on Linux. The WPF app starts on Windows 11 and shows the
main window with a document (2026-11-20); beyond that, canvas input (mouse, pen, touch), the tools, panels and
dialogs, the WIC decoders, OCR, model downloads and the file association have been compiled but not yet seen working.
The same holds for what was added after that first start: HEIC export, the RAW codec's white balance, Real-ESRGAN on
the graphics card, the console companion, the thumbnail handler, the installer and the icons. Of these, the parts that
don't need Windows are tested on Linux (a DNG's white balance, the companion's relay, the thumbnail handler's ZIP
reader in its native form, the installer's change to `PATH`, and the WebGPU path on lavapipe, where it matches the CPU
to 0.0005 of 255), but none has been seen working on Windows.

Known issue: the Editor test `EveryCommandOnANormalDocument("image.autoStraighten")` fails now and then ("Collection
was modified") and passes when run again.

## Licence

MIT, as the original: see `LICENSE` (© 2026 Anders Bjarby for Glimmer Native, © 2026 Johan Pålsson for Glimmer for
Windows). Third-party components: `THIRD_PARTY_NOTICES.md`.
