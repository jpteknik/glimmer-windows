// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/ExportLimitTests.swift. The noisy test image is an 8-bit
// sRGB SKBitmap where the Swift draws a CGImage; sizes are read back with SkiaSharp where the Swift uses ImageIO.
using SkiaSharp;
using static Glimmer.Formats.Tests.ExportedFiles;

namespace Glimmer.Formats.Tests;

public class ExportLimitTests
{
    /// <summary>A detailed, noisy image that compresses badly.</summary>
    internal static SKBitmap Noisy(int width = 1600, int height = 1200)
    {
        var px = new byte[width * height * 4];
        Array.Fill(px, (byte)255);
        uint seed = 3;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                seed = unchecked(seed * 1_664_525 + 1_013_904_223);
                int i = (y * width + x) * 4;
                px[i] = (byte)((x * 255 / width + (int)(seed >> 26)) & 255);
                px[i + 1] = (byte)((y * 255 / height + (int)((seed >> 20) & 63)) & 255);
                px[i + 2] = (byte)(seed >> 24);
            }
        }
        return Srgb8(px, width, height);
    }

    [Fact]
    public void LongestSideIsLimited()
    {
        using var noisy = Noisy();
        var data = ImageExporter.Encode(noisy, new ExportOptions(ExportFormat.Png, maxPixelSide: 400));
        Assert.Equal((400, 300), Size(data));
    }

    [Fact]
    public void JpegFitsTheByteLimit()
    {
        const int limit = 120_000;
        using var noisy = Noisy();
        var unlimited = ImageExporter.Encode(noisy, new ExportOptions(ExportFormat.Jpeg, quality: 0.92));
        Assert.True(unlimited.Length > limit, $"{unlimited.Length}");
        var data = ImageExporter.Encode(noisy, new ExportOptions(ExportFormat.Jpeg, quality: 0.92, maxBytes: limit));
        Assert.True(data.Length <= limit, $"{data.Length}");
        Assert.True(data.Length > limit / 3, $"{data.Length}");  // used most of the budget, not the lowest quality
    }

    [Fact]
    public void TheBudgetIsUsedAsFullyAsTheQualityStepsAllow()
    {
        // New: the bisection keeps the best quality that fits, not merely one that fits. One quality step above what it
        // found no longer fits; well below the limit means it stopped at a low quality.
        const int limit = 120_000;
        using var noisy = Noisy();
        var data = ImageExporter.Encode(noisy, new ExportOptions(ExportFormat.Jpeg, quality: 0.92, maxBytes: limit));
        var floor = ImageExporter.Encode(noisy, new ExportOptions(ExportFormat.Jpeg, quality: 0.05));
        Assert.True(data.Length > limit * 0.85, $"{data.Length} of {limit}; the lowest quality gives {floor.Length}");
    }

    [Fact]
    public void ATinyLimitShrinksTheImageToo()
    {
        using var noisy = Noisy(600, 400);
        var data = ImageExporter.Encode(noisy, new ExportOptions(ExportFormat.Webp, quality: 0.9, maxBytes: 6_000));
        Assert.True(data.Length <= 6_000, $"{data.Length}");
        Assert.True(Size(data).Width < 600);
    }

    [Fact]
    public void PngIgnoresTheByteLimit()
    {
        using var noisy = Noisy(200, 100);
        var a = ImageExporter.Encode(noisy, new ExportOptions(ExportFormat.Png));
        var b = ImageExporter.Encode(noisy, new ExportOptions(ExportFormat.Png, maxBytes: 10));
        Assert.Equal(a, b);
    }
}
