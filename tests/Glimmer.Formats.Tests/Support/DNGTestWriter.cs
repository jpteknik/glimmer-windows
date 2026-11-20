// Ported from the DNGTestWriter enum in Packages/GlimmerKit/Tests/GlimmerRenderTests/RawDevelopmentTests.swift: the same
// bytes. TestDngDecoder (new) stands in for the system decoder that reads them.
#if GLIMMER_RAW
using System.Buffers.Binary;
using System.Text;

namespace Glimmer.Formats.Tests;

/// <summary>Writes a small DNG (a Bayer mosaic in a TIFF with the DNG tags Apple's decoder asks for), so the tests need
/// no camera file. <c>scene</c> gives red, green and blue from 0 to 1 per pixel.</summary>
internal static class DNGTestWriter
{
    private static byte[] Le16(ushort v) => [(byte)(v & 0xFF), (byte)(v >> 8)];
    private static byte[] Le32(uint v) => [.. Le16((ushort)(v & 0xFFFF)), .. Le16((ushort)(v >> 16))];

    private sealed record Tag(ushort Id, ushort Type, uint Count, byte[] Value);

    private static Tag Short(ushort id, params ushort[] v) => new(id, 3, (uint)v.Length, [.. v.SelectMany(Le16)]);
    private static Tag Long(ushort id, params uint[] v) => new(id, 4, (uint)v.Length, [.. v.SelectMany(Le32)]);
    private static Tag Byte(ushort id, params byte[] v) => new(id, 1, (uint)v.Length, v);
    private static Tag Ascii(ushort id, string s) { byte[] d = [.. Encoding.UTF8.GetBytes(s), 0]; return new(id, 2, (uint)d.Length, d); }
    private static Tag Rational(ushort id, params (uint N, uint D)[] v) => new(id, 5, (uint)v.Length, [.. v.SelectMany(r => Le32(r.N).Concat(Le32(r.D)))]);
    private static Tag SRational(ushort id, params (int N, int D)[] v) => new(id, 10, (uint)v.Length, [.. v.SelectMany(r => Le32(unchecked((uint)r.N)).Concat(Le32(unchecked((uint)r.D))))]);

    public static byte[] Data(int width = 640, int height = 480, Func<int, int, (double R, double G, double B)>? scene = null)
    {
        scene ??= (_, _) => (0.5, 0.5, 0.5);
        var pixels = new byte[width * height * 2];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = scene(x, y);
                // The mosaic: red, green / green, blue.
                double v = y % 2 == 0 ? (x % 2 == 0 ? r : g) : (x % 2 == 0 ? g : b);
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan((y * width + x) * 2), (ushort)Math.Round(Math.Clamp(v, 0, 1) * 60_000, MidpointRounding.AwayFromZero));
            }
        }
        (int, int)[] identity = [(10000, 10000), (0, 10000), (0, 10000), (0, 10000), (10000, 10000), (0, 10000), (0, 10000), (0, 10000), (10000, 10000)];
        var tags = new List<Tag>
        {
            Long(254, 0), Long(256, (uint)width), Long(257, (uint)height), Short(258, 16), Short(259, 1), Short(262, 32803),
            Ascii(271, "Glimmer Tests"), Ascii(272, "Glimmer Test Camera"), Long(273, 0), Short(274, 1), Short(277, 1),
            Long(278, (uint)height), Long(279, (uint)pixels.Length), Short(284, 1), Ascii(305, "Glimmer tests"),
            Short(33421, 2, 2), Byte(33422, 0, 1, 1, 2),
            Byte(50706, 1, 4, 0, 0), Byte(50707, 1, 1, 0, 0), Ascii(50708, "Glimmer Test Camera"), Byte(50710, 0, 1, 2), Short(50711, 1), Short(50713, 1, 1),
            Rational(50714, (0, 1)), Long(50717, 65535), Rational(50718, (1, 1), (1, 1)), Rational(50719, (0, 1), (0, 1)),
            Rational(50720, ((uint)width, 1), ((uint)height, 1)), SRational(50721, identity), SRational(50722, identity),
            Rational(50727, (1, 1), (1, 1), (1, 1)), Rational(50728, (1, 1), (1, 1), (1, 1)), SRational(50730, (0, 1)), Rational(50731, (1, 1)),
            Rational(50732, (1, 1)), Short(50778, 21), Short(50779, 21), Long(50829, 0, 0, (uint)height, (uint)width),
        };
        tags.Sort((a, b) => a.Id.CompareTo(b.Id));
        const int ifdOffset = 8;
        int ifdSize = 2 + tags.Count * 12 + 4;
        var extra = new List<byte>();
        var entries = new List<byte>();
        int strip = 0;
        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            if (tag.Id == 273) strip = i;
            entries.AddRange(Le16(tag.Id)); entries.AddRange(Le16(tag.Type)); entries.AddRange(Le32(tag.Count));
            if (tag.Value.Length <= 4)
            {
                entries.AddRange(tag.Value); entries.AddRange(new byte[4 - tag.Value.Length]);
            }
            else
            {
                entries.AddRange(Le32((uint)(ifdOffset + ifdSize + extra.Count)));
                extra.AddRange(tag.Value);
                if (tag.Value.Length % 2 == 1) extra.Add(0);
            }
        }
        var stripOffset = Le32((uint)(ifdOffset + ifdSize + extra.Count));
        for (int k = 0; k < 4; k++) entries[strip * 12 + 8 + k] = stripOffset[k];
        return [.. "II"u8.ToArray(), .. Le16(42), .. Le32(ifdOffset), .. Le16((ushort)tags.Count), .. entries, .. Le32(0), .. extra, .. pixels];
    }

    /// <summary>A grey ramp from dark on the left to light on the right, with a warm patch in the lower right quarter.</summary>
    public static readonly byte[] Ramp = Data(scene: (x, y) =>
    {
        double v = 0.05 + 0.6 * x / 640;
        return x > 400 && y > 300 ? (v * 1.2, v * 0.9, v * 0.6) : (v, v, v);
    });
}

/// <summary>
/// New: the tests' stand-in for the system's RAW decoder (WIC's codecs on Windows, which the tests can't reach). It reads
/// the DNGs DNGTestWriter writes (classic little-endian TIFF, one uncompressed strip of 16-bit RGGB samples, identity
/// colour matrices, white level 65535), fills in the mosaic's missing colours from their neighbours (bilinear), and gives
/// the picture as shot in linear light. Like a system decoder it reads the size from the header, refuses what it doesn't
/// understand, and says the white balance the file was shot with (daylight here: its neutral is equal parts).
/// </summary>
internal sealed class TestDngDecoder : IPlatformRawDecoder
{
    public int Decodes;

    private sealed record Header(int Width, int Height, long StripOffset, long StripBytes, bool Dng);

    private static Header? Read(ReadOnlySpan<byte> d)
    {
        if (d.Length < 16 || d[0] != 'I' || d[1] != 'I' || BinaryPrimitives.ReadUInt16LittleEndian(d[2..]) != 42) return null;
        uint ifd = BinaryPrimitives.ReadUInt32LittleEndian(d[4..]);
        if (ifd + 2 > d.Length) return null;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(d[(int)ifd..]);
        if (ifd + 2 + count * 12L > d.Length) return null;
        long width = 0, height = 0, offset = -1, bytes = -1;
        bool dng = false;
        for (int i = 0; i < count; i++)
        {
            int at = (int)ifd + 2 + i * 12;
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(d[at..]), type = BinaryPrimitives.ReadUInt16LittleEndian(d[(at + 2)..]);
            uint value = type == 3 ? BinaryPrimitives.ReadUInt16LittleEndian(d[(at + 8)..]) : BinaryPrimitives.ReadUInt32LittleEndian(d[(at + 8)..]);
            switch (tag)
            {
                case 256: width = value; break;
                case 257: height = value; break;
                case 273: offset = value; break;
                case 279: bytes = value; break;
                case 50706: dng = true; break;
            }
        }
        if (width < 2 || height < 2 || width > 100_000 || height > 100_000 || offset < 0 || bytes != width * height * 2) return null;
        return new Header((int)width, (int)height, offset, bytes, dng);
    }

    public bool IsRaw(ReadOnlyMemory<byte> data) => Read(data.Span) is { Dng: true };

    public (int Width, int Height)? ReadSize(ReadOnlyMemory<byte> data) => Read(data.Span) is { } h ? (h.Width, h.Height) : null;

    public (double Temperature, double Tint)? AsShotWhiteBalance(ReadOnlyMemory<byte> data) => (6500, 0);

    public IReadOnlyList<string> Cameras => ["Glimmer Test Camera"];

    public DecodedImage Decode(ReadOnlyMemory<byte> data)
    {
        Interlocked.Increment(ref Decodes);
        var h = Read(data.Span) ?? throw new InvalidDataException("Not a DNG this decoder reads.");
        if (h.StripOffset + h.StripBytes > data.Length) throw new InvalidDataException("The picture is cut short.");
        var samples = data.Slice((int)h.StripOffset, (int)h.StripBytes);
        int w = h.Width, height = h.Height;
        float Sample(int x, int y) => BinaryPrimitives.ReadUInt16LittleEndian(samples.Span[((y * w + x) * 2)..]) / 65535f;
        int ColourAt(int x, int y) => y % 2 == 0 ? (x % 2 == 0 ? 0 : 1) : (x % 2 == 0 ? 1 : 2);
        var rgba = new float[w * height * 4];
        var sum = new float[3];
        var n = new int[3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Array.Clear(sum);
                Array.Clear(n);
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int sx = x + dx, sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= w || sy >= height) continue;
                        int c = ColourAt(sx, sy);
                        // The pixel's own colour from itself only; the others from the neighbours that have them.
                        if (c == ColourAt(x, y) && (dx != 0 || dy != 0)) continue;
                        sum[c] += Sample(sx, sy);
                        n[c] += 1;
                    }
                }
                int o = (y * w + x) * 4;
                for (int c = 0; c < 3; c++) rgba[o + c] = n[c] > 0 ? sum[c] / n[c] : 0;
                rgba[o + 3] = 1;
            }
        }
        return DecodedImage.FromLinearRgba(w, height, rgba);
    }
}
#endif
