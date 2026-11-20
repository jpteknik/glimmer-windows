// New file, no Swift original. The Swift tests embed the system's own sRGB profile (CGColorSpace.copyICCData()).
// SkiaSharp has no call that gives a profile's bytes, but its PNG encoder embeds the profile Skia writes for a
// colour space in the file's iCCP chunk; this takes it from there.
using System.Buffers.Binary;
using System.IO.Compression;
using SkiaSharp;

namespace Glimmer.Formats.Tests;

internal static class TestProfiles
{
    /// <summary>The ICC profile Skia embeds for sRGB, or null when its PNG encoder doesn't embed one.</summary>
    public static ReadOnlyMemory<byte>? Srgb() => FromPng(SKColorSpace.CreateSrgb());

    public static ReadOnlyMemory<byte>? FromPng(SKColorSpace space)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul, space));
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray();
        int at = 8;
        while (at + 12 <= bytes.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at));
            string type = System.Text.Encoding.ASCII.GetString(bytes, at + 4, 4);
            if (type == "iCCP")
            {
                var body = bytes.AsSpan(at + 8, length);
                int nul = body.IndexOf((byte)0);
                // The profile's name, a null, the compression method (0 = zlib), then the compressed profile.
                using var input = new MemoryStream(body[(nul + 2)..].ToArray());
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                return output.ToArray();
            }
            at += 12 + length;
        }
        return null;
    }

    /// <summary>The ICC profile Skia embeds for <paramref name="space"/> in a WebP (its ICCP chunk): sRGB too, which its
    /// PNG encoder marks with an sRGB chunk instead.</summary>
    public static byte[]? FromWebP(SKColorSpace space)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul, space));
        using var pixmap = bitmap.PeekPixels();
        using var webp = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 75))
                         ?? throw new InvalidOperationException("Skia wrote no WebP.");
        return ChunkOfWebP(webp.ToArray(), "ICCP");
    }

    /// <summary>The body of a WebP file's first chunk of <paramref name="type"/>, or null.</summary>
    public static byte[]? ChunkOfWebP(byte[] webp, string type)
    {
        int at = 12;
        while (at + 8 <= webp.Length)
        {
            string name = System.Text.Encoding.ASCII.GetString(webp, at, 4);
            int length = BinaryPrimitives.ReadInt32LittleEndian(webp.AsSpan(at + 4));
            if (name == type) return webp.AsSpan(at + 8, length).ToArray();
            at += 8 + length + (length & 1);
        }
        return null;
    }
}
