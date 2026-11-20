// New file, no Swift original: pictures for the import tests that SkiaSharp's own encoders don't write (16-bit and
// grey PNGs, PNGs with a chosen profile, JPEGs with an EXIF orientation), written by hand from the PNG and JPEG
// specifications. The Swift tests make theirs with ImageIO.
using System.IO.Compression;
using System.Text;

namespace Glimmer.Formats.Tests;

internal static class TestImages
{
    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in bytes) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static byte[] Be32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    public static byte[] PngChunk(string type, ReadOnlySpan<byte> body)
    {
        byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. body];
        return [.. Be32((uint)body.Length), .. typed, .. Be32(Crc(typed))];
    }

    public static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Zlib(ReadOnlySpan<byte> raw)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        return output.ToArray();
    }

    /// <summary>A PNG from samples: <paramref name="colorType"/> 0 grey, 2 RGB, 4 grey+alpha, 6 RGBA; 8 or 16 bits.
    /// <paramref name="extraChunks"/> go before the image data (an iCCP, a gAMA).</summary>
    public static byte[] Png(int width, int height, int colorType, int bitDepth, ushort[] samples, params (string Type, byte[] Body)[] extraChunks)
    {
        int channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
        int bytes = bitDepth / 8;
        int rowBytes = width * channels * bytes;
        var raw = new byte[(rowBytes + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int at = y * (rowBytes + 1) + 1;   // filter byte 0 (none)
            for (int i = 0; i < width * channels; i++)
            {
                ushort v = samples[y * width * channels + i];
                if (bytes == 2) { raw[at + i * 2] = (byte)(v >> 8); raw[at + i * 2 + 1] = (byte)v; }
                else raw[at + i] = (byte)v;
            }
        }
        byte[] ihdr = [.. Be32((uint)width), .. Be32((uint)height), (byte)bitDepth, (byte)colorType, 0, 0, 0];
        var file = new List<byte>(PngSignature);
        file.AddRange(PngChunk("IHDR", ihdr));
        foreach (var (type, body) in extraChunks) file.AddRange(PngChunk(type, body));
        file.AddRange(PngChunk("IDAT", Zlib(raw)));
        file.AddRange(PngChunk("IEND", []));
        return [.. file];
    }

    /// <summary>An iCCP chunk's body for a profile.</summary>
    public static byte[] Iccp(ReadOnlySpan<byte> profile) => [.. "profile"u8.ToArray(), 0, 0, .. Zlib(profile)];

    /// <summary>A JPEG with an APP1 Exif segment holding only an orientation tag, right after its start marker.</summary>
    public static byte[] WithOrientation(byte[] jpeg, ushort orientation)
    {
        var tiff = new List<byte> { 0x4D, 0x4D, 0, 42, 0, 0, 0, 8, 0, 1 };   // big-endian TIFF, directory at 8, one entry
        tiff.AddRange([0x01, 0x12, 0, 3, 0, 0, 0, 1, (byte)(orientation >> 8), (byte)orientation, 0, 0, 0, 0, 0, 0]);
        var body = new List<byte>("Exif\0\0"u8.ToArray());
        body.AddRange(tiff);
        var app1 = new List<byte> { 0xFF, 0xE1, (byte)((body.Count + 2) >> 8), (byte)(body.Count + 2) };
        app1.AddRange(body);
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }
}
