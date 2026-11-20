// New file, no Swift original: the Mac decodes with ImageIO and draws with CoreGraphics, which its tests take as given.
// The port decodes with SkiaSharp and converts colours itself, so these check that against the specifications (EXIF
// orientation, the sRGB curve), against Skia's own colour conversion (profiles), and the system decoder's place in it.
using System.Numerics;
using Glimmer.Core;
using SkiaSharp;

namespace Glimmer.Formats.Tests;

public class ImageImporterTests
{
    private static void Near(Vector4 expected, Vector4 actual, float tolerance = 0.002f) =>
        Assert.True(Vector4.Abs(expected - actual) is var d && d.X <= tolerance && d.Y <= tolerance && d.Z <= tolerance && d.W <= tolerance,
                    $"expected {expected}, got {actual}");

    private static Vector4 AsHalf(Vector4 v) => new((float)(Half)v.X, (float)(Half)v.Y, (float)(Half)v.Z, (float)(Half)v.W);

    // MARK: Orientation

    /// <summary>Six blocks of colour, three across and two down, each 16 × 16 pixels: A B C over D E F.</summary>
    private static readonly (char Name, byte R, byte G, byte B)[] Blocks =
        [('A', 255, 0, 0), ('B', 0, 255, 0), ('C', 0, 0, 255), ('D', 255, 255, 0), ('E', 0, 255, 255), ('F', 255, 0, 255)];

    private static char Which(Vector4 linear)
    {
        bool r = linear.X > 0.5f, g = linear.Y > 0.5f, b = linear.Z > 0.5f;
        return Blocks.Single(k => (k.R > 0) == r && (k.G > 0) == g && (k.B > 0) == b).Name;
    }

    /// <summary>The upright picture for each EXIF orientation, worked out from the EXIF table (which stored side is the
    /// picture's top and which its left), not from Glimmer's code: rows from the top, separated by "/".</summary>
    [Theory]
    [InlineData(1, "ABC/DEF")]
    [InlineData(2, "CBA/FED")]
    [InlineData(3, "FED/CBA")]
    [InlineData(4, "DEF/ABC")]
    [InlineData(5, "AD/BE/CF")]
    [InlineData(6, "DA/EB/FC")]
    [InlineData(7, "FC/EB/DA")]
    [InlineData(8, "CF/BE/AD")]
    public void EveryExifOrientationTurnsThePictureUpright(int orientation, string upright)
    {
        var rgba = new byte[48 * 32 * 4];
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 48; x++)
            {
                var block = Blocks[y / 16 * 3 + x / 16];
                int i = (y * 48 + x) * 4;
                rgba[i] = block.R; rgba[i + 1] = block.G; rgba[i + 2] = block.B; rgba[i + 3] = 255;
            }
        }
        var jpeg = TestImages.WithOrientation(HostileFiles.Encoded(rgba, 48, 32, SKEncodedImageFormat.Jpeg), (ushort)orientation);
        var image = ImageImporter.Image(jpeg);
        var rows = upright.Split('/');
        Assert.Equal(orientation, image.Orientation);
        Assert.True(image.Width == rows[0].Length * 16 && image.Height == rows.Length * 16, $"{image.Width} × {image.Height}");
        var tiles = ImageImporter.Tiles(image);
        for (int by = 0; by < rows.Length; by++)
        {
            for (int bx = 0; bx < rows[by].Length; bx++)
            {
                Assert.Equal(rows[by][bx], Which(image.PixelAt(bx * 16 + 8, by * 16 + 8)));
                Assert.Equal(rows[by][bx], Which(TilePixels.At(tiles, bx * 16 + 8, by * 16 + 8)));
            }
        }
    }

    // MARK: Colour

    /// <summary>Decision for the port: 8-bit sRGB becomes linear exactly (the sRGB curve in double precision), not
    /// with the 1/4096 steps CoreGraphics rounds to on the Mac.</summary>
    [Fact]
    public void EightBitSrgbBecomesLinearExactly()
    {
        var samples = new ushort[256 * 3];
        for (int v = 0; v < 256; v++) samples[v * 3] = samples[v * 3 + 1] = samples[v * 3 + 2] = (ushort)v;
        var image = ImageImporter.Image(TestImages.Png(256, 1, colorType: 2, bitDepth: 8, samples));
        var tiles = ImageImporter.Tiles(image);
        for (int v = 0; v < 256; v++)
        {
            float exact = (float)ColorMath.SrgbToLinear(v / 255.0);
            Assert.Equal(new Vector4(exact, exact, exact, 1), image.PixelAt(v, 0));
            Assert.Equal(AsHalf(new Vector4(exact, exact, exact, 1)), TilePixels.At(tiles, v, 0));
        }
    }

    /// <summary>Every 16-bit level, measured against the exact conversion rounded to a half float. SkiaSharp hands over a
    /// 16-bit PNG's samples only as half floats, so they are rounded once before the curve; this says by how much.</summary>
    [Fact]
    public void SixteenBitPngsKeepTheirPrecision()
    {
        var samples = new ushort[65536 * 3];
        for (int v = 0; v < 65536; v++) { samples[v * 3] = (ushort)v; samples[v * 3 + 1] = (ushort)(65535 - v); }
        var tiles = ImageImporter.Tiles(ImageImporter.Image(TestImages.Png(256, 256, colorType: 2, bitDepth: 16, samples)));
        int worst = 0;
        for (int v = 0; v < 65536; v++)
        {
            var got = TilePixels.At(tiles, v % 256, v / 256);
            var exact = (Half)(float)ColorMath.SrgbToLinear(v / 65535.0);
            var complement = (Half)(float)ColorMath.SrgbToLinear((65535 - v) / 65535.0);
            worst = Math.Max(worst, Math.Abs(BitConverter.HalfToInt16Bits((Half)got.X) - BitConverter.HalfToInt16Bits(exact)));
            worst = Math.Max(worst, Math.Abs(BitConverter.HalfToInt16Bits((Half)got.Y) - BitConverter.HalfToInt16Bits(complement)));
        }
        Assert.True(worst <= 1, $"{worst} steps of a half float from the exact value");
        // Finer than 8 bits: two levels inside one 8-bit step come out apart.
        Assert.NotEqual(TilePixels.At(tiles, 0x00, 0x80).X, TilePixels.At(tiles, 0x40, 0x80).X);
    }

    [Fact]
    public void GreyAndTransparentPicturesArePremultiplied()
    {
        // An untagged grey PNG is sRGB grey here (SkiaSharp's reading of an untagged file).
        var grey = ImageImporter.Image(TestImages.Png(2, 1, colorType: 0, bitDepth: 8, [0, 128]));
        float g = (float)ColorMath.SrgbToLinear(128 / 255.0);
        Assert.Equal(new Vector4(0, 0, 0, 1), grey.PixelAt(0, 0));
        Assert.Equal(new Vector4(g, g, g, 1), grey.PixelAt(1, 0));
        float half = 128 / 255f;
        var greyAlpha = ImageImporter.Image(TestImages.Png(2, 1, colorType: 4, bitDepth: 8, [255, 128, 128, 0]));
        Assert.Equal(new Vector4(half, half, half, half), greyAlpha.PixelAt(0, 0));
        Assert.Equal(Vector4.Zero, greyAlpha.PixelAt(1, 0));
        var rgba = ImageImporter.Image(TestImages.Png(1, 1, colorType: 6, bitDepth: 8, [255, 0, 0, 128]));
        Assert.Equal(new Vector4(half, 0, 0, half), rgba.PixelAt(0, 0));
    }

    /// <summary>A Display P3 picture, against SkiaSharp's own conversion of the same file to linear sRGB (skcms, a
    /// separate implementation): P3's red lies outside sRGB and keeps its values beyond 0…1.</summary>
    [Fact]
    public void AnEmbeddedProfileIsHonoured()
    {
        using var p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        var profile = TestProfiles.FromPng(p3)!.Value.ToArray();
        ushort[] samples = [255, 0, 0, 128, 128, 128, 10, 200, 30];
        var png = TestImages.Png(3, 1, colorType: 2, bitDepth: 8, samples, ("iCCP", TestImages.Iccp(profile)));
        var image = ImageImporter.Image(png);
        var skia = SkiaLinear(png, 3, 1);
        for (int x = 0; x < 3; x++) Near(skia[x], image.PixelAt(x, 0));
        var red = image.PixelAt(0, 0);
        Assert.True(red.X > 1.2f && red.Y < -0.03f && red.Z < -0.01f, $"{red}");
        // The same picture from the system decoder, with the profile it found: the same colours.
        var tiff = TiffOf([255, 0, 0, 255, 128, 128, 128, 255, 10, 200, 30, 255], 3, 1, profile);
        var fromSystem = ImageImporter.Image(tiff, new TestTiffDecoder());
        for (int x = 0; x < 3; x++) Assert.Equal(image.PixelAt(x, 0), fromSystem.PixelAt(x, 0));
    }

    private static unsafe Vector4[] SkiaLinear(byte[] file, int width, int height)
    {
        using var data = SKData.CreateCopy(file);
        using var codec = SKCodec.Create(data);
        using var linear = SKColorSpace.CreateSrgbLinear();
        var halves = new Half[width * height * 4];
        fixed (Half* p = halves)
            Assert.Equal(SKCodecResult.Success, codec.GetPixels(new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Unpremul, linear), (IntPtr)p));
        return [.. Enumerable.Range(0, width * height).Select(i => new Vector4((float)halves[i * 4], (float)halves[i * 4 + 1], (float)halves[i * 4 + 2], (float)halves[i * 4 + 3]))];
    }

    private static byte[] TiffOf(byte[] rgba, int width, int height, byte[]? profile = null, ushort orientation = 1)
    {
        using var stream = new MemoryStream();
        TiffWriter.Write(stream, width, height, (ReadOnlyMemory<byte>)rgba, TiffWriter.Alpha.Unassociated,
                         new TiffWriter.Options { Compression = TiffWriter.Compression.None, IccProfile = profile });
        var tiff = stream.ToArray();
        if (orientation != 1)
        {
            // TiffWriter writes orientation 1 (tag 274); the file is changed where that entry holds its value.
            int count = tiff[8] | tiff[9] << 8;
            for (int e = 0; e < count; e++)
            {
                int at = 10 + e * 12;
                if ((tiff[at] | tiff[at + 1] << 8) == 274) { tiff[at + 8] = (byte)orientation; tiff[at + 9] = 0; }
            }
        }
        return tiff;
    }

    // MARK: The system decoder

    [Fact]
    public void TheSystemDecoderReadsWhatSkiaSharpCannot()
    {
        var picture = HostileFiles.Picture(8, 4);
        var tiff = HostileFiles.Tiff(picture, 8, 4);
        Assert.Equal(ImageImportError.Kind.Unreadable, Assert.Throws<ImageImportError>(() => ImageImporter.Image(tiff, null)).Reason);
        var decoder = new TestTiffDecoder();
        var image = ImageImporter.Image(tiff, decoder);
        Assert.True(image.Width == 8 && image.Height == 4 && decoder.Decodes == 1);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int i = (y * 8 + x) * 4;
                var expected = new Vector4((float)ColorMath.SrgbToLinear(picture[i] / 255.0), (float)ColorMath.SrgbToLinear(picture[i + 1] / 255.0),
                                           (float)ColorMath.SrgbToLinear(picture[i + 2] / 255.0), 1);
                Assert.Equal(expected, image.PixelAt(x, y));
            }
        }
        // The orientation it reports turns the picture as a JPEG's does.
        var turned = ImageImporter.Image(TiffOf(picture, 8, 4, orientation: 6), decoder);
        Assert.True(turned.Width == 4 && turned.Height == 8);
        Assert.Equal(image.PixelAt(0, 3), turned.PixelAt(0, 0));   // the bottom-left corner comes to the top left
    }

    private sealed class Stand(Func<(int, int)?> size, Func<DecodedImage> decode) : IPlatformImageDecoder
    {
        public int Decodes;
        public (int Width, int Height)? ReadSize(ReadOnlyMemory<byte> data) => size();
        public DecodedImage Decode(ReadOnlyMemory<byte> data) { Decodes += 1; return decode(); }
    }

    [Fact]
    public void TheSystemDecoderIsHeldToTheSameChecks()
    {
        byte[] file = [.. "a file only the system reads"u8];
        static DecodedImage Small() => DecodedImage.FromRgba8(2, 2, new byte[16]);
        ImageImportError.Kind Refused(IPlatformImageDecoder decoder, byte[]? data = null) =>
            Assert.Throws<ImageImportError>(() => ImageImporter.Image(data ?? file, decoder)).Reason;
        // A size over the limits is refused on the header: nothing is decoded.
        var huge = new Stand(() => (100_000, 100_000), Small);
        Assert.Equal(ImageImportError.Kind.TooLarge, Refused(huge));
        Assert.Equal(0, huge.Decodes);
        // A decoder that fails while decoding: the file is damaged.
        Assert.Equal(ImageImportError.Kind.Damaged, Refused(new Stand(() => (2, 2), () => throw new InvalidDataException("broken"))));
        // A picture of another size than the header promised: damaged too, half a picture is never opened.
        Assert.Equal(ImageImportError.Kind.Damaged, Refused(new Stand(() => (4, 4), Small)));
        // A decoder that can't read the header doesn't get to decode.
        var blind = new Stand(() => throw new InvalidOperationException("no"), Small);
        Assert.Equal(ImageImportError.Kind.Unreadable, Refused(blind));
        Assert.Equal(0, blind.Decodes);
        // A TIFF cut short is refused before the decoder sees it.
        var tiff = HostileFiles.Tiff(HostileFiles.Picture(8, 4), 8, 4);
        var decoder = new TestTiffDecoder();
        Assert.Equal(ImageImportError.Kind.Damaged, Refused(decoder, tiff[..^10]));
        Assert.Equal(0, decoder.Decodes);
        // A picture stored turned keeps the size the header gave, and comes out upright.
        var image = ImageImporter.Image(file, new Stand(() => (2, 4), () => DecodedImage.FromRgba8(2, 4, new byte[32], orientation: 6)));
        Assert.True(image.Width == 4 && image.Height == 2);
    }

    [Fact]
    public void ASystemDecodersPictureIsChecked()
    {
        Assert.Throws<ArgumentException>(() => DecodedImage.FromRgba8(2, 2, new byte[15]));
        Assert.Throws<ArgumentOutOfRangeException>(() => DecodedImage.FromRgba16(2, 2, new ushort[16], orientation: 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => DecodedImage.FromLinearRgba(0, 2, []));
        // Floating point: above 1 is kept, NaN becomes 0, and the picture is premultiplied.
        Assert.Equal(new Vector4(2, 0.25f, 0, 0.5f), DecodedImage.FromLinearRgba(1, 1, [4, 0.5f, float.NaN, 0.5f]).PixelAt(0, 0));
    }

    // MARK: Tiles and files

    [Fact]
    public void TilesAreMadeWhereTheyAreAsked()
    {
        var image = DecodedImage.FromRgba8(3, 2, [.. Enumerable.Range(0, 24).Select(i => (byte)(i * 10 + 5))]);
        var tiles = ImageImporter.Tiles(image, (-10, 300));
        Assert.Equal(new[] { new TileCoord(-1, 1) }, tiles.Coords);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
                Assert.Equal(AsHalf(image.PixelAt(x, y)), TilePixels.At(tiles, -10 + x, 300 + y));
        Assert.Equal(Vector4.Zero, TilePixels.At(tiles, -11, 300));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageImporter.Tiles(image, ((1 << 30) - 2, 0)));
    }

    [Fact]
    public void APictureIsOpenedFromAFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"glimmer-image-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, TestImages.Png(2, 1, colorType: 0, bitDepth: 8, [0, 255]));
            Assert.Equal(2, ImageImporter.Image(path).Width);
        }
        finally { File.Delete(path); }
        Assert.Equal(ImageImportError.Kind.Unreadable, Assert.Throws<ImageImportError>(() => ImageImporter.Image(path)).Reason);   // gone
    }
}

/// <summary>A test that makes a file over 2 GB: on Linux and macOS a sparse file, which takes no disk; on Windows it
/// would take the space, so it is skipped there (the check it reaches is the same code).</summary>
public sealed class SparseFileFactAttribute : FactAttribute
{
    public SparseFileFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "a file over 2 GB takes that much disk on Windows";
    }
}

public class LargeFileTests
{
    /// <summary>Decision for the port: files over 2 GB (more than a .NET array holds) are refused, with words, before
    /// anything is read.</summary>
    [SparseFileFact]
    public void AFileOverTwoGigabytesIsRefusedBeforeItIsRead()
    {
        string path = Path.Combine(Path.GetTempPath(), $"glimmer-large-{Guid.NewGuid():N}.psd");
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew))
            {
                file.Write("8BPS"u8);
                file.SetLength(FileInput.MaxBytes + 1);
            }
            var image = Assert.Throws<ImageImportError>(() => ImageImporter.Image(path));
            Assert.Equal(ImageImportError.Kind.FileTooLarge, image.Reason);
            Assert.Equal("The image file is larger than 2 GB, more than Glimmer can open.", image.Message);
#if GLIMMER_PSD
            Assert.Equal("This Photoshop file is larger than 2 GB, more than Glimmer can open.",
                         Assert.Throws<PSDFile.Problem>(() => PSDImport.Document(path)).Message);
#endif
            var project = Assert.Throws<AipsImportError>(() => AipsImporter.ImportProject(path));
            Assert.Equal(AipsImportError.Kind.FileTooLarge, project.Reason);
            Assert.Equal("The .aips project is larger than 2 GB, more than Glimmer can open.", project.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheLimitIsCheckedOnTheFilesLength()
    {
        string path = Path.Combine(Path.GetTempPath(), $"glimmer-small-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, new byte[10]);
            Assert.Equal(10, FileInput.Read(path, () => new InvalidOperationException(), maxBytes: 10).Length);
            Assert.Throws<InvalidOperationException>(() => FileInput.Read(path, () => new InvalidOperationException(), maxBytes: 9));
        }
        finally { File.Delete(path); }
    }
}
