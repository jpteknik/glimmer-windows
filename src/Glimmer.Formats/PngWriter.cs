// New file, no Swift original. The Mac writes 16-bit PNG through ImageIO (GlimmerRender/ImageExporter.swift,
// encodeOnce). SkiaSharp's PNG encoder writes 16 bits only from float pixels and holds the whole picture as floats, so
// Glimmer writes 16-bit PNG itself, from the PNG specification (W3C, third edition, 2025): RGBA, 16 bits a sample, the
// adaptive filter choice libpng uses (the filter whose bytes have the smallest sum as signed values), zlib. Own code.
using System.Buffers.Binary;
using System.IO.Compression;

namespace Glimmer.Formats;

/// <summary>Writes 16-bit RGBA PNG (straight alpha), tagged sRGB or with an ICC profile, as Skia tags its 8-bit
/// files.</summary>
internal static class PngWriter
{
    /// <summary>Fills <paramref name="row"/> with row <paramref name="y"/> (0 at the top): R, G, B, A per pixel, in this
    /// computer's byte order.</summary>
    public delegate void RowSource(int y, Span<ushort> row);

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Writes the picture. <paramref name="iccProfile"/> null marks it sRGB (an sRGB chunk, as Skia writes for
    /// sRGB); otherwise the profile goes in an iCCP chunk.</summary>
    public static void Write16(Stream destination, int width, int height, RowSource rows, ReadOnlySpan<byte> iccProfile = default,
                               bool srgb = true)
    {
        const int bytesPerPixel = 8;
        long rowBytesLong = (long)width * bytesPerPixel;
        if (width < 1 || height < 1 || rowBytesLong > int.MaxValue / 8)
            throw new ArgumentOutOfRangeException(nameof(width), FormattableString.Invariant($"A PNG can't be written at {width} × {height} pixels."));
        int rowBytes = (int)rowBytesLong;

        destination.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 16;      // bits a sample
        ihdr[9] = 6;       // RGBA
        // Compression 0 (zlib), filter method 0 (adaptive), no interlace.
        Chunk(destination, "IHDR"u8, ihdr);
        if (srgb && iccProfile.IsEmpty)
        {
            Chunk(destination, "sRGB"u8, [0]);   // perceptual
        }
        else if (!iccProfile.IsEmpty)
        {
            using var body = new MemoryStream();
            body.Write("ICC Profile"u8);
            body.Write([0, 0]);  // the name's end, compression method 0
            using (var z = new ZLibStream(body, CompressionLevel.Optimal, leaveOpen: true)) z.Write(iccProfile);
            Chunk(destination, "iCCP"u8, body.ToArray());
        }

        using (var idat = new IdatStream(destination))
        using (var zlib = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
        {
            var samples = new ushort[width * 4];
            var current = new byte[rowBytes];
            var previous = new byte[rowBytes];
            var trial = new byte[rowBytes];
            var best = new byte[rowBytes + 1];
            for (int y = 0; y < height; y++)
            {
                rows(y, samples);
                for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(current.AsSpan(i * 2), samples[i]);
                long bestSum = long.MaxValue;
                for (byte filter = 0; filter <= 4; filter++)
                {
                    long sum = Filter(filter, current, previous, trial, bytesPerPixel, y == 0, bestSum);
                    if (sum < bestSum)
                    {
                        bestSum = sum;
                        best[0] = filter;
                        trial.CopyTo(best, 1);
                    }
                }
                zlib.Write(best);
                (previous, current) = (current, previous);
            }
        }
        Chunk(destination, "IEND"u8, []);
    }

    /// <summary>Filters <paramref name="row"/> into <paramref name="output"/> and returns the sum of the output's bytes
    /// as signed values (stops early, returning more than <paramref name="limit"/>, when it can't win).</summary>
    private static long Filter(byte filter, ReadOnlySpan<byte> row, ReadOnlySpan<byte> above, Span<byte> output, int bpp, bool firstRow, long limit)
    {
        long sum = 0;
        for (int i = 0; i < row.Length; i++)
        {
            int left = i >= bpp ? row[i - bpp] : 0;
            int up = firstRow ? 0 : above[i];
            int upLeft = i >= bpp && !firstRow ? above[i - bpp] : 0;
            int predicted = filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) >> 1,
                _ => Paeth(left, up, upLeft),
            };
            byte v = (byte)(row[i] - predicted);
            output[i] = v;
            sum += v < 128 ? v : 256 - v;
            if (sum > limit) return sum;
        }
        return sum;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream destination, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        destination.Write(word);
        destination.Write(type);
        destination.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        destination.Write(word);
    }

    /// <summary>Cuts the compressed stream into IDAT chunks.</summary>
    private sealed class IdatStream(Stream destination) : Stream
    {
        private readonly byte[] buffer = new byte[256 * 1024];
        private int used;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] data, int offset, int count) => Write(data.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                int n = Math.Min(data.Length, buffer.Length - used);
                data[..n].CopyTo(buffer.AsSpan(used));
                used += n;
                data = data[n..];
                if (used == buffer.Length) Emit();
            }
        }

        private void Emit()
        {
            if (used == 0) return;
            Chunk(destination, "IDAT"u8, buffer.AsSpan(0, used));
            used = 0;
        }

        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Emit();
            base.Dispose(disposing);
        }
    }
}

/// <summary>The CRC-32 of PNG and zlib (polynomial 0xEDB88320, reflected).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Make();

    private static uint[] Make()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>Continues a running CRC (start with 0xFFFFFFFF, finish by inverting).</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
