// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/WebPEncoderTests.swift, adapted to SkiaSharp: WebPEncoder is
// libwebp here, where the Mac has its own encoders. Files are decoded with SkiaSharp into 8-bit premultiplied sRGB, as
// the Swift's ImageIO decoding draws into a premultiplied context. The test pixels' noise comes from a seeded generator
// (the Swift uses the system's). Not ported: near-lossless (no counterpart in SkiaSharp), and the photo suite's
// reconstruction output (it reads the Mac's VP8 encoder's internals); the photo suite's size and fidelity report is.
// The float TIFF test of the original's file is here too.
using System.Numerics;
using Glimmer.Core;
using SkiaSharp;
using static Glimmer.Formats.Tests.ExportedFiles;

namespace Glimmer.Formats.Tests;

public class WebPEncoderTests
{
    /// <summary>Decodes into premultiplied RGBA8 sRGB (what the Swift's CGContext offers).</summary>
    internal static (byte[] Pixels, int Width, int Height) DecodeRgba(byte[] data) => Pixels8(data);

    /// <summary>Smooth gradients plus noise: exercises every predictor and long Huffman codes.</summary>
    private static byte[] TestPixels(int width, int height, bool alpha)
    {
        var random = new Random(11);
        var px = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                int noise = random.Next(-6, 7);
                px[i] = (byte)Math.Clamp(x * 255 / Math.Max(width - 1, 1) + noise, 0, 255);
                px[i + 1] = (byte)Math.Clamp(y * 255 / Math.Max(height - 1, 1) - noise, 0, 255);
                px[i + 2] = (byte)((x ^ y) & 0xff);
                px[i + 3] = alpha ? (byte)(x < width / 3 ? 0 : 255) : (byte)255;
            }
        }
        return px;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 3)]
    [InlineData(64, 64)]
    [InlineData(333, 129)]
    public void OpaqueImagesRoundTripExactly(int w, int h)
    {
        var px = TestPixels(w, h, alpha: false);
        var data = WebPEncoder.Encode(px, w, h);
        var decoded = DecodeRgba(data);
        Assert.True(decoded.Width == w && decoded.Height == h);
        Assert.Equal(px, decoded.Pixels);
    }

    [Fact]
    public void AlphaRoundTrips()
    {
        int w = 120, h = 40;
        var px = TestPixels(w, h, alpha: true);
        var decoded = DecodeRgba(WebPEncoder.Encode(px, w, h));
        for (int i = 0; i < px.Length; i += 4)
        {
            Assert.Equal(px[i + 3], decoded.Pixels[i + 3]);
            if (px[i + 3] == 255) Assert.Equal(px[i..(i + 3)], decoded.Pixels[i..(i + 3)]);
        }
    }

    [Fact]
    public void FlatImagesUseSimpleCodes()
    {
        // One colour: every prefix code collapses to a single symbol.
        var px = Enumerable.Range(0, 50 * 50 * 4).Select(i => i % 4 == 3 ? (byte)255 : (byte)77).ToArray();
        var data = WebPEncoder.Encode(px, 50, 50);
        Assert.True(data.Length < 1000, $"{data.Length}");  // 10 kB raw
        Assert.Equal(px, DecodeRgba(data).Pixels);
    }

    [Fact]
    public void EmbedsTheICCProfile()
    {
        var px = TestPixels(16, 16, alpha: false);
        var data = WebPEncoder.Encode(px, 16, 16, IccProfile.ColorSpace(WorkingColorSpace.DisplayP3));
        var icc = TestProfiles.ChunkOfWebP(data, "ICCP");
        Assert.NotNull(icc);
        Assert.Contains("Display P3", System.Text.Encoding.BigEndianUnicode.GetString(icc!, 268, 70));
        using var codec = SKCodec.Create(SKData.CreateCopy(data));
        Assert.True(codec.Info.ColorSpace!.ToColorSpaceXyz(out var xyz));
        Assert.Equal(SKColorSpaceXyz.DisplayP3.Values[0], xyz.Values[0], 3);
        // Without a space, no profile (the file is sRGB by convention), as the Mac writes sRGB WebP.
        Assert.Null(TestProfiles.ChunkOfWebP(WebPEncoder.Encode(px, 16, 16), "ICCP"));
    }

    [Fact]
    public void RejectsHugeImages()
    {
        var error = Assert.Throws<WebPEncoder.Error>(() => WebPEncoder.Encode([], 20_000, 1));
        Assert.Equal("WebP images can be at most 16 383 × 16 383 pixels (this one is 20000 × 1).", error.Message);
        Assert.Throws<WebPEncoder.Error>(() => WebPEncoder.EncodeLossy([], 1, 16_384, 0.8));
    }

    [Fact]
    public void ExportsADocumentAsWebP()
    {
        var state = new DocumentState(300, 200, layers: [SolidLayer(new Vector4(1, 0, 0, 1), size: 300)]);
        using var flat = ImageExporter.Flatten(state);
        // 100 % quality is lossless: exact colour.
        var data = ImageExporter.Encode(flat, new ExportOptions(ExportFormat.Webp, quality: 1));
        Assert.Equal("RIFF"u8.ToArray(), data[..4]);
        var decoded = DecodeRgba(data);
        Assert.True(decoded.Width == 300 && decoded.Pixels[0] == 255 && decoded.Pixels[1] == 0);
        // Below that it is lossy (a "VP8 " chunk): close colour, smaller file.
        var lossy = ImageExporter.Encode(flat, new ExportOptions(ExportFormat.Webp, quality: 0.8));
        Assert.Equal("VP8 "u8.ToArray(), lossy[12..16]);
        var approx = DecodeRgba(lossy);
        Assert.True(approx.Width == 300 && approx.Pixels[0] > 240 && approx.Pixels[1] < 16, $"{approx.Pixels[0]} {approx.Pixels[1]}");
    }

    [Fact]
    public void FloatTIFFKeepsHDRValues()
    {
        // Linear 2.0 (brighter than white) must survive as a float above 1.
        var state = new DocumentState(64, 64, layers: [SolidLayer(new Vector4(2, 0.5f, 0, 1), size: 64)]);
        using var flat = ImageExporter.Flatten(state);
        var options = new ExportOptions(ExportFormat.Tiff, ExportBitDepth.Float32);
        Assert.Equal(ExportBitDepth.Float32, options.EffectiveBitDepth);
        Assert.Equal(ExportBitDepth.Sixteen, new ExportOptions(ExportFormat.Png, ExportBitDepth.Float32).EffectiveBitDepth);
        var data = ImageExporter.Encode(flat, options);
        var tiff = Tiff(data);
        Assert.Equal(32, tiff.Bits);
        Assert.True(tiff.IsFloat);
        int i = (10 * 64 + 10) * 4;
        Assert.True(Math.Abs(tiff.Samples[i] - 2) < 0.01 && Math.Abs(tiff.Samples[i + 1] - 0.5) < 0.01, $"{tiff.Samples[i]} {tiff.Samples[i + 1]}");
    }

    [OutsideReaderFact("tifffile", "imagecodecs", "numpy")]
    public void AnOutsideReaderSeesTheHDRValuesToo()
    {
        var state = new DocumentState(64, 64, layers: [SolidLayer(new Vector4(2, 0.5f, 0, 1), size: 64)]);
        using var flat = ImageExporter.Flatten(state);
        string path = Path.Combine(Path.GetTempPath(), $"glimmer-hdr-{Guid.NewGuid():N}.tif");
        try
        {
            File.WriteAllBytes(path, ImageExporter.Encode(flat, new ExportOptions(ExportFormat.Tiff, ExportBitDepth.Float32)));
            var result = OutsideReader.Read("read_tiff_with_tifffile.py", path);
            var first = result.GetProperty("pixel_10_10").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            Assert.True(Math.Abs(first[0] - 2) < 0.01 && Math.Abs(first[1] - 0.5) < 0.01, string.Join(" ", first));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PhotoSizedImageEncodesSmall()
    {
        // A 2048 × 1536 smooth image with grain: WebP must stay well below raw size.
        int w = 2048, h = 1536;
        var px = new byte[w * h * 4];
        Array.Fill(px, (byte)255);
        uint seed = 1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                seed = unchecked(seed * 1_664_525 + 1_013_904_223);
                int n = (int)(seed >> 29);
                int i = (y * w + x) * 4;
                px[i] = (byte)Math.Min(255, x * 200 / w + n);
                px[i + 1] = (byte)Math.Min(255, y * 180 / h + n);
                px[i + 2] = (byte)Math.Min(255, 90 + ((x + y) >> 5) % 40);
            }
        }
        var data = WebPEncoder.Encode(px, w, h);
        Assert.True(data.Length < px.Length / 3, $"{data.Length}");
    }

    // MARK: Lossy (the Swift's WebPLossyTests)

    /// <summary>A photo-like test picture: gradients, a disc, a hard-edged box and mild texture.</summary>
    private static byte[] Picture(int w, int h, bool alpha = false)
    {
        var px = new byte[w * h * 4];
        Array.Fill(px, (byte)255);
        uint seed = 7;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                seed = unchecked(seed * 1_664_525 + 1_013_904_223);
                int n = (int)(seed >> 30);
                int i = (y * w + x) * 4;
                int dx = x - w / 2, dy = y - h / 2;
                int r = Math.Min(w, h) / 4;
                bool inDisc = dx * dx + dy * dy < r * r;
                bool inBox = x > w * 3 / 4 && y < h / 4;
                px[i] = (byte)Math.Clamp((inDisc ? 220 : x * 200 / Math.Max(w, 1)) + n, 0, 255);
                px[i + 1] = (byte)Math.Clamp((inBox ? 30 : y * 180 / Math.Max(h, 1) + 40) + n, 0, 255);
                px[i + 2] = (byte)Math.Clamp((inDisc ? 60 : 140 - x * 60 / Math.Max(w, 1)) + n, 0, 255);
                if (alpha) px[i + 3] = (byte)(x < w / 4 ? 0 : x < w / 2 ? 128 : 255);
            }
        }
        return px;
    }

    private static double Psnr(byte[] a, byte[] b, Func<int, bool>? include = null)
    {
        double sum = 0, n = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (include is not null && !include(i)) continue;
            for (int c = 0; c < 3; c++) { double d = a[i + c] - (double)b[i + c]; sum += d * d; n++; }
        }
        return sum == 0 ? 99 : 10 * Math.Log10(255 * 255 / (sum / n));
    }

    [Theory]
    [InlineData(256, 192)]
    [InlineData(17, 33)]
    [InlineData(1, 1)]
    [InlineData(640, 427)]
    public void DecodesCloseToTheSource(int w, int h)
    {
        var px = Picture(w, h);
        var data = WebPEncoder.EncodeLossy(px, w, h, quality: 0.9);
        var decoded = DecodeRgba(data);
        Assert.True(decoded.Width == w && decoded.Height == h);
        double quality = Psnr(px, decoded.Pixels);
        Assert.True(quality > (w * h < 1000 ? 24 : 32), $"PSNR {quality}");
    }

    [Fact]
    public void QualityTradesSizeForFidelity()
    {
        int w = 640, h = 480;
        var px = Picture(w, h);
        var sizes = new List<int>();
        var fidelity = new List<double>();
        foreach (double q in new[] { 0.3, 0.6, 0.9 })
        {
            var data = WebPEncoder.EncodeLossy(px, w, h, q);
            sizes.Add(data.Length);
            fidelity.Add(Psnr(px, DecodeRgba(data).Pixels));
        }
        int lossless = WebPEncoder.Encode(px, w, h).Length;
        Assert.True(sizes[0] < sizes[1] && sizes[1] < sizes[2], string.Join(" ", sizes));
        Assert.True(fidelity[0] < fidelity[1] && fidelity[1] < fidelity[2], string.Join(" ", fidelity));
        Assert.True(sizes[2] < lossless / 3, $"{sizes[2]} vs {lossless}");
        Assert.True(fidelity[0] > 26, $"{fidelity[0]}");
    }

    [Fact]
    public void AlphaIsKeptExactly()
    {
        int w = 200, h = 96;
        var px = Picture(w, h, alpha: true);
        var data = WebPEncoder.EncodeLossy(px, w, h, quality: 0.8);
        var decoded = DecodeRgba(data);
        for (int i = 3; i < px.Length; i += 4) Assert.Equal(px[i], decoded.Pixels[i]);
        // Opaque pixels keep their colour (decoded is premultiplied, so compare where alpha is 255).
        Assert.True(Psnr(px, decoded.Pixels, i => px[i + 3] == 255) > 30);
    }

    [Fact]
    public void FlatImagesAreTiny()
    {
        byte[] colour = [90, 140, 200, 255];
        var px = Enumerable.Range(0, 320 * 240 * 4).Select(i => colour[i % 4]).ToArray();
        var data = WebPEncoder.EncodeLossy(px, 320, 240, quality: 0.75);
        Assert.True(data.Length < 600, $"{data.Length}");
        var decoded = DecodeRgba(data);
        Assert.True(Math.Abs(decoded.Pixels[0] - 90) <= 3 && Math.Abs(decoded.Pixels[2] - 200) <= 3);
    }

    /// <summary>Encodes a real photo when GLIMMER_PHOTO names one, and reports size and fidelity at three qualities.</summary>
    [OptInFact("GLIMMER_PHOTO")]
    public void EncodesAPhoto()
    {
        var (px, w, h) = DecodeRgba(File.ReadAllBytes(Environment.GetEnvironmentVariable("GLIMMER_PHOTO")!));
        var lines = new List<string>();
        foreach (int q in new[] { 50, 80, 95 })
        {
            var start = System.Diagnostics.Stopwatch.StartNew();
            var data = WebPEncoder.EncodeLossy(px, w, h, q / 100.0);
            lines.Add($"q{q}: {data.Length / 1024} kB, PSNR {Psnr(px, DecodeRgba(data).Pixels):F2}, {start.Elapsed.TotalSeconds:F1} s");
        }
        File.WriteAllLines(Environment.GetEnvironmentVariable("GLIMMER_PHOTO") + ".webp-report.txt", lines);
    }
}
