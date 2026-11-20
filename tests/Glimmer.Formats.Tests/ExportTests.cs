// Ported from the ExportTests suite in Packages/GlimmerKit/Tests/GlimmerRenderTests/CanvasRendererTests.swift (the two
// tests the Render port left to Formats), with new tests for what the Windows port does itself: the colour conversion,
// the 16-bit PNG writer, the ICC profiles, HEIC through the system's encoder and the atomic write. Files are read back
// with SkiaSharp and the tests' TIFF reader (ExportedFiles) where the Swift reads them with ImageIO. HEIC is not read
// back: only the system's encoder writes it, and the tests can't reach that.
using System.Numerics;
using Glimmer.Core;
using SkiaSharp;
using static Glimmer.Formats.Tests.ExportedFiles;

namespace Glimmer.Formats.Tests;

public class ExportTests
{
    [Theory]
    [InlineData(ExportFormat.Png)]
    [InlineData(ExportFormat.Jpeg)]
    [InlineData(ExportFormat.Tiff)]
    [InlineData(ExportFormat.Webp)]
    public void ExportsReadableImage(ExportFormat format)
    {
        var state = new DocumentState(300, 200, layers: [SolidLayer(new Vector4(1, 0, 0, 1), size: 300)]);
        using var flat = ImageExporter.Flatten(state);
        Assert.True(flat.Width == 300 && flat.Height == 200);

        var data = ImageExporter.Encode(flat, new ExportOptions(format, ExportBitDepth.Sixteen));
        Assert.Equal((300, 200), Size(data));
        Assert.Equal(format.Supports16Bit() ? 16 : 8, BitsPerComponent(data));

        // Read back one pixel as 8-bit sRGB.
        var (px, w, _) = Pixels8(data);
        int i = (10 * w + 10) * 4;
        Assert.True(px[i] > 245 && px[i + 1] < 12 && px[i + 2] < 12, $"{px[i]} {px[i + 1]} {px[i + 2]}");
    }

    [Fact]
    public void FlattensOverMatteWithoutAlpha()
    {
        var state = new DocumentState(64, 64, layers: []);  // fully transparent
        using var flat = ImageExporter.Flatten(state);
        using var jpeg = ImageExporter.Convert(flat, new ExportOptions(ExportFormat.Jpeg));
        var (px, _, _) = Read(jpeg);
        Assert.True(px[0] == 255 && px[1] == 255 && px[2] == 255);
    }

    [Fact]
    public void TheFlattenedPictureIsLinearHalfAndTheSrgbOneIsEightBitPremultiplied()
    {
        // Half-transparent pure red in the first tile, an opaque mid grey of linear 0.5 in the second (a solid grid
        // fills whole tiles).
        var state = new DocumentState(512, 32, layers:
        [
            SolidLayer(new Vector4(0.5f, 0, 0, 0.5f), size: 32),
            new Layer("grey", new RasterContent(TileGrid.Solid(new Vector4(0.5f, 0.5f, 0.5f, 1), new PixelRect(256, 0, 256, 32)))),
        ]);
        using var flat = ImageExporter.Flatten(state);
        Assert.Equal((SKColorType.RgbaF16, SKAlphaType.Premul), (flat.ColorType, flat.AlphaType));
        Assert.True(flat.ColorSpace!.GammaIsLinear);
        var linear = LinearFloats(flat);
        Assert.Equal(new[] { 0.5f, 0, 0, 0.5f }, linear[..4]);
        Assert.Equal(new[] { 0.5f, 0.5f, 0.5f, 1 }, linear[(300 * 4)..(300 * 4 + 4)]);

        using var srgb = ImageExporter.FlattenedSRGB(state);
        Assert.Equal((SKColorType.Rgba8888, SKAlphaType.Premul), (srgb.ColorType, srgb.AlphaType));
        Assert.True(srgb.ColorSpace!.IsSrgb);
        var raw = srgb.Bytes;
        // Red straight is 255; premultiplied by half it is 128 (127.5 rounded up), alpha 128.
        Assert.Equal(new byte[] { 128, 0, 0, 128 }, raw[..4]);
        // Linear 0.5 is sRGB 188.
        Assert.Equal(new byte[] { 188, 188, 188, 255 }, raw[(300 * 4)..(300 * 4 + 4)]);
    }

    [Fact]
    public void SoftEdgesKeepTheirColourInEightBitFiles()
    {
        // One step of coverage. Premultiplied 8-bit (the Mac's CGImage) would leave red 1/255 and lose the colour to
        // rounding; the straight values written here keep it.
        var state = new DocumentState(16, 16, layers: [SolidLayer(new Vector4(1f / 255, 0, 0, 1f / 255), size: 16)]);
        using var flat = ImageExporter.Flatten(state);
        using var png = ImageExporter.Convert(flat, new ExportOptions(ExportFormat.Png));
        Assert.Equal(SKAlphaType.Unpremul, png.AlphaType);
        Assert.Equal(new byte[] { 255, 0, 0, 1 }, png.Bytes[..4]);
        var data = ImageExporter.Encode(flat, new ExportOptions(ExportFormat.Png));
        using var codec = SKCodec.Create(SKData.CreateCopy(data));
        using var decoded = SKBitmap.Decode(codec, new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        Assert.Equal(new byte[] { 255, 0, 0, 1 }, decoded.Bytes[..4]);
    }

    [Fact]
    public void SixteenBitPngKeepsEveryLevel()
    {
        // 4096 levels of grey across the picture: a 16-bit file holds every one, an 8-bit one only 256.
        int w = 4096, h = 2;
        var pixels = new Half[w * h * 4];
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                int i = (y * w + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = (Half)(x / (float)(w - 1));
                pixels[i + 3] = (Half)1f;
            }
        }
        using var image = LinearImages.Bitmap(pixels, w, h);
        var data = ImageExporter.Encode(image, new ExportOptions(ExportFormat.Png, ExportBitDepth.Sixteen));
        var chunks = PngChunks(data);
        Assert.Equal(new[] { "IHDR", "sRGB" }, chunks.Take(2).Select(c => c.Type));
        Assert.Equal("IEND", chunks[^1].Type);
        var samples = Png16Samples(data);
        for (int x = 0; x < w; x++)
        {
            float v = (float)pixels[x * 4];
            int expected = (int)(Glimmer.Render.SrgbTransfer.EncodeFast(v) * 65535f + 0.5f);
            Assert.Equal(expected, samples[x * 4]);
            Assert.Equal(65535, samples[x * 4 + 3]);
        }
        // Every level the Half pixels hold (3 073 of the 4 096 asked for; Halves are coarse near 1) stays its own.
        int levels = Enumerable.Range(0, w).Select(x => pixels[x * 4]).Distinct().Count();
        Assert.Equal(levels, Enumerable.Range(0, w).Select(x => samples[x * 4]).Distinct().Count());
        Assert.True(levels > 3000, $"{levels}");
        var eight = ImageExporter.Encode(image, new ExportOptions(ExportFormat.Png));
        var (px8, _, _) = Pixels8(eight);
        Assert.Equal(256, Enumerable.Range(0, w).Select(x => px8[x * 4]).Distinct().Count());
        // Skia reads the same file (and the colour is right).
        var (px, _, _) = Pixels8(data);
        Assert.Equal(255, px[(w - 1) * 4]);
        Assert.Equal(0, px[0]);
    }

    [Fact]
    public void SixteenBitPngRowsAreFilteredAsTheSpecificationSays()
    {
        // A picture where every filter has rows to win (smooth across, smooth down, diagonal, noise): what an independent
        // reading of the rows gives back is exactly what was converted, whichever filter each row chose.
        int w = 96, h = 96;
        var pixels = new Half[w * h * 4];
        var random = new Random(5);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                float band = (y / 24) switch { 0 => x / 96f, 1 => y / 96f, 2 => (x + y) / 192f, _ => (float)random.NextDouble() };
                pixels[i] = (Half)band; pixels[i + 1] = (Half)(1 - band); pixels[i + 2] = (Half)(band * band); pixels[i + 3] = (Half)1f;
            }
        }
        using var image = LinearImages.Bitmap(pixels, w, h);
        var options = new ExportOptions(ExportFormat.Png, ExportBitDepth.Sixteen);
        using var converted = ImageExporter.Convert(image, options);
        var expected = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(converted.GetPixelSpan()).ToArray();
        var samples = Png16Samples(ImageExporter.Encode(image, options), out var filters);
        Assert.Equal(expected, samples);
        Assert.Contains(4, filters);   // Paeth
        Assert.True(filters.Count >= 3, string.Join(",", filters));
    }

    [Fact]
    public void TheProfilesAreSkiasByteForByte()
    {
        // Skia's own: from the iCCP chunk of a PNG it writes (sRGB gets an sRGB chunk there, so from WebP's ICCP).
        Assert.Equal(TestProfiles.FromPng(IccProfile.ColorSpace(WorkingColorSpace.DisplayP3))!.Value.ToArray(), IccProfile.Rgb(WorkingColorSpace.DisplayP3));
        Assert.Equal(TestProfiles.FromPng(SKColorSpace.CreateSrgbLinear())!.Value.ToArray(), IccProfile.Rgb(WorkingColorSpace.SRGB, linear: true));
        Assert.Equal(TestProfiles.FromPng(IccProfile.ColorSpace(WorkingColorSpace.DisplayP3, linear: true))!.Value.ToArray(),
                     IccProfile.Rgb(WorkingColorSpace.DisplayP3, linear: true));
        Assert.Equal(TestProfiles.FromWebP(SKColorSpace.CreateSrgb()), IccProfile.Rgb(WorkingColorSpace.SRGB));
        // And each reads back as the space it names.
        foreach (var (space, linear) in new[] { (WorkingColorSpace.SRGB, false), (WorkingColorSpace.DisplayP3, false), (WorkingColorSpace.SRGB, true), (WorkingColorSpace.DisplayP3, true) })
        {
            using var parsed = SKColorSpace.CreateIcc(IccProfile.Rgb(space, linear));
            Assert.Equal(linear, parsed.GammaIsLinear);
            Assert.True(parsed.ToColorSpaceXyz(out var xyz));
            var expected = (space == WorkingColorSpace.DisplayP3 ? SKColorSpaceXyz.DisplayP3 : SKColorSpaceXyz.Srgb).Values;
            for (int i = 0; i < 9; i++) Assert.Equal(expected[i], xyz.Values[i], 3);
        }
    }

    [Fact]
    public void DisplayP3ExportsAreConvertedAndTagged()
    {
        // sRGB red is (0.917, 0.200, 0.139) in Display P3's encoded values.
        var state = new DocumentState(32, 32, layers: [SolidLayer(new Vector4(1, 0, 0, 1), size: 32)]);
        using var flat = ImageExporter.Flatten(state);
        var options = new ExportOptions(ExportFormat.Png, colorSpace: WorkingColorSpace.DisplayP3);
        using var converted = ImageExporter.Convert(flat, options);
        var raw = converted.Bytes;
        Assert.InRange(raw[0], 233, 235);
        Assert.InRange(raw[1], 50, 52);
        Assert.InRange(raw[2], 34, 36);
        var data = ImageExporter.Encode(flat, options);
        Assert.Equal(IccProfile.Rgb(WorkingColorSpace.DisplayP3), PngProfile(data));
        // A colour-managed reader gets red back.
        var (px, _, _) = Pixels8(data);
        Assert.True(px[0] >= 254 && px[1] <= 1 && px[2] <= 1, $"{px[0]} {px[1]} {px[2]}");
        // The 16-bit writer tags the same way.
        var deep = ImageExporter.Encode(flat, options with { BitDepth = ExportBitDepth.Sixteen });
        Assert.Equal(IccProfile.Rgb(WorkingColorSpace.DisplayP3), PngProfile(deep));
        Assert.DoesNotContain(PngChunks(deep), c => c.Type == "sRGB");
    }

    [Fact]
    public void TiffCarriesItsAlphaKindAndProfile()
    {
        var state = new DocumentState(40, 20, layers: [SolidLayer(new Vector4(0.25f, 0.5f, 0, 0.5f), size: 40)]);
        using var flat = ImageExporter.Flatten(state);
        foreach (var depth in new[] { ExportBitDepth.Eight, ExportBitDepth.Sixteen, ExportBitDepth.Float32 })
        {
            var tiff = Tiff(ImageExporter.Encode(flat, new ExportOptions(ExportFormat.Tiff, depth)));
            Assert.Equal((int)depth, tiff.Bits);
            Assert.Equal(4, tiff.SamplesPerPixel);
            // Straight ("unassociated") alpha for integers, premultiplied ("associated") for floats.
            Assert.Equal(depth == ExportBitDepth.Float32 ? 1 : 2, tiff.ExtraSample);
            Assert.Equal(IccProfile.Rgb(WorkingColorSpace.SRGB, linear: depth == ExportBitDepth.Float32), tiff.Icc);
            if (depth == ExportBitDepth.Float32)
            {
                Assert.Equal(new[] { 0.25, 0.5, 0, 0.5 }, tiff.Samples[..4].Select(v => Math.Round(v, 3)));
            }
            else
            {
                double max = (1 << (int)depth) - 1;
                // Straight 0.5 and 1.0 linear, gamma-encoded.
                Assert.InRange(tiff.Samples[0], Math.Round(ColorMath.LinearToSrgb(0.5) * max) - 1, Math.Round(ColorMath.LinearToSrgb(0.5) * max) + 1);
                Assert.Equal(max, tiff.Samples[1]);
                Assert.Equal(Math.Round(0.5 * max, MidpointRounding.AwayFromZero), tiff.Samples[3]);
            }
        }
    }

    [Fact]
    public void LayerImageShowsOneLayerAlone()
    {
        var hidden = SolidLayer(new Vector4(0, 0, 1, 1), size: 16, opacity: 0.25, mode: BlendMode.Multiply) with { IsVisible = false };
        var state = new DocumentState(16, 16, layers: [SolidLayer(new Vector4(1, 0, 0, 1), size: 16), hidden]);
        using var image = ImageExporter.LayerImageSRGB(hidden, state);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, image.Bytes[..4]);
    }

    private sealed class FakeHeicEncoder : IPlatformImageEncoder
    {
        public (byte[] Rgba, int Width, int Height, byte[] Icc, double Quality)? Last;

        public bool CanEncode(ExportFormat format) => format == ExportFormat.Heic;

        public byte[] Encode(ExportFormat format, ReadOnlySpan<byte> rgba, int width, int height, ReadOnlySpan<byte> iccProfile, double quality)
        {
            Last = (rgba.ToArray(), width, height, iccProfile.ToArray(), quality);
            return "ftypheic"u8.ToArray();
        }
    }

    [Fact]
    [Trait("Shares", "ImageExporter.PlatformEncoder")]
    public void HeicGoesThroughTheSystemsEncoder()
    {
        lock (PlatformEncoderLock.Gate)
        {
            var previous = ImageExporter.PlatformEncoder;
            try
            {
                var state = new DocumentState(8, 4, layers: [SolidLayer(new Vector4(0, 0.5f, 0, 0.5f), size: 8)]);
                using var flat = ImageExporter.Flatten(state);
                ImageExporter.PlatformEncoder = null;
                Assert.False(ImageExporter.CanWrite(ExportFormat.Heic));
                Assert.True(ImageExporter.CanWrite(ExportFormat.Png));
                var error = Assert.ThrowsAny<Exception>(() => ImageExporter.Encode(flat, new ExportOptions(ExportFormat.Heic)));
                Assert.Contains("no encoder for HEIC", error.Message);
                Assert.Null(LinkedImage.Create("/a/photo.heic", "Flimmer"));

                var fake = new FakeHeicEncoder();
                ImageExporter.PlatformEncoder = fake;
                Assert.True(ImageExporter.CanWrite(ExportFormat.Heic));
                var data = ImageExporter.Encode(flat, new ExportOptions(ExportFormat.Heic, quality: 0.7, colorSpace: WorkingColorSpace.DisplayP3));
                Assert.Equal("ftypheic"u8.ToArray(), data);
                var (rgba, w, h, icc, quality) = fake.Last!.Value;
                Assert.Equal((8, 4, 0.7), (w, h, quality));
                Assert.Equal(IccProfile.Rgb(WorkingColorSpace.DisplayP3), icc);
                // Straight alpha (HEIC keeps transparency), in the profile's colours: sRGB green is (117, 251, 76) in P3.
                Assert.Equal(128, rgba[3]);
                Assert.InRange(rgba[0], 115, 119);
                Assert.InRange(rgba[1], 249, 252);
                Assert.Equal(ExportFormat.Heic, LinkedImage.Create("/a/photo.HEIF", "Flimmer")?.Format);
            }
            finally
            {
                ImageExporter.PlatformEncoder = previous;
            }
        }
    }

    [Fact]
    public void ExportWritesTheWholeFileOrNothing()
    {
        var folder = Directory.CreateTempSubdirectory("glimmer-export-");
        try
        {
            var state = new DocumentState(20, 10, layers: [SolidLayer(new Vector4(0, 0, 1, 1), size: 20)]);
            string path = Path.Combine(folder.FullName, "out.png");
            File.WriteAllBytes(path, new byte[100_000]);   // an older, bigger file is replaced
            ImageExporter.Export(state, new ExportOptions(ExportFormat.Png), path);
            var data = File.ReadAllBytes(path);
            Assert.Equal((20, 10), Size(data));
            Assert.Equal(new[] { "out.png" }, Directory.GetFiles(folder.FullName).Select(Path.GetFileName));
            // Into a folder that isn't there: an error, and nothing written anywhere.
            Assert.ThrowsAny<IOException>(() => ImageExporter.Export(state, new ExportOptions(ExportFormat.Png), Path.Combine(folder.FullName, "missing", "x.png")));
            Assert.Equal(new[] { "out.png" }, Directory.GetFiles(folder.FullName).Select(Path.GetFileName));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void FormatsKnowWhatTheyHold()
    {
        Assert.Equal(new[] { "png", "jpeg", "heic", "tiff", "webp" }, Enum.GetValues<ExportFormat>().Select(f => f.RawValue()));
        Assert.Equal(ExportFormat.Webp, ExportFormatExtensions.FormatFromRawValue("webp"));
        Assert.Equal(new[] { "PNG", "JPEG", "HEIC", "TIFF", "WebP" }, Enum.GetValues<ExportFormat>().Select(f => f.Title()));
        Assert.False(ExportFormat.Jpeg.SupportsAlpha());
        Assert.True(ExportFormat.Webp.HasQuality() && !ExportFormat.Webp.IsLossy());
        Assert.Equal(ExportBitDepth.Sixteen, new ExportOptions(ExportFormat.Png, ExportBitDepth.Float32).EffectiveBitDepth);
        Assert.Equal(ExportBitDepth.Eight, new ExportOptions(ExportFormat.Webp, ExportBitDepth.Sixteen).EffectiveBitDepth);
        Assert.Equal(new ExportOptions(ExportFormat.Jpeg, quality: 0.85, maxPixelSide: 2048, maxBytes: 500_000), ExportOptions.Web);
        Assert.Equal("32-bit float (linear)", ExportBitDepth.Float32.Title());
    }
}

/// <summary>Tests that set ImageExporter.PlatformEncoder hold this, so they don't see each other's.</summary>
internal static class PlatformEncoderLock
{
    public static readonly object Gate = new();
}
