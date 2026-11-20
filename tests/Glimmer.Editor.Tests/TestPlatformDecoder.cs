// Windows only: the tests' stand-in for the system's image decoder (WIC in the app), so that the path for formats
// SkiaSharp can't read is exercised everywhere the tests run.
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Glimmer.Formats;

namespace Glimmer.Editor.Tests;

/// <summary>Reads a made-up format only it knows: "GLTEST" + width and height (int32, little-endian) + 8-bit RGBA with
/// straight alpha. The orientation byte after the size is EXIF's (1 = upright).</summary>
public sealed class TestPlatformDecoder : IPlatformImageDecoder
{
    private static ReadOnlySpan<byte> Magic => "GLTEST"u8;
    private const int Header = 6 + 4 + 4 + 1;

    public int Decodes { get; private set; }

    /// <summary>Installed once for the whole test assembly, as the app does at start-up.</summary>
    [ModuleInitializer]
    internal static void Install() => ImageImporter.PlatformDecoder = new TestPlatformDecoder();

    /// <summary>A file in the format: <paramref name="rgba"/> is width × height × 4 bytes.</summary>
    public static byte[] Encode(int width, int height, byte[] rgba, byte orientation = 1)
    {
        var data = new byte[Header + rgba.Length];
        Magic.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(6), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), height);
        data[14] = orientation;
        rgba.CopyTo(data, Header);
        return data;
    }

    /// <summary>One colour all over.</summary>
    public static byte[] Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = a; }
        return Encode(width, height, rgba);
    }

    public (int Width, int Height)? ReadSize(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        if (span.Length < Header || !span[..6].SequenceEqual(Magic)) return null;
        return (BinaryPrimitives.ReadInt32LittleEndian(span[6..]), BinaryPrimitives.ReadInt32LittleEndian(span[10..]));
    }

    public DecodedImage Decode(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        int width = BinaryPrimitives.ReadInt32LittleEndian(span[6..]), height = BinaryPrimitives.ReadInt32LittleEndian(span[10..]);
        long needed = (long)width * height * 4;
        if (span.Length - Header < needed) throw new InvalidDataException("cut short");
        Decodes++;
        return DecodedImage.FromRgba8(width, height, span.Slice(Header, (int)needed).ToArray(), orientation: span[14]);
    }
}
