// Ported from the HostileFiles enum in Packages/GlimmerKit/Tests/GlimmerRenderTests/HostileImageTests.swift.
// The Swift encodes its valid files with ImageIO and its own WebP encoder; here SkiaSharp encodes PNG, JPEG and WebP,
// and Glimmer's TiffWriter the TIFF (uncompressed, which the tests' stand-in for the system decoder reads). HEIC is left
// out: on Windows only the system decoder reads it, and the tests can't reach that.
using System.IO.Compression;
using SkiaSharp;

namespace Glimmer.Formats.Tests;

/// <summary>Broken, lying and oversized image files, and valid ones to break.</summary>
internal static class HostileFiles
{
    /// <summary>A small valid picture (a colour ramp with noise, so every codec has something to chew on): RGBA, 8 bits,
    /// opaque.</summary>
    public static byte[] Picture(int width = 96, int height = 64)
    {
        var px = new byte[width * height * 4];
        uint seed = 7;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                seed = unchecked(seed * 1_664_525 + 1_013_904_223);
                int i = (y * width + x) * 4;
                px[i] = (byte)(x * 255 / width);
                px[i + 1] = (byte)(y * 255 / height);
                px[i + 2] = (byte)(seed >> 24);
                px[i + 3] = 255;
            }
        }
        return px;
    }

    public static byte[] Encoded(byte[] rgba, int width, int height, SKEncodedImageFormat format)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(info, rgba);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>An uncompressed RGB TIFF of the picture, by Glimmer's own writer.</summary>
    public static byte[] Tiff(byte[] rgba, int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++) { rgb[i * 3] = rgba[i * 4]; rgb[i * 3 + 1] = rgba[i * 4 + 1]; rgb[i * 3 + 2] = rgba[i * 4 + 2]; }
        using var stream = new MemoryStream();
        TiffWriter.Write(stream, width, height, (ReadOnlyMemory<byte>)rgb, TiffWriter.Alpha.None, new TiffWriter.Options { Compression = TiffWriter.Compression.None });
        return stream.ToArray();
    }

    public static byte[] Be32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    public static byte[] Be16(ushort v) => [(byte)(v >> 8), (byte)v];

    /// <summary>A minimal flat PSD: header, three empty sections, raw planar RGB.</summary>
    public static byte[] Psd(int width, int height, bool withPixels = true)
    {
        var d = new List<byte>("8BPS"u8.ToArray());
        d.AddRange(Be16(1)); d.AddRange(new byte[6]); d.AddRange(Be16(3));
        d.AddRange(Be32((uint)height)); d.AddRange(Be32((uint)width)); d.AddRange(Be16(8)); d.AddRange(Be16(3));
        d.AddRange(Be32(0)); d.AddRange(Be32(0)); d.AddRange(Be32(0)); d.AddRange(Be16(0));
        if (withPixels) d.AddRange(Enumerable.Repeat((byte)128, width * height * 3));
        return [.. d];
    }

    /// <summary>A well-formed PNG whose header claims <paramref name="width"/> × <paramref name="height"/> (1-bit grey) over
    /// one tiny IDAT.</summary>
    public static byte[] PngClaiming(uint width, uint height)
    {
        byte[] ihdr = [.. Be32(width), .. Be32(height), 1, 0, 0, 0, 0];
        byte[] idat = [0x78, 0x9C, 0x63, 0x60, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01];   // zlib of two zero bytes
        return [.. TestImages.PngSignature, .. TestImages.PngChunk("IHDR", ihdr), .. TestImages.PngChunk("IDAT", idat), .. TestImages.PngChunk("IEND", [])];
    }

    /// <summary>A real decompression bomb: a valid 1-bit PNG of <paramref name="side"/> × <paramref name="side"/> zeros.
    /// The file is around a hundred kilobytes; decoded as RGBA it would be side² × 4 bytes.</summary>
    public static byte[] PngBomb(int side) => OneBitZeros(side, side, CompressionLevel.SmallestSize);

    /// <summary>A valid, fully encoded 1-bit PNG of zeros (like the bomb, at an allowed size).</summary>
    public static byte[] PngClaimingFlat(int width, int height) => OneBitZeros(width, height, CompressionLevel.Fastest);

    private static byte[] OneBitZeros(int width, int height, CompressionLevel level)
    {
        int rowBytes = 1 + (width + 7) / 8;
        var row = new byte[rowBytes];
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, level, leaveOpen: true))
        {
            for (int y = 0; y < height; y++) z.Write(row);
        }
        byte[] ihdr = [.. Be32((uint)width), .. Be32((uint)height), 1, 0, 0, 0, 0];
        return [.. TestImages.PngSignature, .. TestImages.PngChunk("IHDR", ihdr), .. TestImages.PngChunk("IDAT", output.ToArray()), .. TestImages.PngChunk("IEND", [])];
    }

    /// <summary>A little-endian TIFF header whose only directory claims <paramref name="width"/> × <paramref name="height"/>
    /// with no pixel data.</summary>
    public static byte[] TiffClaiming(uint width, uint height)
    {
        static byte[] Le16(ushort v) => [(byte)v, (byte)(v >> 8)];
        static byte[] Le32(uint v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
        static byte[] Entry(ushort tag, ushort type, uint value) => [.. Le16(tag), .. Le16(type), .. Le32(1), .. Le32(value)];
        byte[][] entries = [Entry(256, 4, width), Entry(257, 4, height), Entry(258, 3, 8), Entry(259, 3, 1), Entry(262, 3, 1),
                            Entry(273, 4, 1000), Entry(277, 3, 1), Entry(278, 4, height), Entry(279, 4, 16)];
        return [0x49, 0x49, .. Le16(42), .. Le32(8), .. Le16((ushort)entries.Length), .. entries.SelectMany(e => e), .. Le32(0)];
    }

    /// <summary>Valid files in every format these tests can open (Swift's, without HEIC). A Photoshop file's picture is
    /// read by Glimmer's own reader, which every build has, with or without its PSD import (the Mac's ImageIO reads
    /// it).</summary>
    public static List<(string Name, byte[] Data)> Valid()
    {
        var picture = Picture();
        return
        [
            ("png", Encoded(picture, 96, 64, SKEncodedImageFormat.Png)),
            ("jpeg", Encoded(picture, 96, 64, SKEncodedImageFormat.Jpeg)),
            ("tiff", Tiff(picture, 96, 64)),
            ("webp", Encoded(picture, 96, 64, SKEncodedImageFormat.Webp)),
            ("psd", Psd(96, 64)),
        ];
    }

    /// <summary>Every broken variant of the valid files, plus the liars and the garbage.</summary>
    public static List<(string Name, byte[] Data)> Hostile()
    {
        var output = new List<(string, byte[])>();
        foreach (var (name, data) in Valid())
        {
            foreach (int percent in new[] { 1, 10, 50, 90, 99 })
                output.Add(($"{name}-truncated-{percent}", data[..Math.Max(1, data.Length * percent / 100)]));
            // Flipped bytes at fixed places (the same file every run).
            uint seed = 99;
            for (int round = 0; round < 6; round++)
            {
                var bytes = (byte[])data.Clone();
                for (int i = 0; i < (round + 1) * 4; i++)
                {
                    seed = unchecked(seed * 1_664_525 + 1_013_904_223);
                    int at = (int)((seed >> 8) % (uint)bytes.Length);
                    bytes[at] ^= (byte)((byte)(seed >> 3) | 1);
                }
                output.Add(($"{name}-corrupt-{round}", bytes));
            }
            output.Add(($"{name}-header-only", data[..16]));
            output.Add(($"{name}-then-garbage", [.. data[..64], .. Enumerable.Repeat((byte)0xA5, 4096)]));
        }
        output.Add(("empty", []));
        output.Add(("one-byte", [0xFF]));
        output.Add(("text", "this is not an image\n"u8.ToArray()));
        output.Add(("zeros", new byte[65_536]));
        // (Swift's shift binds tighter than its multiplication: i × (2654435761 >> 7).)
        output.Add(("random", [.. Enumerable.Range(0, 65_536).Select(i => unchecked((byte)(i * (2_654_435_761L >> 7))))]));
        output.Add(("png-claims-100000x100000", PngClaiming(100_000, 100_000)));
        output.Add(("png-claims-max", PngClaiming(0x7FFF_FFFF, 0x7FFF_FFFF)));
        output.Add(("png-claims-zero", PngClaiming(0, 0)));
        output.Add(("png-claims-wide", PngClaiming(32_768, 30_000)));
        output.Add(("psd-claims-300000x300000", Psd(300_000, 300_000, withPixels: false)));
        output.Add(("psd-claims-30000x30000-no-pixels", Psd(30_000, 30_000, withPixels: false)));
        output.Add(("tiff-claims-200000x200000", TiffClaiming(200_000, 200_000)));
        output.Add(("tiff-claims-20000x20000-no-pixels", TiffClaiming(20_000, 20_000)));
        return output;
    }
}
