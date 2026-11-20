# Porting Glimmer Native to Windows

This repository is Johan Pålsson's Windows port of [Glimmer Native](https://github.com/fltman/glimmer-native) by Anders Bjarby,
the AI-first, layer-based image editor for macOS (Swift, Metal, SwiftUI). The port is based on commit
`6d3ef0c926d46978bdae4b6d3e8cc0b098b31ee0` ("Release 0.3.1").

The Mac version's own architecture document is `docs/PLAN.md` in that repository (in Swedish). The port keeps its
architecture and file formats and replaces the Apple frameworks with Windows and cross-platform equivalents.

## Stack

| | Mac (original) | Windows (this port) |
|---|---|---|
| Language | Swift 6 | C# (latest), .NET 10 |
| Window and controls | SwiftUI + AppKit | WPF |
| Pixel math | Metal shaders, Core Image, MPS | C# on the CPU (tiles in parallel, SIMD where it pays) |
| Image files | ImageIO | SkiaSharp (PNG, JPEG, WebP, BMP, GIF, ICO) + own TIFF writer; WIC for formats Skia can't read |
| Text | Core Text | SkiaSharp + HarfBuzz |
| Tile compression | Compression framework (LZFSE) | `Glimmer.Lzfse` (own codec, same format) |
| Keys | Keychain | Windows DPAPI (current user) |
| On-device models | Vision, Core ML | Windows OCR, ONNX Runtime |
| Automation | App Intents (Shortcuts) | Command line + the local agent API |

## Projects

| Swift (glimmer-native) | C# (this repo) |
|---|---|
| `Packages/GlimmerKit/Sources/GlimmerCore` | `src/Glimmer.Core` — document model, tiles, undo, `.glimmer`, text layout (SkiaSharp + HarfBuzz where the Mac uses Core Text) |
| Apple's LZFSE | `src/Glimmer.Lzfse` |
| `Packages/GlimmerKit/Sources/GlimmerRender` | `src/Glimmer.Render` — compositor, filters, effects, text, export, `.aips` |
| `Packages/GlimmerKit/Sources/GlimmerTools` | `src/Glimmer.Tools` |
| `Packages/GlimmerKit/Sources/GlimmerHeal` | `src/Glimmer.Heal` |
| `Packages/GlimmerKit/Sources/GlimmerAI` | `src/Glimmer.AI` |
| `GlimmerCore/ImageImporter`, `AipsImporter`, `PSD/*` (except `PSDImportReport`, which the model needs); `GlimmerRender/ImageExporter`, `AipsExporter`, `PSDExport`, `PSDAppearance`, `RawDevelopment`, `RawLayer`, `LinkedImage` | `src/Glimmer.Formats` — files in and out (a separate project here so import/export can be worked on alongside the renderer) |
| `App/` — logic (EditorSession*, AIController*, AgentServer, DocumentOpener, GlimmerDocument, LinkJournal, AppCommands, Intents) | `src/Glimmer.Editor` (no WPF; testable anywhere) |
| `App/` — views | `src/Glimmer.App` (WPF, Windows only) |
| `Packages/GlimmerKit/Tests/*` | `tests/Glimmer.*.Tests` (xUnit) |

Every library targets `net10.0` and builds and tests on Linux as well as Windows. Only `Glimmer.App` targets
Windows (`net10.0-windows10.0.19041.0`). Anything that needs Windows (clipboard, dialogs, key storage, OCR) is an
interface in `Glimmer.Editor` with its implementation in `Glimmer.App`.

## Conventions

**Naming.** Swift `camelCase` members become C# `PascalCase`; otherwise names stay the same so the two code bases
can be read side by side (`DocumentState.insertLayerAboveActive` → `DocumentState.InsertLayerAboveActive`). A Swift
file `X.swift` becomes `X.cs` in the matching folder, and starts with a comment naming the file it was ported from:
`// Ported from Packages/GlimmerKit/Sources/GlimmerCore/Layer.swift`. Keep the original's comments where they still
hold; they carry measurements and decisions.

**Values.** The document model is value-typed in Swift so that undo snapshots are free. In C# the model types are
immutable: `sealed record` (or `readonly record struct` for small ones) with `init` properties, collections as
`ImmutableArray<T>` / `ImmutableDictionary<TKey,TValue>`. A Swift `mutating func` becomes a method that returns the
new value (`state = state.InsertLayerAboveActive(layer)`). Where Swift relies on `Equatable`/`Hashable` and a
member is a collection, override equality so it compares contents (records compare `ImmutableArray` by reference).

**Pixels.** Premultiplied, linear, extended-sRGB RGBA, exactly as on the Mac. A tile is 256 × 256 pixels stored as
`Half` (Float16) RGBA, row-major, little-endian: the same bytes as the Mac's tile textures and the `.glimmer` files.
Arithmetic is done in `float`. Colours are `System.Numerics.Vector4` (X = R, Y = G, Z = B, W = A). Tiles are never
modified after they are made (copy on write), and each has an identity that is never reused; caches, undo and saving
compare identities, not pixels. A missing tile is fully transparent; a uniform tile is stored as one colour.

**Geometry.** Swift `SIMD2<Double>` is `Glimmer.Core.Vec2` (double X, Y). `PixelRect` and `TileCoord` are integer,
y-down, as in Swift.

**JSON.** `.glimmer` manifests and `.aips` projects must stay readable by the Mac version and the other way round.
Use System.Text.Json with converters that reproduce Swift's `Codable` output: enum raw values; enums with associated
values as `{"case":{"_0":…}}` (or the labels); `nil` optionals left out; `SIMD2` as `[x, y]`; `UUID` as an upper-case
string; `Data` as base64; a dictionary whose key is neither `String` nor `Int` (and doesn't adopt
`CodingKeyRepresentable`) as a flat array `[key, value, key, value, …]`. Where the Swift code has a custom
`init(from:)`/`encode(to:)`, port that instead. Decoding is as lenient as the Swift decoder.

**Errors and text.** Swift `throws` becomes an exception; a `LocalizedError.errorDescription` becomes the exception's
`Message` with the same English text. Every text a person sees stays in English in the code, word for word as in the
Swift source; the Swedish translation is applied by the app from a table keyed by the English text (as the Mac String
Catalog does).

**Concurrency.** `async`/`await` maps to `Task`; an `actor` becomes a class that guards its state with a lock or a
`SemaphoreSlim`. Libraries never touch a UI thread.

**No GPU, with one exception.** Everything the Mac does in Metal is done on the CPU. The exception is Real-ESRGAN,
which runs on the graphics card through ONNX Runtime's WebGPU provider when it can and falls back to the CPU on any
failure (`OnDevice/OnnxGpu.cs`); its CPU output is unchanged. Correctness first: the golden images from the Mac
test suite (`tests/Glimmer.Render.Tests/Golden`) are the reference, with the same tolerances as the Swift tests.
Then speed: `Parallel.For` over tiles or rows, `System.Numerics.Tensors.TensorPrimitives` for Half ↔ float, and
`Vector128`/`Vector<T>` in hot loops.

**Packages.** Only the packages in `Directory.Packages.props`, all MIT/BSD/Apache. A new package needs a reason.

**Tests.** The Swift tests of a module are ported with it (Swift Testing `#expect` → xUnit `Assert`), keeping their
names. A test that cannot run off the Mac is ported in spirit (same property, different instrument) or listed as not
ported with the reason. No test calls a real network service; no test spends money.

## Building

Each person or process builds into its own artifacts folder, outside the source tree, so builds never collide:

```sh
dotnet build Glimmer.slnx --artifacts-path /tmp/glimmer-build/<name>
dotnet test tests/Glimmer.Core.Tests/Glimmer.Core.Tests.csproj --artifacts-path /tmp/glimmer-build/<name>
dotnet publish src/Glimmer.App/Glimmer.App.csproj -c Release -r win-x64 --artifacts-path /tmp/glimmer-build/<name>
```

The WPF project builds on Linux (`EnableWindowsTargeting`) but runs only on Windows.
