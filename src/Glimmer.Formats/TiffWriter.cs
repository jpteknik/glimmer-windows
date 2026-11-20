// New file, no Swift original. The Mac writes TIFF through ImageIO (GlimmerRender/ImageExporter.swift, encodeOnce);
// SkiaSharp has no TIFF encoder, so the Windows port writes baseline TIFF itself, from the published specifications:
// "TIFF Revision 6.0" (Adobe, 1992), "BigTIFF" (the 64-bit form, as libtiff 4 and Photoshop read it) and Adobe's
// "TIFF Technical Note 3" (2005) for the floating-point predictor. Own code; nothing copied from other writers.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Glimmer.Formats;

/// <summary>
/// Writes an RGB or RGBA picture as a TIFF: 8 or 16 bits per sample as unsigned integers, or 32-bit floats (linear,
/// with values above 1 kept), uncompressed or Deflate, with an ICC profile. One image, chunky (R, G, B[, A] per
/// pixel), in strips, little-endian. A file over 4 GB is written as BigTIFF.
/// </summary>
public static class TiffWriter
{
    public enum SampleFormat { UInt8, UInt16, Float32 }

    /// <summary>The fourth sample, as TIFF's ExtraSamples tag names it: none, premultiplied ("associated") or
    /// straight ("unassociated").</summary>
    public enum Alpha { None, Associated, Unassociated }

    /// <summary>TIFF's own numbers: 1 is no compression, 8 is Deflate (zlib).</summary>
    public enum Compression : ushort { None = 1, Deflate = 8 }

    public sealed record Options
    {
        public Compression Compression { get; init; } = Compression.Deflate;
        /// <summary>With Deflate: horizontal differencing for integers (predictor 2), the floating-point predictor for
        /// floats (predictor 3). Smaller files; every reader that knows Deflate knows predictor 2, nearly every one 3.</summary>
        public bool Predictor { get; init; } = true;
        public ReadOnlyMemory<byte>? IccProfile { get; init; }
        public double DotsPerInch { get; init; } = 72;
        /// <summary>Null: classic TIFF, and BigTIFF only when the file would pass 4 GB. True: always BigTIFF.
        /// False: never; a file that needs it is refused.</summary>
        public bool? BigTiff { get; init; }
        /// <summary>How many strips are made at once. <see cref="RowSource"/> is then asked for rows from several threads.</summary>
        public int Parallelism { get; init; } = Environment.ProcessorCount;
    }

    /// <summary>Fills <paramref name="row"/> with row <paramref name="y"/> (0 at the top): the samples interleaved, each
    /// in this computer's byte order. Asked once for every row, possibly from several threads at once.</summary>
    public delegate void RowSource(int y, Span<byte> row);

    /// <summary>Uncompressed data is cut into strips of about this many bytes; so is data before it is compressed.</summary>
    internal const int StripBytes = 256 * 1024;

    public static byte[] Encode(int width, int height, SampleFormat format, Alpha alpha, RowSource rows, Options? options = null)
    {
        using var stream = new MemoryStream();
        Write(stream, width, height, format, alpha, rows, options);
        return stream.ToArray();
    }

    /// <summary>A whole picture of 8-bit samples, row after row.</summary>
    public static void Write(Stream destination, int width, int height, ReadOnlyMemory<byte> samples, Alpha alpha, Options? options = null) =>
        Write(destination, width, height, SampleFormat.UInt8, alpha, Rows(samples, width, height, alpha, 1), options);

    /// <summary>A whole picture of 16-bit samples, row after row.</summary>
    public static void Write(Stream destination, int width, int height, ReadOnlyMemory<ushort> samples, Alpha alpha, Options? options = null) =>
        Write(destination, width, height, SampleFormat.UInt16, alpha, Rows(samples, width, height, alpha, 2), options);

    /// <summary>A whole picture of 32-bit float samples, row after row.</summary>
    public static void Write(Stream destination, int width, int height, ReadOnlyMemory<float> samples, Alpha alpha, Options? options = null) =>
        Write(destination, width, height, SampleFormat.Float32, alpha, Rows(samples, width, height, alpha, 4), options);

    private static RowSource Rows<T>(ReadOnlyMemory<T> samples, int width, int height, Alpha alpha, int bytes) where T : unmanaged
    {
        long perRow = (long)width * (alpha == Alpha.None ? 3 : 4);
        if (samples.Length != perRow * height) throw new ArgumentException($"{samples.Length} samples for a {width} × {height} picture that needs {perRow * height}.", nameof(samples));
        return (y, row) => MemoryMarshal.AsBytes(samples.Span.Slice((int)(y * perRow), (int)perRow)).CopyTo(row);
    }

    public static void Write(Stream destination, int width, int height, SampleFormat format, Alpha alpha, RowSource rows, Options? options = null)
    {
        options ??= new Options();
        int samplesPerPixel = alpha == Alpha.None ? 3 : 4;
        int bytesPerSample = format switch { SampleFormat.UInt8 => 1, SampleFormat.UInt16 => 2, _ => 4 };
        long rowBytesLong = (long)width * samplesPerPixel * bytesPerSample;
        if (width < 1 || height < 1 || rowBytesLong > int.MaxValue / 2)
            throw new ArgumentOutOfRangeException(nameof(width), FormattableString.Invariant($"A TIFF can't be written at {width} × {height} pixels."));
        if (options.Parallelism < 1) throw new ArgumentOutOfRangeException(nameof(options), "Parallelism must be at least 1.");
        int rowBytes = (int)rowBytesLong;
        int rowsPerStrip = Math.Clamp(StripBytes / rowBytes, 1, height);
        int strips = (height + rowsPerStrip - 1) / rowsPerStrip;
        bool compressed = options.Compression == Compression.Deflate;
        bool predicted = compressed && options.Predictor;
        ushort predictor = !predicted ? (ushort)1 : format == SampleFormat.Float32 ? (ushort)3 : (ushort)2;

        byte[] Strip(int s)
        {
            int first = s * rowsPerStrip, count = Math.Min(rowsPerStrip, height - first);
            var raw = new byte[count * rowBytes];
            for (int i = 0; i < count; i++)
            {
                var row = raw.AsSpan(i * rowBytes, rowBytes);
                rows(first + i, row);
                if (predictor == 3) FloatPredict(row, samplesPerPixel);
                else
                {
                    if (predictor == 2) DifferencePredict(row, samplesPerPixel, bytesPerSample);
                    if (!BitConverter.IsLittleEndian) SwapToLittleEndian(row, bytesPerSample);
                }
            }
            if (!compressed) return raw;
            using var output = new MemoryStream(raw.Length / 2 + 64);
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
            return output.ToArray();
        }

        // Compressed strips are made first, in parallel, because their lengths come before them in the file.
        // Uncompressed ones have known lengths and are made while they are written, so nothing the size of the
        // picture is held.
        byte[][]? made = null;
        var lengths = new long[strips];
        if (compressed)
        {
            made = new byte[strips][];
            Parallel.For(0, strips, new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism }, s => made[s] = Strip(s));
            for (int s = 0; s < strips; s++) lengths[s] = made[s].Length;
        }
        else
        {
            for (int s = 0; s < strips; s++) lengths[s] = (long)Math.Min(rowsPerStrip, height - s * rowsPerStrip) * rowBytes;
        }

        var layout = Layout.Make(false, width, height, samplesPerPixel, format, alpha, options, rowsPerStrip, predictor, lengths);
        if (options.BigTiff == true || (layout.End > uint.MaxValue && options.BigTiff is null))
            layout = Layout.Make(true, width, height, samplesPerPixel, format, alpha, options, rowsPerStrip, predictor, lengths);
        if (!layout.Big && layout.End > uint.MaxValue)
            throw new InvalidOperationException("The picture takes more than 4 GB as a TIFF, and BigTIFF was not allowed.");

        destination.Write(layout.Head.Written);
        var pad = new byte[1];
        for (int s = 0; s < strips; s++)
        {
            destination.Write(made is null ? Strip(s) : made[s]);
            if (lengths[s] % 2 == 1) destination.Write(pad);   // the next strip on a word boundary
        }
    }

    /// <summary>Horizontal differencing (predictor 2): every sample less the same sample of the pixel to its left,
    /// in the sample's own width. Computed on this computer's numbers, before they are put in file order.</summary>
    internal static void DifferencePredict(Span<byte> row, int samplesPerPixel, int bytesPerSample)
    {
        if (bytesPerSample == 1)
        {
            for (int i = row.Length - 1; i >= samplesPerPixel; i--) row[i] = (byte)(row[i] - row[i - samplesPerPixel]);
        }
        else
        {
            var values = MemoryMarshal.Cast<byte, ushort>(row);
            for (int i = values.Length - 1; i >= samplesPerPixel; i--) values[i] = (ushort)(values[i] - values[i - samplesPerPixel]);
        }
    }

    /// <summary>The floating-point predictor (predictor 3, TIFF Technical Note 3): the row's bytes are regrouped, the
    /// most significant byte of every sample first, then each byte has the byte one pixel's samples before it
    /// subtracted. The same in either byte order of the file.</summary>
    internal static void FloatPredict(Span<byte> row, int samplesPerPixel)
    {
        const int bytes = 4;
        int count = row.Length / bytes;
        var source = row.ToArray();
        for (int i = 0; i < count; i++)
            for (int b = 0; b < bytes; b++)
                row[(BitConverter.IsLittleEndian ? bytes - 1 - b : b) * count + i] = source[i * bytes + b];
        for (int i = row.Length - 1; i >= samplesPerPixel; i--) row[i] = (byte)(row[i] - row[i - samplesPerPixel]);
    }

    private static void SwapToLittleEndian(Span<byte> row, int bytesPerSample)
    {
        if (bytesPerSample == 2) BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<byte, ushort>(row), MemoryMarshal.Cast<byte, ushort>(row));
        else if (bytesPerSample == 4) BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<byte, uint>(row), MemoryMarshal.Cast<byte, uint>(row));
    }

    /// <summary>The header, the image file directory and the values that don't fit in it; where every strip goes.</summary>
    private sealed class Layout
    {
        public required bool Big;
        public required ByteWriter Head;
        public required long End;

        private const ushort Short = 3, Long = 4, Rational = 5, Undefined = 7, Long8 = 16;

        public static Layout Make(bool big, int width, int height, int samplesPerPixel, SampleFormat format, Alpha alpha, Options options,
                                  int rowsPerStrip, ushort predictor, long[] lengths)
        {
            ushort bits = format switch { SampleFormat.UInt8 => 8, SampleFormat.UInt16 => 16, _ => 32 };
            ushort sampleFormat = format == SampleFormat.Float32 ? (ushort)3 : (ushort)1;
            var (numerator, denominator) = RationalOf(options.DotsPerInch);
            // The entries, by tag, each with its values as little-endian bytes. Strip offsets are filled in below.
            var entries = new List<(ushort Tag, ushort Type, long Count, byte[] Value)>
            {
                (256, Long, 1, Le32(width)),
                (257, Long, 1, Le32(height)),
                (258, Short, samplesPerPixel, Le16s(Enumerable.Repeat(bits, samplesPerPixel))),
                (259, Short, 1, Le16s([(ushort)options.Compression])),
                (262, Short, 1, Le16s([2])),                                       // RGB
                (273, big ? Long8 : Long, lengths.Length, new byte[lengths.Length * (big ? 8 : 4)]),
                (274, Short, 1, Le16s([1])),                                       // the first row is the top
                (277, Short, 1, Le16s([(ushort)samplesPerPixel])),
                (278, Long, 1, Le32(rowsPerStrip)),
                (279, big ? Long8 : Long, lengths.Length, Offsets(lengths, big)),
                (282, Rational, 1, [.. Le32(numerator), .. Le32(denominator)]),
                (283, Rational, 1, [.. Le32(numerator), .. Le32(denominator)]),
                (284, Short, 1, Le16s([1])),                                       // chunky: R, G, B[, A] per pixel
                (296, Short, 1, Le16s([2])),                                       // dots per inch
            };
            if (predictor != 1) entries.Add((317, Short, 1, Le16s([predictor])));
            if (alpha != Alpha.None) entries.Add((338, Short, 1, Le16s([alpha == Alpha.Associated ? (ushort)1 : (ushort)2])));
            entries.Add((339, Short, samplesPerPixel, Le16s(Enumerable.Repeat(sampleFormat, samplesPerPixel))));
            if (options.IccProfile is { Length: > 0 } icc) entries.Add((34675, Undefined, icc.Length, icc.ToArray()));

            int inline = big ? 8 : 4;
            long headerSize = big ? 16 : 8;
            long directorySize = big ? 8 + 20L * entries.Count + 8 : 2 + 12L * entries.Count + 4;
            // Values too long for their entry follow the directory, each on a word boundary; then the strips.
            long at = headerSize + directorySize;
            var where = new long[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Value.Length <= inline) continue;
                where[i] = at;
                at += entries[i].Value.Length + entries[i].Value.Length % 2;
            }
            var offsets = new long[lengths.Length];
            for (int s = 0; s < lengths.Length; s++) { offsets[s] = at; at += lengths[s] + lengths[s] % 2; }
            int stripOffsets = entries.FindIndex(e => e.Tag == 273);
            entries[stripOffsets] = entries[stripOffsets] with { Value = Offsets(offsets, big) };
            if (!big && at > uint.MaxValue) return new Layout { Big = false, Head = new ByteWriter(16), End = at };

            var head = new ByteWriter((int)Math.Min(offsets.Length > 0 ? offsets[0] : at, 1 << 24));
            head.Ascii("II");
            if (big) { head.LE16(43); head.LE16(8); head.LE16(0); head.LE64((ulong)headerSize); }
            else { head.LE16(42); head.LE32((uint)headerSize); }
            if (big) head.LE64((ulong)entries.Count); else head.LE16((ushort)entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                var (tag, type, count, value) = entries[i];
                head.LE16(tag); head.LE16(type);
                if (big) head.LE64((ulong)count); else head.LE32((uint)count);
                if (value.Length <= inline) { head.Write(value); head.Zeros(inline - value.Length); }
                else if (big) head.LE64((ulong)where[i]);
                else head.LE32((uint)where[i]);
            }
            if (big) head.LE64(0); else head.LE32(0);                          // no further image
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Value.Length <= inline) continue;
                head.Write(entries[i].Value);
                if (entries[i].Value.Length % 2 == 1) head.U8(0);
            }
            return new Layout { Big = big, Head = head, End = at };
        }

        private static byte[] Le32(long value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)value); return b; }

        private static byte[] Le16s(IEnumerable<ushort> values)
        {
            var list = values.ToList();
            var b = new byte[list.Count * 2];
            for (int i = 0; i < list.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), list[i]);
            return b;
        }

        private static byte[] Offsets(long[] values, bool big)
        {
            var b = new byte[values.Length * (big ? 8 : 4)];
            for (int i = 0; i < values.Length; i++)
            {
                if (big) BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(i * 8), (ulong)values[i]);
                else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), (uint)values[i]);
            }
            return b;
        }

        /// <summary>The resolution as a fraction: whole dots per inch exactly, anything else to a thousandth.</summary>
        private static (long Numerator, long Denominator) RationalOf(double dpi)
        {
            if (!double.IsFinite(dpi) || dpi <= 0) dpi = 72;
            dpi = Math.Min(dpi, 1_000_000);
            return dpi == Math.Floor(dpi) ? ((long)dpi, 1) : ((long)Math.Round(dpi * 1000), 1000);
        }
    }
}
