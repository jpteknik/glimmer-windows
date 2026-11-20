// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/CoverExportTests.swift. The test photos are 8-bit sRGB
// SKBitmaps where the Swift draws CGImages, and files are read back with SkiaSharp where the Swift uses ImageIO.
using SkiaSharp;
using static Glimmer.Formats.Tests.ExportedFiles;

namespace Glimmer.Formats.Tests;

public class CoverExportTests
{
    /// <summary>A wide test photo: red left third, blue right third, noisy green middle (hard to compress).</summary>
    internal static SKBitmap Photo(int width = 2400, int height = 1200)
    {
        var px = new byte[width * height * 4];
        uint seed = 7;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                seed = unchecked(seed * 1_664_525 + 1_013_904_223);
                int i = (y * width + x) * 4;
                int third = x * 3 / width;
                px[i] = (byte)(third == 0 ? 220 : 0);
                px[i + 1] = third == 1 ? (byte)(seed >> 24) : (byte)0;
                px[i + 2] = (byte)(third == 2 ? 220 : 0);
                px[i + 3] = 255;
            }
        }
        return Srgb8(px, width, height);
    }

    [Fact]
    public void CoverIsTheCentreSquareAtExactlyTheSize()
    {
        using var photo = Photo();
        using var cover = ImageExporter.SquareCover(photo, 3000);  // up from 1200
        Assert.True(cover.Width == 3000 && cover.Height == 3000);
        using var small = ImageExporter.SquareCover(photo, 1400);
        Assert.True(small.Width == 1400 && small.Height == 1400);
        // The centre square of a 2:1 image drops the outer quarters: the edges are the red and blue thirds' inner parts.
        var data = ImageExporter.Encode(small, new ExportOptions(ExportFormat.Png));
        var (px, _, _) = Pixels8(data);
        int row = 700 * 1400 * 4;
        Assert.True(px[row + 10 * 4] > 150 && px[row + 10 * 4 + 2] < 60, "left edge is red");
        Assert.True(px[row + 1390 * 4 + 2] > 150 && px[row + 1390 * 4] < 60, "right edge is blue");
    }

    [Fact]
    public void ByteLimitLowersQualityButNeverTheSize()
    {
        using var photo = Photo();
        using var cover = ImageExporter.SquareCover(photo, 3000);
        const int limit = 900_000;
        var data = ImageExporter.Encode(cover, new ExportOptions(ExportFormat.Jpeg, quality: 0.95, maxBytes: limit), keepSize: true);
        Assert.True(data.Length <= limit, $"{data.Length}");
        Assert.Equal((3000, 3000), Size(data));
    }

    [Fact]
    public void AnImpossibleLimitSaysSo()
    {
        using var photo = Photo();
        using var cover = ImageExporter.SquareCover(photo, 1400);
        var jpeg = Assert.Throws<ExportLimitError>(() =>
            ImageExporter.Encode(cover, new ExportOptions(ExportFormat.Jpeg, maxBytes: 2_000), keepSize: true));
        Assert.Equal(2_000, jpeg.Limit);
        Assert.Equal($"Even at the lowest quality the image is {jpeg.Smallest} bytes, over the limit of 2000.", jpeg.Message);
        Assert.Throws<ExportLimitError>(() =>
            ImageExporter.Encode(cover, new ExportOptions(ExportFormat.Png, maxBytes: 50_000), keepSize: true));
    }

    [Fact]
    public void TransparentAreasGetTheBackground()
    {
        using var empty = Srgb8(new byte[100 * 100 * 4], 100, 100);
        using var cover = ImageExporter.SquareCover(empty, 1400, background: new Glimmer.Core.Vec3(1, 1, 1));
        var data = ImageExporter.Encode(cover, new ExportOptions(ExportFormat.Png));
        var (px, _, _) = Pixels8(data);
        Assert.True(px[0] > 250 && px[3] == 255);
    }
}
