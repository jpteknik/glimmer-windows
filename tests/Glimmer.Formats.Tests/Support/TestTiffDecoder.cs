// New file, no Swift original. The Mac opens TIFF (and HEIC, RAW, …) with ImageIO; on Windows the app's system decoder
// (WIC) does, through IPlatformImageDecoder, which the tests can't reach. This stands in for it: a small reader of the
// TIFFs TiffWriter writes uncompressed (8-bit RGB or RGBA, chunky, in strips, either byte order), written from the TIFF
// 6.0 specification. Like a real system decoder it throws on anything it doesn't understand, and it reports the size
// from the header before it decodes anything.
using System.Buffers.Binary;

namespace Glimmer.Formats.Tests;

internal sealed class TestTiffDecoder : IPlatformImageDecoder
{
    /// <summary>How often <see cref="Decode"/> was called (the tests check that a refused file never gets that far).</summary>
    public int Decodes;

    private sealed class Directory(ReadOnlyMemory<byte> data, bool little, Dictionary<ushort, (ushort Type, uint Count, int At)> entries)
    {
        private uint U16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(data.Span[at..]) : BinaryPrimitives.ReadUInt16BigEndian(data.Span[at..]);
        private uint U32(int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(data.Span[at..]) : BinaryPrimitives.ReadUInt32BigEndian(data.Span[at..]);

        public bool Has(ushort tag) => entries.ContainsKey(tag);

        public uint[] Values(ushort tag)
        {
            if (!entries.TryGetValue(tag, out var e)) throw new InvalidDataException($"TIFF tag {tag} is missing.");
            int width = e.Type switch { 3 => 2, 4 => 4, _ => throw new InvalidDataException($"TIFF tag {tag} has type {e.Type}.") };
            if (e.Count > 1_000_000) throw new InvalidDataException("Too many values.");
            long at = e.Count * width <= 4 ? e.At + 8 : U32(e.At + 8);
            if (at + e.Count * width > data.Length) throw new InvalidDataException("A value lies outside the file.");
            var values = new uint[e.Count];
            for (int i = 0; i < values.Length; i++) values[i] = width == 2 ? U16((int)at + i * 2) : U32((int)at + i * 4);
            return values;
        }

        public ReadOnlyMemory<byte> Bytes(ushort tag)
        {
            var e = entries[tag];
            long at = e.Count <= 4 ? e.At + 8 : U32(e.At + 8);
            if (at + e.Count > data.Length) throw new InvalidDataException("A value lies outside the file.");
            return data.Slice((int)at, (int)e.Count);
        }
    }

    private static Directory? Read(ReadOnlyMemory<byte> data)
    {
        var b = data.Span;
        if (b.Length < 8) return null;
        bool little = b[0] == 0x49 && b[1] == 0x49, big = b[0] == 0x4D && b[1] == 0x4D;
        if (!little && !big) return null;
        uint U16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(data.Span[at..]) : BinaryPrimitives.ReadUInt16BigEndian(data.Span[at..]);
        uint U32(int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(data.Span[at..]) : BinaryPrimitives.ReadUInt32BigEndian(data.Span[at..]);
        if (U16(2) != 42) return null;
        long ifd = U32(4);
        if (ifd + 2 > b.Length) throw new InvalidDataException("The directory lies outside the file.");
        uint count = U16((int)ifd);
        if (ifd + 2 + count * 12L > b.Length) throw new InvalidDataException("The directory is cut short.");
        var entries = new Dictionary<ushort, (ushort, uint, int)>();
        for (int i = 0; i < count; i++)
        {
            int at = (int)ifd + 2 + i * 12;
            entries[(ushort)U16(at)] = ((ushort)U16(at + 2), U32(at + 4), at);
        }
        return new Directory(data, little, entries);
    }

    public (int Width, int Height)? ReadSize(ReadOnlyMemory<byte> data)
    {
        var d = Read(data);
        if (d is null) return null;
        return ((int)Math.Min(d.Values(256)[0], int.MaxValue), (int)Math.Min(d.Values(257)[0], int.MaxValue));
    }

    public DecodedImage Decode(ReadOnlyMemory<byte> data)
    {
        Interlocked.Increment(ref Decodes);
        var d = Read(data) ?? throw new InvalidDataException("Not a TIFF.");
        int width = (int)d.Values(256)[0], height = (int)d.Values(257)[0];
        int samples = (int)d.Values(277)[0];
        if (d.Values(259)[0] != 1 || d.Values(258).Any(bits => bits != 8) || samples is not (3 or 4))
            throw new InvalidDataException("Only uncompressed 8-bit RGB or RGBA is read here.");
        var offsets = d.Values(273);
        var counts = d.Values(279);
        long rowBytes = (long)width * samples;
        var rgba = new ushort[(long)width * height * 4];
        long row = 0;
        for (int s = 0; s < offsets.Length; s++)
        {
            if (offsets[s] + (long)counts[s] > data.Length) throw new InvalidDataException("A strip lies outside the file.");
            var strip = data.Span.Slice((int)offsets[s], (int)counts[s]);
            for (int at = 0; at + rowBytes <= strip.Length && row < height; at += (int)rowBytes, row++)
            {
                for (int x = 0; x < width; x++)
                {
                    long o = (row * width + x) * 4;
                    for (int c = 0; c < 4; c++) rgba[o + c] = (ushort)((c < samples ? strip[at + x * samples + c] : 255) * 257);
                }
            }
        }
        if (row != height) throw new InvalidDataException("The strips hold fewer rows than the picture has.");
        int orientation = d.Has(274) ? (int)d.Values(274)[0] : 1;
        ReadOnlyMemory<byte>? icc = d.Has(34675) ? d.Bytes(34675) : (ReadOnlyMemory<byte>?)null;
        return DecodedImage.FromRgba16(width, height, rgba, icc, orientation);
    }
}
