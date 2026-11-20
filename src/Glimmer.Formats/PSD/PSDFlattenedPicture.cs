// Ported from Packages/GlimmerKit/Sources/GlimmerCore/PSD/PSDFile.swift (the header, the image resources and the
// flattened picture) and new for the rest. These parts are compiled into every build, also one made without
// GLIMMER_PSD: the Mac shows a Photoshop file's flattened picture through ImageIO whether or not its own PSD import is
// in the build, and Windows has no system reader for the format, so Glimmer reads the picture itself. The layers, the
// import and the writer stay behind the switch (PSDFile.cs, PSDImport.cs, PSDWriter.cs).
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using Glimmer.Core;

namespace Glimmer.Formats;

/// <summary>The part of the reader every build has: see PSDFile.cs for the type.</summary>
public sealed partial record PSDFile
{
    /// <summary>The colour modes, with the header's numbers as values (Swift raw values, <c>UInt16</c>).</summary>
    public enum ColorMode : ushort { Bitmap = 0, Grayscale = 1, Indexed = 2, Rgb = 3, Cmyk = 4, Multichannel = 7, Duotone = 8, Lab = 9 }

    /// <summary>
    /// Swift's <c>PSDFile.Header</c>. C# can't give a nested type and a member the same name, and the member
    /// (<see cref="Header"/>) keeps it.
    /// </summary>
    public readonly record struct FileHeader
    {
        /// <summary>PSB (version 2).</summary>
        public bool IsLarge { get; init; }
        /// <summary>1…56.</summary>
        public int Channels { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        /// <summary>1, 8, 16 or 32 bits per channel.</summary>
        public int Depth { get; init; }
        public ColorMode Mode { get; init; }

        public FileHeader(bool isLarge, int channels, int width, int height, int depth, ColorMode mode)
        {
            IsLarge = isLarge; Channels = channels; Width = width; Height = height; Depth = depth; Mode = mode;
        }
    }

    /// <summary>Why a file can't be read, or a file can't be written, in words a person can read.</summary>
    /// <remarks>Swift's <c>PSDFile.Problem</c> (an <c>Error</c>); its <c>message</c> is <see cref="Exception.Message"/>.</remarks>
    public sealed class Problem : Exception
    {
        public Problem(string message) : base(message) { }
    }

    /// <summary>The largest plane read: the largest array .NET can make (Swift's <c>Data</c> has no such limit).</summary>
    internal static readonly long MaxPlaneBytes = Array.MaxLength;

    /// <summary>The header alone: cheap, and enough to refuse a file before anything else is read.</summary>
    public static FileHeader ReadHeader(ReadOnlyMemory<byte> data)
    {
        var r = new PSDBytes(data);
        if (r.Ascii(4) != "8BPS") throw new Problem("This isn't a Photoshop file.");
        ushort version = r.U16();
        if (version != 1 && version != 2) throw new Problem(FormattableString.Invariant($"This Photoshop file has a version Glimmer doesn't know ({version})."));
        r.Skip(6);
        long channels = r.U16(), height = r.U32(), width = r.U32();
        long depth = r.U16();
        ushort mode = r.U16();
        if (!Enum.IsDefined((ColorMode)mode)) throw new Problem("This Photoshop file has a color mode Glimmer doesn't know.");
        long side = version == 2 ? 300_000 : 30_000;
        if (channels is < 1 or > 56 || width < 1 || width > side || height < 1 || height > side || depth is not (1 or 8 or 16 or 32))
            throw new Problem("This Photoshop file's header is damaged.");
        return new FileHeader(version == 2, (int)channels, (int)width, (int)height, (int)depth, (ColorMode)mode);
    }

    // MARK: Image resources

    /// <summary>
    /// Whether the file carries a real flattened picture. Photoshop says so itself, in the
    /// "version info" resource (1057): a file saved with Maximize Compatibility off holds a
    /// blank picture in its place (seen with real files, 2026-11-16). Null when the file
    /// doesn't say (files from other programs often don't).
    /// </summary>
    public static bool? HasFlattenedPicture(ReadOnlyMemory<byte> data)
    {
        if (data.Length < 34) return null;
        var r = new PSDBytes(data);
        if (!r.TrySkip(26) || !r.TryU32(out uint colour) || !r.TrySkip(colour) || !r.TryU32(out uint size) || !r.TrySlice(size, out var resources)) return null;
        while (resources.Remaining >= 12)
        {
            if (!resources.TryAscii(4, out string? signature) || signature != "8BIM" || !resources.TryU16(out ushort id) || !resources.TryU8(out byte nameLength)
                || !resources.TrySkip(nameLength + (nameLength % 2 == 0 ? 1 : 0)) || !resources.TryU32(out uint length) || !resources.TrySlice(length, out var body))
                return null;
            if (length % 2 == 1) resources.TrySkip(1);
            if (id == 1057)
            {
                if (!body.TrySkip(4) || !body.TryU8(out byte flag)) return null;
                return flag != 0;
            }
        }
        return null;
    }

    private static ReadOnlyMemory<byte>? ReadIccProfile(ref PSDBytes r)
    {
        while (r.Remaining >= 12)
        {
            if (!r.TryAscii(4, out string? signature) || signature != "8BIM" || !r.TryU16(out ushort id) || !r.TryU8(out byte nameLength)) return null;
            // A Pascal string padded to an even size, counting its length byte.
            if (!r.TrySkip(nameLength + (nameLength % 2 == 0 ? 1 : 0)) || !r.TryU32(out uint size)) return null;
            if (!r.TrySlice(size, out var body)) return null;
            if (size % 2 == 1) r.TrySkip(1);
            if (id == 1039) return body.Rest;
        }
        return null;
    }

    internal static Problem Damaged() => new("A layer in this Photoshop file is damaged.");

    /// <summary>PackBits, one row. Exactly <c>destination.Length</c> bytes come out or the row is damaged.</summary>
    /// <remarks>Swift appends to a <c>Data</c> (<c>unpackBits(_:into:expecting:)</c>); here the row is written into its place.</remarks>
    internal static void UnpackBits(ReadOnlySpan<byte> row, Span<byte> destination)
    {
        int produced = 0, i = 0, expecting = destination.Length;
        while (i < row.Length && produced < expecting)
        {
            int n = (sbyte)row[i]; i += 1;
            if (n >= 0)
            {
                int count = n + 1;
                if (i + count > row.Length || produced + count > expecting) throw Damaged();
                row.Slice(i, count).CopyTo(destination[produced..]); i += count; produced += count;
            }
            else if (n != -128)
            {
                int count = 1 - n;
                if (i >= row.Length || produced + count > expecting) throw Damaged();
                destination.Slice(produced, count).Fill(row[i]); i += 1; produced += count;
            }
        }
        if (produced != expecting) throw Damaged();
    }

    // MARK: The flattened image

    /// <param name="withBitmap">Also a 1-bit picture, its rows padded to whole bytes (new: <see cref="ReadFlattened"/>
    /// shows a Bitmap file's picture; the reader proper leaves it out, as Swift does).</param>
    private static ImmutableArray<ReadOnlyMemory<byte>> ReadComposite(ref PSDBytes r, FileHeader header, ref long budget, bool withBitmap)
    {
        int bytesPerSample = Math.Max(1, header.Depth / 8);
        if (header.Depth < 8 && !withBitmap) return [];
        long rowBytes = header.Depth == 1 ? ((long)header.Width + 7) / 8 : (long)header.Width * bytesPerSample, planeSize = rowBytes * header.Height;
        if (planeSize * header.Channels > budget || planeSize > MaxPlaneBytes) throw new Problem("The image is too large for this computer.");
        var planes = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>(header.Channels);
        switch (r.U16())
        {
            case 0:
                for (int channel = 0; channel < header.Channels; channel++) planes.Add(r.Slice(planeSize).Rest);
                break;
            case 1:
            {
                // The counts of every row of every channel, then the rows. Their sum is checked against what is left
                // before a plane is allocated.
                var counts = r.Slice((long)header.Height * header.Channels * (header.IsLarge ? 4 : 2));
                var walk = counts;
                long total = 0;
                for (long i = 0; i < (long)header.Height * header.Channels; i++) total += header.IsLarge ? walk.U32() : walk.U16();
                if (total > r.Remaining) throw PSDBytes.CutShort();
                for (int channel = 0; channel < header.Channels; channel++)
                {
                    var output = new byte[planeSize];
                    for (int row = 0; row < header.Height; row++)
                        UnpackBits(r.Slice(header.IsLarge ? counts.U32() : counts.U16()).Rest.Span, output.AsSpan(row * (int)rowBytes, (int)rowBytes));
                    planes.Add(output);
                }
                break;
            }
            default:
                return [];
        }
        budget -= planeSize * header.Channels;
        return planes.ToImmutable();
    }

    /// <summary>A file's flattened picture as its parts (new: on the Mac ImageIO reads it, and Windows has no reader for
    /// Photoshop files).</summary>
    /// <param name="ColorModeData">An indexed file's palette (768 bytes), a duotone's settings, or nothing.</param>
    /// <param name="Planes">One plane per channel in the header, 1-bit rows padded to whole bytes.</param>
    internal sealed record FlattenedPicture(FileHeader Header, ReadOnlyMemory<byte> ColorModeData, ReadOnlyMemory<byte>? IccProfile,
                                            ImmutableArray<ReadOnlyMemory<byte>> Planes);

    /// <summary>
    /// The flattened picture alone: the header, colour mode data, profile and picture, with the layers skipped by their
    /// section's length, so that a file whose layers are damaged still shows its picture. Throws <see cref="Problem"/>
    /// when the picture can't be read (none, cut short, compressed in a way the format doesn't allow, or more than
    /// <paramref name="maxBytes"/>).
    /// </summary>
    internal static FlattenedPicture ReadFlattened(ReadOnlyMemory<byte> data, long maxBytes)
    {
        var header = ReadHeader(data);
        var r = new PSDBytes(data);
        r.Skip(26);
        var colorModeData = r.Slice(r.U32()).Rest;
        var resources = r.Slice(r.U32());
        var icc = ReadIccProfile(ref resources);
        r.Skip(r.Length(header.IsLarge));                         // layer and mask information
        long budget = maxBytes;
        var planes = ReadComposite(ref r, header, ref budget, withBitmap: true);
        if (planes.Length != header.Channels) throw new Problem("This Photoshop file carries no flattened picture Glimmer can read.");
        return new FlattenedPicture(header, colorModeData, icc, planes);
    }
}

/// <summary>A cursor over a file's bytes. Every read checks what is left; nothing trusts a length it was given.</summary>
/// <remarks>Swift's methods are <c>mutating</c> and their cursors <c>inout</c>; here the cursor is passed by <c>ref</c>.
/// The <c>Try</c> forms stand in for Swift's <c>try?</c>: on failure they leave the cursor where it was.</remarks>
internal struct PSDBytes
{
    private readonly ReadOnlyMemory<byte> data;
    private int offset;

    public PSDBytes(ReadOnlyMemory<byte> data) { this.data = data; offset = 0; }

    /// <summary>The keys whose blocks have 8-byte lengths in a PSB, as the specification lists them.</summary>
    public static readonly FrozenSet<string> LongKeys =
        new[] { "LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD" }.ToFrozenSet(StringComparer.Ordinal);

    public readonly int Remaining => data.Length - offset;
    public readonly ReadOnlyMemory<byte> Rest => data[offset..];

    internal const string CutShortMessage = "This Photoshop file is cut short or damaged.";
    public static PSDFile.Problem CutShort() => new(CutShortMessage);

    public void Skip(long count)
    {
        if (count < 0 || count > Remaining) throw CutShort();
        offset += (int)count;
    }

    /// <summary>The next <paramref name="count"/> bytes as a cursor of their own, and this one moved past them.</summary>
    public PSDBytes Slice(long count)
    {
        if (count < 0 || count > Remaining) throw CutShort();
        var slice = new PSDBytes(data.Slice(offset, (int)count));
        offset += (int)count;
        return slice;
    }

    public byte U8()
    {
        if (Remaining < 1) throw CutShort();
        return data.Span[offset++];
    }

    public ushort U16()
    {
        if (Remaining < 2) throw CutShort();
        ushort v = BinaryPrimitives.ReadUInt16BigEndian(data.Span[offset..]);
        offset += 2;
        return v;
    }

    public short I16() => (short)U16();

    public uint U32()
    {
        if (Remaining < 4) throw CutShort();
        uint v = BinaryPrimitives.ReadUInt32BigEndian(data.Span[offset..]);
        offset += 4;
        return v;
    }

    public int I32() => (int)U32();

    /// <summary>A section length: four bytes, or eight in a PSB where the specification says so.</summary>
    public long Length(bool large)
    {
        if (!large) return U32();
        uint high = U32(), low = U32();
        if (high >= 0x8000) throw CutShort();                    // far beyond any file
        return (long)high << 32 | low;
    }

    /// <summary>Text from the file's bytes. (Swift decodes them as UTF-8 too, so ill-formed bytes become U+FFFD.)</summary>
    public string Ascii(long count) => Encoding.UTF8.GetString(Slice(count).data.Span);

    public bool TrySkip(long count)
    {
        if (count < 0 || count > Remaining) return false;
        offset += (int)count;
        return true;
    }

    public bool TrySlice(long count, out PSDBytes slice)
    {
        slice = default;
        if (count < 0 || count > Remaining) return false;
        slice = Slice(count);
        return true;
    }

    public bool TryU8(out byte value)
    {
        value = 0;
        if (Remaining < 1) return false;
        value = U8();
        return true;
    }

    public bool TryU16(out ushort value)
    {
        value = 0;
        if (Remaining < 2) return false;
        value = U16();
        return true;
    }

    public bool TryU32(out uint value)
    {
        value = 0;
        if (Remaining < 4) return false;
        value = U32();
        return true;
    }

    public bool TryI32(out int value)
    {
        value = 0;
        if (Remaining < 4) return false;
        value = I32();
        return true;
    }

    public bool TryAscii(long count, out string? value)
    {
        value = null;
        if (count < 0 || count > Remaining) return false;
        value = Ascii(count);
        return true;
    }
}

/// <summary>
/// A Photoshop file's flattened picture as a picture to open (new: on the Mac ImageIO reads it). RGB and grey at 8, 16
/// and 32 bits with their profile, indexed colour, bitmap, Lab (converted exactly), duotone and multichannel as grey, and
/// CMYK with an uncalibrated conversion (no colour management for inks).
/// </summary>
internal static class PSDFlattenedImage
{
    // MARK: The flattened picture (new: on the Mac ImageIO reads it)

    /// <summary>
    /// A Photoshop file's flattened picture, for <see cref="ImageImporter.Image(ReadOnlyMemory{byte})"/>: the size is
    /// checked on the header before anything is read, and a picture that is cut short or damaged is refused, as for any
    /// other image. Only the colour channels are shown: a further channel (transparency, a spot colour) is left alone,
    /// as <c>PSDAppearance</c> reads the picture on the Mac (an image reader that applies it gets a paler picture).
    /// </summary>
    internal static DecodedImage Image(ReadOnlyMemory<byte> data)
    {
        PSDFile.FileHeader header;
        try { header = PSDFile.ReadHeader(data); }
        catch (PSDFile.Problem) { throw ImageImportError.Unreadable(); }
        ImageImporter.CheckSize(header.Width, header.Height);
        PSDFile.FlattenedPicture picture;
        try { picture = PSDFile.ReadFlattened(data, DocumentLimits.PhysicalMemory / 4); }
        catch (PSDFile.Problem) { throw ImageImportError.Damaged(); }
        return PictureOf(picture) ?? throw ImageImportError.Unreadable();
    }

    private static DecodedImage? PictureOf(PSDFile.FlattenedPicture picture)
    {
        var header = picture.Header;
        int width = header.Width, height = header.Height;
        long pixels = (long)width * height;
        var planes = picture.Planes;
        // (The null is cast: left bare, it would become an empty ReadOnlyMemory through its conversion from an array.)
        ReadOnlyMemory<byte>? icc = picture.IccProfile is { Length: > 0 } p ? p : (ReadOnlyMemory<byte>?)null;
        switch (header.Mode, header.Depth)
        {
            case (PSDFile.ColorMode.Rgb, 8 or 16) when planes.Length >= 3:
                return Interleaved(width, height, header.Depth / 8, [planes[0], planes[1], planes[2]], null,
                                   icc is { } rgbProfile ? SourceColor.FromIcc(rgbProfile.Span, gray: false) ?? SourceColor.Srgb : SourceColor.Srgb);
            case (PSDFile.ColorMode.Grayscale or PSDFile.ColorMode.Duotone or PSDFile.ColorMode.Multichannel, 8 or 16) when planes.Length >= 1:
            {
                // Duotone and multichannel pictures are shown as their first channel in grey.
                var gray = header.Mode == PSDFile.ColorMode.Grayscale && icc is { } grayProfile ? SourceColor.FromIcc(grayProfile.Span, gray: true) : null;
                return Interleaved(width, height, header.Depth / 8, [planes[0]], null, gray ?? SourceColor.GrayGamma22);
            }
            case (PSDFile.ColorMode.Rgb or PSDFile.ColorMode.Grayscale, 32) when planes.Length >= (header.Mode == PSDFile.ColorMode.Rgb ? 3 : 1):
            {
                // 32 bits a channel: linear light in the profile's primaries.
                bool rgb = header.Mode == PSDFile.ColorMode.Rgb;
                int channels = rgb ? 4 : 2, colors = rgb ? 3 : 1;
                var floats = new float[pixels * channels];
                for (int c = 0; c < colors; c++)
                {
                    var plane = planes[c].Span;
                    for (long i = 0; i < pixels; i++) floats[i * channels + c] = BinaryPrimitives.ReadSingleBigEndian(plane.Slice((int)(i * 4), 4));
                }
                for (long i = 0; i < pixels; i++) floats[i * channels + channels - 1] = 1;
                var space = rgb && icc is { } floatProfile ? SourceColor.FromIcc(floatProfile.Span, gray: false)?.AsLinear() : null;
                return DecodedImage.Floats(width, height, channels, floats, space ?? SourceColor.LinearSrgb);
            }
            case (PSDFile.ColorMode.Indexed, 8) when planes.Length >= 1 && picture.ColorModeData.Length >= 768:
            {
                // The palette: 256 reds, 256 greens, 256 blues.
                var palette = picture.ColorModeData.Span;
                var index = planes[0].Span;
                var rgba = new byte[pixels * 4];
                for (long i = 0; i < pixels; i++)
                {
                    int k = index[(int)i];
                    rgba[i * 4] = palette[k]; rgba[i * 4 + 1] = palette[256 + k]; rgba[i * 4 + 2] = palette[512 + k]; rgba[i * 4 + 3] = 255;
                }
                return DecodedImage.Bytes(width, height, 4, rgba, SourceColor.Srgb);
            }
            case (PSDFile.ColorMode.Bitmap, 1) when planes.Length >= 1:
            {
                // One bit a pixel, rows padded to whole bytes; a set bit is black.
                var bits = planes[0].Span;
                int rowBytes = (width + 7) / 8;
                var gray = new byte[pixels];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        gray[(long)y * width + x] = (bits[y * rowBytes + x / 8] & (0x80 >> (x % 8))) != 0 ? (byte)0 : (byte)255;
                return DecodedImage.Bytes(width, height, 1, gray, SourceColor.Srgb);
            }
            case (PSDFile.ColorMode.Lab, 8 or 16) when planes.Length >= 3:
                return Lab(width, height, header.Depth / 8, planes);
            case (PSDFile.ColorMode.Cmyk, 8 or 16) when planes.Length >= 4:
                return Cmyk(width, height, header.Depth / 8, planes);
            default:
                return null;
        }
    }

    private static double Sample(ReadOnlySpan<byte> plane, long i, int bytes) =>
        bytes == 1 ? plane[(int)i] / 255.0 : BinaryPrimitives.ReadUInt16BigEndian(plane.Slice((int)(i * 2), 2)) / 65535.0;

    /// <summary>CIELAB (D50, the profile connection space) to linear sRGB, exactly: L from 0 to 100, a and b centred on
    /// 128 (8 bits) or 32768 (16 bits).</summary>
    private static DecodedImage Lab(int width, int height, int bytes, ImmutableArray<ReadOnlyMemory<byte>> planes)
    {
        long pixels = (long)width * height;
        var m = SourceColor.SrgbFromXyz.Value;
        var output = new float[pixels * 4];
        var lp = planes[0].Span; var ap = planes[1].Span; var bp = planes[2].Span;
        static double Inverse(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - 4.0 / 29);
        for (long i = 0; i < pixels; i++)
        {
            double l = Sample(lp, i, bytes) * 100;
            double a = bytes == 1 ? ap[(int)i] - 128.0 : (BinaryPrimitives.ReadUInt16BigEndian(ap.Slice((int)(i * 2), 2)) - 32768) / 256.0;
            double b = bytes == 1 ? bp[(int)i] - 128.0 : (BinaryPrimitives.ReadUInt16BigEndian(bp.Slice((int)(i * 2), 2)) - 32768) / 256.0;
            double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
            double x = 0.9642 * Inverse(fx), y = Inverse(fy), z = 0.8249 * Inverse(fz);
            output[i * 4] = SourceColor.Sanitize((float)(m[0] * x + m[1] * y + m[2] * z));
            output[i * 4 + 1] = SourceColor.Sanitize((float)(m[3] * x + m[4] * y + m[5] * z));
            output[i * 4 + 2] = SourceColor.Sanitize((float)(m[6] * x + m[7] * y + m[8] * z));
            output[i * 4 + 3] = 1;
        }
        return DecodedImage.Floats(width, height, 4, output, SourceColor.LinearSrgb);
    }

    /// <summary>CMYK without colour management: each colour is what its ink and black leave of white, taken as sRGB. The
    /// file stores CMYK inverted (the highest value is no ink). A colour-managed conversion needs the inks' profile, which
    /// nothing here can apply; the picture is close, not exact.</summary>
    private static DecodedImage Cmyk(int width, int height, int bytes, ImmutableArray<ReadOnlyMemory<byte>> planes)
    {
        long pixels = (long)width * height;
        var output = new float[pixels * 4];
        var c = planes[0].Span; var m = planes[1].Span; var y = planes[2].Span; var k = planes[3].Span;
        for (long i = 0; i < pixels; i++)
        {
            double white = Sample(k, i, bytes);
            output[i * 4] = (float)(Sample(c, i, bytes) * white);
            output[i * 4 + 1] = (float)(Sample(m, i, bytes) * white);
            output[i * 4 + 2] = (float)(Sample(y, i, bytes) * white);
            output[i * 4 + 3] = 1;
        }
        return DecodedImage.Floats(width, height, 4, output, SourceColor.Srgb);
    }

    /// <summary>Colour planes and a transparency plane (none: opaque), big-endian samples as the file stores them, as one picture.</summary>
    internal static DecodedImage Interleaved(int width, int height, int bytes, IReadOnlyList<ReadOnlyMemory<byte>> planes, ReadOnlyMemory<byte>? alpha, SourceColor space)
    {
        int channels = planes.Count == 1 ? 2 : 4;
        long pixels = (long)width * height;
        if (bytes == 1)
        {
            var output = new byte[pixels * channels];
            for (int c = 0; c < planes.Count; c++)
            {
                var p = planes[c].Span;
                for (long i = 0; i < pixels; i++) output[i * channels + c] = p[(int)i];
            }
            if (alpha is { } a) { var p = a.Span; for (long i = 0; i < pixels; i++) output[i * channels + channels - 1] = p[(int)i]; }
            else for (long i = 0; i < pixels; i++) output[i * channels + channels - 1] = 255;   // no transparency channel: opaque
            return DecodedImage.Bytes(width, height, channels, output, space);
        }
        var shorts = new ushort[pixels * channels];
        for (int c = 0; c < planes.Count; c++)
        {
            var p = planes[c].Span;
            for (long i = 0; i < pixels; i++) shorts[i * channels + c] = BinaryPrimitives.ReadUInt16BigEndian(p.Slice((int)(i * 2), 2));
        }
        if (alpha is { } t) { var p = t.Span; for (long i = 0; i < pixels; i++) shorts[i * channels + channels - 1] = BinaryPrimitives.ReadUInt16BigEndian(p.Slice((int)(i * 2), 2)); }
        else for (long i = 0; i < pixels; i++) shorts[i * channels + channels - 1] = ushort.MaxValue;
        return DecodedImage.Shorts(width, height, channels, shorts, space);
    }
}
