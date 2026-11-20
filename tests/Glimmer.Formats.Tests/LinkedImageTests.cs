// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/LinkedImageTests.swift. "Written in place" is checked through
// a handle opened before the write (a replaced file would leave that handle on the old contents) where the Swift
// compares inode numbers, and the format with SkiaSharp where the Swift asks ImageIO for the type.
// aGlimmerDocumentOnlyEverReadsAndWritesGlimmerFiles is not ported: it checks the Mac app's document types (UTType),
// which the Windows app declares in its own manifest.
using SkiaSharp;
using static Glimmer.Formats.Tests.ExportedFiles;

namespace Glimmer.Formats.Tests;

/// <summary>"Edit in Glimmer" from Flimmer: the file keeps its format and its place, and a Glimmer document never
/// becomes an image file.</summary>
public class LinkedImageTests
{
    private static SKBitmap Image(bool alpha = false)
    {
        var px = new byte[64 * 48 * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 204;   // 0.8, 0.2, 0.4 in sRGB
            px[i + 1] = 51;
            px[i + 2] = 102;
            px[i + 3] = alpha ? (byte)128 : (byte)255;
        }
        return Srgb8(px, 64, 48);
    }

    private static string TempFile(string name, byte[]? contents = null)
    {
        var folder = Directory.CreateTempSubdirectory("linked-");
        string path = Path.Combine(folder.FullName, name);
        File.WriteAllBytes(path, contents ?? [0]);
        return path;
    }

    [Fact]
    public void FormatsFollowTheExtension()
    {
        Assert.Equal(ExportFormat.Png, LinkedImage.FormatFor("/a/photo.PNG"));
        Assert.Equal(ExportFormat.Jpeg, LinkedImage.FormatFor("/a/photo.jpeg"));
        Assert.Equal(ExportFormat.Jpeg, LinkedImage.FormatFor("/a/photo.jpg"));
        Assert.Equal(ExportFormat.Tiff, LinkedImage.FormatFor("/a/photo.tif"));
        Assert.Equal(ExportFormat.Webp, LinkedImage.FormatFor("/a/photo.webp"));
        Assert.Null(LinkedImage.FormatFor("/a/photo.psd"));   // PSD opens as a new document instead
        Assert.Null(LinkedImage.Create("/a/clip.mov", "Flimmer"));
        var link = LinkedImage.Create("/a/photo.png", "Flimmer")!;
        Assert.Equal(("photo.png", "Flimmer"), (link.FileName, link.Requester));
        Assert.Equal(link, LinkedImage.Create("/a/photo.png", "Flimmer"));
    }

    [Fact]
    public void PngStaysPngInTheSameFile()
    {
        using var image = Image();
        string path = TempFile("photo.png", ImageExporter.Encode(image, new ExportOptions(ExportFormat.Png)));
        try
        {
            var link = LinkedImage.Create(path, "Flimmer")!;
            // A reader that has the file open, as the other app's watch has it.
            using var watcher = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var halfClear = Image(alpha: true);
            link.Write(halfClear);
            var data = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, data[..8]);   // a PNG
            // Written in place: the handle opened before the write sees the new contents.
            watcher.Position = 0;
            var seen = new byte[data.Length];
            watcher.ReadExactly(seen);
            Assert.Equal(data, seen);
            Assert.Equal(data.Length, watcher.Length);
            Assert.True(File.Exists(path) && !Directory.Exists(path), "never a package");
            Assert.Equal(SKEncodedImageFormat.Png, SKCodec.Create(SKData.CreateCopy(data))!.EncodedFormat);
            Assert.Equal(new[] { "photo.png" }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));   // nothing written beside it
            Assert.Equal(128, Pixels8(data).Pixels[3]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void JpegStaysJpegAndShrinksTheFileProperly()
    {
        // Start with a bigger file than the result: an in-place write must truncate.
        string path = TempFile("frame.jpg", Enumerable.Repeat((byte)0xAB, 2_000_000).ToArray());
        try
        {
            var link = LinkedImage.Create(path, "Flimmer")!;
            using var image = Image();
            link.Write(image);
            var data = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, data[..3]);   // a JPEG
            Assert.True(data.Length < 100_000, $"{data.Length}");
            using var codec = SKCodec.Create(SKData.CreateCopy(data))!;
            Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
            Assert.Equal(64, codec.Info.Width);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
