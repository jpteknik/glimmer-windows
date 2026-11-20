# Notes for the Mac version

Things found in Glimmer Native (commit `6d3ef0c`) while porting it. Each says what was measured, what the Windows port
does, and whether the port deviates from the Mac.

## PSD reader (`Packages/GlimmerKit/Sources/GlimmerCore/PSD/PSDFile.swift`)

Measured against the 347 MIT-licensed test files of psd-tools (`third-party-psds` left out, as the original does);
psd-tools was used as the outside reader.

1. **16- and 32-bit layer masks are read as 8-bit.** These files store masks at the document's depth: the compressed
   streams inflate to exactly 2.00 and 4.00 bytes per sample. `PSDFile.swift` reads masks as 1 byte per sample, so
   every 16-bit PSD with a mask is refused as damaged (9 files; 2 more at 32 bits). A mask stored uncompressed would
   instead be misread without an error. **Port:** decodes masks at the document's depth and reduces them to 8 bits.
2. **Field order in the layer mask record.** The "real" mask rectangle comes before the density/feather parameters;
   read in that order, the `-3` channels fit their rectangles exactly. `PSDFile.swift` reads the parameters first, so
   3 files with both are refused. **Port:** reads in psd-tools' order.
   The writer has the same half: `PSDWriter.swift` packs masks with `bytes: 1`, so a 16-bit file's mask is written
   at 8 bits. **Port:** writes the mask at the document's depth (v × 257 for 16 bits); psd-tools reads it back the same.
4. **Hidden areas of a mask that shows by default (found by reading the code; not run on a Mac).** `PSDImport.mask`
   builds its tiles with `Selection.rasterize`, which leaves out all-zero tiles. With a mask whose default is "show",
   a fully hidden 256 × 256 area then comes in shown. **Port:** keeps the tile, with a test.
5. **Strictness (policy, not a bug).** One layer with bottom < top (`vector-mask2.psd`) refuses the whole file, and a
   `-3` plane larger than 4× the canvas refuses `masks.psd`/`.psb` although Glimmer discards `-3` planes. **Port:**
   unchanged, same as the Mac.

## WebP encoder (`Packages/GlimmerKit/Sources/GlimmerRender/VP8Encoder.swift`)

- `WebPEncoder.encodeLossy` accepts a width or height of 16 384, but `VP8Encoder.swift:948` stores the size in 14
  bits, so such a file declares a size of 0. Lossless (VP8L) is fine because it stores width − 1. **Port:** uses
  SkiaSharp's libwebp encoder, which refuses anything over 16 383 px with a message.

## AI services

- `docs/PLAN.md` §2.8 says the OpenRouter client has a circuit breaker. The Swift code has none (retry with backoff,
  `Retry-After` capped at 60 s, 401/402 fatal, 4 retries). **Port:** same as the code, no circuit breaker.
