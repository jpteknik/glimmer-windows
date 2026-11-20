// Ported from Packages/GlimmerKit/Sources/GlimmerCore/PSD/PSDFile.swift
#if GLIMMER_PSD   // Directory.Build.props switches this (Config/Features.xcconfig on the Mac)
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Glimmer.Core;

namespace Glimmer.Formats;

/// <summary>
/// A Photoshop file as its parts, read from Adobe's published specification ("Adobe Photoshop
/// File Formats Specification", November 2019: "This document is provided for 3rd parties to
/// read and write the Photoshop native file format."). Own code; nothing copied from other readers.
/// </summary>
/// <remarks>
/// A PSD is untrusted input. Nothing is allocated from a number in the file until that number
/// has been checked against the format's limits, Glimmer's own, and the bytes that are left.
/// <para>
/// Port notes. Swift's <c>Data</c> is <see cref="ReadOnlyMemory{T}"/> here: planes read without compression and the
/// kept blocks are slices of the file's own bytes, as they are in Swift. Swift's 64-bit <c>Int</c> sizes are
/// <see cref="long"/> wherever a number from the file is multiplied. A single plane is also held to
/// <see cref="Array.MaxLength"/> bytes, the largest array .NET can make, and a whole file to the same (it is one
/// array); <see cref="FileInput"/> refuses a larger file with words.
/// </para>
/// <para>
/// Two things are read differently from the Mac, as measured against real files (docs/UPSTREAM-NOTES.md, "PSD
/// reader", items 1 and 2): layer masks are read at the document's depth, and the mask record's "real" rectangle is
/// read before its density and feather. Both are marked where they are done.
/// </para>
/// </remarks>
public sealed partial record PSDFile
{
    /// <summary>A rectangle as the file stores one: four signed 32-bit numbers.</summary>
    /// <remarks><see cref="Width"/> and <see cref="Height"/> are 64-bit, as Swift's <c>Int</c> is: the difference of
    /// two 32-bit numbers doesn't always fit in 32 bits.</remarks>
    public readonly record struct Rect
    {
        public int Top { get; init; }
        public int Left { get; init; }
        public int Bottom { get; init; }
        public int Right { get; init; }

        public Rect(int top, int left, int bottom, int right) { Top = top; Left = left; Bottom = bottom; Right = right; }

        public long Width => (long)Right - Left;
        public long Height => (long)Bottom - Top;
    }

    public enum GroupMark { None, Open, Closed, End }

    public sealed record Mask
    {
        public Rect Rect { get; init; }
        /// <summary>What the mask is outside its rectangle: 0 or 255.</summary>
        public byte DefaultColor { get; init; }
        public bool IsDisabled { get; init; }
        /// <summary>One byte per pixel, <c>Rect.Width</c> × <c>Rect.Height</c>, when the file had the channel. (A mask
        /// stored at 16 or 32 bits is brought down to this.)</summary>
        public ReadOnlyMemory<byte>? Pixels { get; init; }
        /// <summary>When the layer has a vector mask as well, the file holds the painted mask a second
        /// time with a rectangle of its own (channel -3). Its plane is read with this rectangle.</summary>
        internal Rect? RealRect { get; init; }
        /// <summary>0…255, when the file gives the mask a density below full.</summary>
        public byte? Density { get; init; }

        public Mask(Rect rect, byte defaultColor, bool isDisabled, ReadOnlyMemory<byte>? pixels)
        {
            Rect = rect; DefaultColor = defaultColor; IsDisabled = isDisabled; Pixels = pixels;
        }

        // Value equality by content, as a Swift struct of these parts would compare.
        public bool Equals(Mask? other) => other is not null && (ReferenceEquals(this, other) ||
            Rect == other.Rect && DefaultColor == other.DefaultColor && IsDisabled == other.IsDisabled && RealRect == other.RealRect
            && Density == other.Density && ByteContent.Equal(Pixels, other.Pixels));

        public override int GetHashCode() => HashCode.Combine(Rect, DefaultColor, IsDisabled, Density, Pixels?.Length);
    }

    public sealed record Layer
    {
        private readonly ImmutableArray<string> extraKeys = [];

        public string Name { get; init; }
        public Rect Rect { get; init; }
        /// <summary>The four-character key, as in the file ("norm", "mul ").</summary>
        public string BlendKey { get; init; }
        public byte Opacity { get; init; }
        public bool IsClipped { get; init; }
        public bool IsVisible { get; init; }
        public GroupMark Group { get; init; }
        public Mask? Mask { get; init; }
        /// <summary>The keys of the additional-information blocks the layer carries ("TySh", "SoLd", "lfx2" …).</summary>
        public ImmutableArray<string> ExtraKeys { get => extraKeys; init => extraKeys = value.IsDefault ? [] : value; }
        /// <summary>The bodies of the blocks Glimmer reads further (fills, vector masks, some adjustments, text), each of bounded size.</summary>
        public ImmutableDictionary<string, ReadOnlyMemory<byte>> Blocks { get; init; } = ImmutableDictionary.Create<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        /// <summary>Decoded planes by channel id (0, 1, 2 = red, green, blue or 0 = gray; -1 = transparency),
        /// each <c>Rect.Width</c> × <c>Rect.Height</c> samples of <see cref="BytesPerSample"/> bytes, big-endian as in the file.</summary>
        public ImmutableDictionary<int, ReadOnlyMemory<byte>> Planes { get; init; }
        public int BytesPerSample { get; init; }

        public Layer(string name, Rect rect, string blendKey, byte opacity, bool isClipped, bool isVisible, GroupMark group, Mask? mask,
                     IEnumerable<string> extraKeys, IReadOnlyDictionary<int, ReadOnlyMemory<byte>> planes, int bytesPerSample)
        {
            Name = name; Rect = rect; BlendKey = blendKey; Opacity = opacity; IsClipped = isClipped; IsVisible = isVisible;
            Group = group; Mask = mask; ExtraKeys = [.. extraKeys]; Planes = planes.ToImmutableDictionary(); BytesPerSample = bytesPerSample;
        }

        public bool Equals(Layer? other) => other is not null && (ReferenceEquals(this, other) ||
            Name == other.Name && Rect == other.Rect && BlendKey == other.BlendKey && Opacity == other.Opacity && IsClipped == other.IsClipped
            && IsVisible == other.IsVisible && Group == other.Group && Equals(Mask, other.Mask) && ExtraKeys.SequenceEqual(other.ExtraKeys)
            && ByteContent.Equal(Blocks, other.Blocks) && ByteContent.Equal(Planes, other.Planes) && BytesPerSample == other.BytesPerSample);

        public override int GetHashCode() => HashCode.Combine(Name, Rect, BlendKey, Opacity, Group, Planes.Count, BytesPerSample);
    }

    private readonly ImmutableArray<Layer> layers = [];
    private readonly ImmutableArray<ReadOnlyMemory<byte>> composite = [];

    public FileHeader Header { get; init; }
    /// <summary>Bottom-most first, as the file stores them.</summary>
    public ImmutableArray<Layer> Layers { get => layers; init => layers = value.IsDefault ? [] : value; }
    /// <summary>The flattened image the file carries: planes in channel order, when it could be read.</summary>
    public ImmutableArray<ReadOnlyMemory<byte>> Composite { get => composite; init => composite = value.IsDefault ? [] : value; }
    public ReadOnlyMemory<byte>? IccProfile { get; init; }

    /// <summary>A file from its parts, for the writer.</summary>
    public PSDFile(FileHeader header, IEnumerable<Layer> layers, IEnumerable<ReadOnlyMemory<byte>> composite, ReadOnlyMemory<byte>? iccProfile)
    {
        Header = header; Layers = [.. layers]; Composite = [.. composite]; IccProfile = iccProfile;
    }

    public bool Equals(PSDFile? other) => other is not null && (ReferenceEquals(this, other) ||
        Header == other.Header && Layers.SequenceEqual(other.Layers) && ByteContent.Equal(Composite, other.Composite)
        && ByteContent.Equal(IccProfile, other.IccProfile));

    public override int GetHashCode() => HashCode.Combine(Header, Layers.Length, Composite.Length);

    /// <summary>What a reader may take from a file before it gives up.</summary>
    public sealed record Limits
    {
        /// <summary>The most pixels the canvas may have (Glimmer's own limit for this computer).</summary>
        public long MaxCanvasPixels { get; init; }
        public int MaxLayers { get; init; } = 8_000;
        /// <summary>The longest a file may take to read. A file built to keep the reader busy is given up on.</summary>
        public double Seconds { get; init; } = 60;
        /// <summary>The most bytes of decoded planes for the whole file.</summary>
        public long MaxDecodedBytes { get; init; }

        /// <summary>Left out, the limits are this computer's (<see cref="DocumentLimits"/>), as in Swift's default arguments.</summary>
        public Limits(long? maxCanvasPixels = null, long? maxDecodedBytes = null)
        {
            MaxCanvasPixels = maxCanvasPixels ?? DocumentLimits.MaxCanvasPixels();
            MaxDecodedBytes = maxDecodedBytes ?? DocumentLimits.PhysicalMemory / 4;
        }
    }

    /// <summary>The additional-information blocks whose contents the import reads.</summary>
    internal static readonly FrozenSet<string> KeptBlocks = new[] { "SoCo", "vmsk", "vsms", "levl", "curv", "nvrt", "TySh" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Reads a file. Throws <see cref="Problem"/> for a file that can't be read, saying why.</summary>
    /// <param name="limits">Left out: this computer's limits.</param>
    public PSDFile(ReadOnlyMemory<byte> data, Limits? limits = null)
    {
        limits ??= new Limits();
        var header = ReadHeader(data);
        // At most 300,000 × 300,000: the product can't overflow a long.
        long pixels = (long)header.Width * header.Height;
        if (pixels > limits.MaxCanvasPixels)
            throw new Problem(DocumentLimits.Refusal(header.Width, header.Height) ?? "The image is too large for this computer.");
        Header = header;
        var r = new PSDBytes(data);
        r.Skip(26);
        r.Skip(r.U32());                                          // colour mode data
        var resources = r.Slice(r.U32());                         // image resources
        IccProfile = ReadIccProfile(ref resources);
        long budget = limits.MaxDecodedBytes;
        long deadline = Deadline(limits.Seconds);
        var section = r.Slice(r.Length(header.IsLarge));          // layer and mask information
        Layers = ReadLayers(ref section, header, limits, ref budget, deadline);
        // The flattened image. A file may be cut short here and still give its layers.
        try { Composite = ReadComposite(ref r, header, ref budget, withBitmap: false); } catch (Problem) { Composite = []; }
    }

    // MARK: Layers

    internal const string TookTooLongMessage = "This Photoshop file takes too long to read, so Glimmer gave up on it.";
    internal static Problem TookTooLong() => new(TookTooLongMessage);

    private static readonly FrozenSet<string> LayerBlocks = new[] { "Lr16", "Lr32" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>A layer while it is read: Swift mutates its <c>Layer</c> in place; here the record is made at the end.</summary>
    private sealed class LayerDraft
    {
        public string Name = "";
        public Rect Rect;
        public string BlendKey = "";
        public byte Opacity;
        public bool IsClipped, IsVisible;
        public GroupMark Group = GroupMark.None;
        public MaskDraft? Mask;
        public readonly List<string> ExtraKeys = [];
        public readonly Dictionary<string, ReadOnlyMemory<byte>> Blocks = new(StringComparer.Ordinal);
        public readonly Dictionary<int, ReadOnlyMemory<byte>> Planes = [];
        public int BytesPerSample;

        public Layer Build() => new(Name, Rect, BlendKey, Opacity, IsClipped, IsVisible, Group, Mask?.Build(), ExtraKeys, Planes, BytesPerSample)
        {
            Blocks = Blocks.ToImmutableDictionary(StringComparer.Ordinal),
        };
    }

    private sealed class MaskDraft
    {
        public Rect Rect;
        public byte DefaultColor;
        public bool IsDisabled;
        public ReadOnlyMemory<byte>? Pixels;
        public Rect? RealRect;
        public byte? Density;

        public Mask Build() => new(Rect, DefaultColor, IsDisabled, Pixels) { RealRect = RealRect, Density = Density };
    }

    private static ImmutableArray<Layer> ReadLayers(ref PSDBytes section, FileHeader header, Limits limits, ref long budget, long deadline)
    {
        if (section.Remaining <= 0) return [];
        long infoLength = section.Length(header.IsLarge);
        var info = section.Slice(infoLength);
        int bytesPerSample = Math.Max(1, header.Depth / 8);
        if (infoLength == 0)
        {
            // 16- and 32-bit documents keep their layers in an additional block ('Lr16', 'Lr32') after the global mask.
            section.Skip(section.U32());                          // global layer mask info
            if (FindBlock(LayerBlocks, ref section, header.IsLarge) is not { } block) return [];
            info = block.Body;
            bytesPerSample = block.Key == "Lr32" ? 4 : 2;
        }
        if (info.Remaining < 2) return [];
        int count = Math.Abs((int)info.I16());
        if (count > limits.MaxLayers) throw new Problem(FormattableString.Invariant($"This Photoshop file says it has {count} layers, more than Glimmer reads."));
        var records = new List<(LayerDraft Layer, List<(int Id, long Length)> Channels)>(count);
        for (int i = 0; i < count; i++)
        {
            if (!InTime(deadline)) throw TookTooLong();
            records.Add(ReadRecord(ref info, header, bytesPerSample));
        }
        var layers = ImmutableArray.CreateBuilder<Layer>(records.Count);
        foreach (var (layer, channels) in records)
        {
            foreach (var (id, length) in channels)
            {
                if (!InTime(deadline)) throw TookTooLong();
                var body = info.Slice(length);
                bool isMask = id == -2 || id == -3;
                Rect? found = id == -3 ? (layer.Mask?.RealRect ?? layer.Mask?.Rect) : isMask ? layer.Mask?.Rect : layer.Rect;
                if (found is not { } rect || rect.Width <= 0 || rect.Height <= 0) continue;
                // A plane no larger than the canvas with a margin: a layer may reach outside it, but not without bound.
                bool overflow = rect.Width > long.MaxValue / rect.Height;
                long samples = overflow ? 0 : rect.Width * rect.Height;
                // Masks are stored at the document's depth like every other channel: read at it, then brought down to
                // the one byte per pixel a Mask holds. (The Mac reads them as 8-bit and so refuses every 16- and 32-bit
                // file that has a mask: docs/UPSTREAM-NOTES.md, "PSD reader", item 1.)
                int sampleBytes = bytesPerSample;
                if (overflow || samples > Math.Max((long)header.Width * header.Height * 4, 1L << 20) || samples * sampleBytes > budget || samples * sampleBytes > MaxPlaneBytes)
                    throw new Problem("A layer in this Photoshop file is larger than Glimmer can read.");
                // Checked above: width × height × bytes fits an array, so each of them fits an int.
                var plane = DecodePlane(ref body, (int)rect.Width, (int)rect.Height, sampleBytes, header.IsLarge);
                budget -= plane.Length;
                if (id == -2) { if (layer.Mask is not null) layer.Mask.Pixels = MaskBytes(plane, sampleBytes); }
                else if (id != -3) layer.Planes[id] = plane;
            }
            layers.Add(layer.Build());
        }
        return layers.MoveToImmutable();
    }

    /// <summary>A mask plane read at 16 or 32 bits as one byte per pixel (new, with the fix above): 16-bit values are
    /// rounded to the nearest of 256 steps, 32-bit ones (0…1) clamped first.</summary>
    internal static ReadOnlyMemory<byte> MaskBytes(ReadOnlyMemory<byte> plane, int bytesPerSample)
    {
        if (bytesPerSample == 1) return plane;
        var source = plane.Span;
        var output = new byte[source.Length / bytesPerSample];
        if (bytesPerSample == 2)
        {
            for (int i = 0; i < output.Length; i++) output[i] = (byte)(((source[i * 2] << 8 | source[i * 2 + 1]) * 255 + 32767) / 65535);
        }
        else
        {
            for (int i = 0; i < output.Length; i++)
            {
                float f = BinaryPrimitives.ReadSingleBigEndian(source.Slice(i * 4, 4));
                output[i] = float.IsNaN(f) ? (byte)0 : (byte)Math.Round(Math.Clamp(f, 0f, 1f) * 255, MidpointRounding.AwayFromZero);
            }
        }
        return output;
    }

    private static bool Within(Rect r, long bound) =>
        Math.Abs((long)r.Top) <= bound && Math.Abs((long)r.Left) <= bound && Math.Abs((long)r.Bottom) <= bound && Math.Abs((long)r.Right) <= bound;

    private static (LayerDraft Layer, List<(int Id, long Length)> Channels) ReadRecord(ref PSDBytes r, FileHeader header, int bytesPerSample)
    {
        var rect = new Rect(r.I32(), r.I32(), r.I32(), r.I32());
        if (rect.Bottom < rect.Top || rect.Right < rect.Left || !Within(rect, 1_000_000))
            throw new Problem("A layer in this Photoshop file has a damaged rectangle.");
        int channelCount = r.U16();
        if (channelCount > 60) throw new Problem("A layer in this Photoshop file has too many channels.");
        var channels = new List<(int Id, long Length)>(channelCount);
        for (int i = 0; i < channelCount; i++)
        {
            int id = r.I16();
            channels.Add((id, r.Length(header.IsLarge)));
        }
        if (r.Ascii(4) != "8BIM") throw new Problem("A layer in this Photoshop file is damaged.");
        string blendKey = r.Ascii(4);
        byte opacity = r.U8(), clipping = r.U8(), flags = r.U8();
        r.Skip(1);
        var extra = r.Slice(r.U32());
        var layer = new LayerDraft
        {
            Rect = rect, BlendKey = blendKey, Opacity = opacity, IsClipped = clipping != 0, IsVisible = (flags & 2) == 0, BytesPerSample = bytesPerSample,
        };
        // Layer mask data.
        var mask = extra.Slice(extra.U32());
        long maskLength = mask.Remaining;
        if (mask.Remaining >= 18)
        {
            var maskRect = new Rect(mask.I32(), mask.I32(), mask.I32(), mask.I32());
            byte color = mask.U8(), maskFlags = mask.U8();
            if (maskRect.Bottom >= maskRect.Top && maskRect.Right >= maskRect.Left)
                layer.Mask = new MaskDraft { Rect = maskRect, DefaultColor = color, IsDisabled = (maskFlags & 2) != 0 };
            // The longer form: the painted mask once more, with its own flags, background and rectangle. It comes before
            // the mask parameters, and is there when the layer has that plane (channel -3) and the record is long enough:
            // psd-tools' order, which real files confirm. (The Mac reads the parameters first and misreads, or refuses,
            // a mask that has both: docs/UPSTREAM-NOTES.md, "PSD reader", item 2.)
            if (channels.Any(c => c.Id == -3) && maskLength >= 36 && mask.TrySkip(2)
                && mask.TryI32(out int top) && mask.TryI32(out int left) && mask.TryI32(out int bottom) && mask.TryI32(out int right)
                && bottom >= top && right >= left && Within(new Rect(top, left, bottom, right), 1_000_000) && layer.Mask is not null)
                layer.Mask.RealRect = new Rect(top, left, bottom, right);
            // Mask parameters (density and feather of the painted and of the vector mask), when the flags say they are there.
            if ((maskFlags & 16) != 0 && mask.TryU8(out byte which))
            {
                // (Swift's `layer.mask?.density = try? mask.u8()` reads nothing when there is no mask.)
                if ((which & 1) != 0 && layer.Mask is not null) layer.Mask.Density = mask.TryU8(out byte density) ? density : null;
                if ((which & 2) != 0) mask.TrySkip(8);
                if ((which & 4) != 0) mask.TrySkip(1);
                if ((which & 8) != 0) mask.TrySkip(8);
            }
        }
        extra.Skip(extra.U32());                                  // blending ranges
        // The name: a Pascal string padded to a multiple of four, counting its length byte.
        int nameLength = extra.U8();
        layer.Name = Encoding.UTF8.GetString(extra.Slice(nameLength).Rest.Span);
        extra.Skip((4 - (nameLength + 1) % 4) % 4);
        // Additional layer information.
        while (extra.Remaining >= 12)
        {
            string signature = extra.Ascii(4);
            if (signature != "8BIM" && signature != "8B64") break;
            string key = extra.Ascii(4);
            long length = extra.Length(header.IsLarge && PSDBytes.LongKeys.Contains(key));
            var body = extra.Slice(length);
            if (length % 2 == 1 && extra.Remaining > 0) extra.Skip(1);
            layer.ExtraKeys.Add(key);
            if (KeptBlocks.Contains(key) && length <= 4 << 20) layer.Blocks[key] = body.Rest;
            switch (key)
            {
                case "luni":
                {
                    long units = body.U32();
                    if (units > body.Remaining / 2) break;
                    // UTF-16, big-endian. Ill-formed text becomes U+FFFD, as Swift's String(decoding:as:) makes it.
                    layer.Name = Encoding.BigEndianUnicode.GetString(body.Slice(units * 2).Rest.Span).Trim('\0');
                    break;
                }
                case "lsct":
                case "lsdk":
                    switch (body.U32())
                    {
                        case 1: layer.Group = GroupMark.Open; break;
                        case 2: layer.Group = GroupMark.Closed; break;
                        case 3: layer.Group = GroupMark.End; break;
                    }
                    // The group's own blend mode follows when the block is long enough.
                    if (body.Remaining >= 8 && body.TryAscii(4, out string? mark) && mark == "8BIM" && body.TryAscii(4, out string? groupKey)) layer.BlendKey = groupKey!;
                    break;
            }
        }
        return (layer, channels);
    }

    private static (string Key, PSDBytes Body)? FindBlock(FrozenSet<string> keys, ref PSDBytes r, bool large)
    {
        while (r.Remaining >= 12)
        {
            string signature = r.Ascii(4);
            if (signature != "8BIM" && signature != "8B64") return null;
            string key = r.Ascii(4);
            long length = r.Length(large && PSDBytes.LongKeys.Contains(key));
            var body = r.Slice(length);
            // Blocks at this level are padded to a multiple of four.
            long pad = (4 - length % 4) % 4;
            if (pad > 0 && r.Remaining >= pad) r.Skip(pad);
            if (keys.Contains(key)) return (key, body);
        }
        return null;
    }

    // MARK: Planes

    /// <summary>One channel's samples: <paramref name="width"/> × <paramref name="height"/> × <paramref name="bytesPerSample"/>
    /// bytes exactly, whatever the file claims. The caller has checked that the plane fits an array.</summary>
    internal static ReadOnlyMemory<byte> DecodePlane(ref PSDBytes r, int width, int height, int bytesPerSample, bool large)
    {
        int rowBytes = width * bytesPerSample, size = rowBytes * height;
        switch (r.U16())
        {
            case 0:
                return r.Slice(size).Rest;
            case 1:
            {
                // The counts, then the rows. (Swift collects the counts in an array; here a second cursor walks them,
                // and their sum is checked against what is left before the plane is allocated.)
                var counts = r.Slice((long)height * (large ? 4 : 2));
                var walk = counts;
                long total = 0;
                for (int y = 0; y < height; y++) total += large ? walk.U32() : walk.U16();
                if (total > r.Remaining) throw PSDBytes.CutShort();
                var output = new byte[size];
                for (int y = 0; y < height; y++)
                    UnpackBits(r.Slice(large ? counts.U32() : counts.U16()).Rest.Span, output.AsSpan(y * rowBytes, rowBytes));
                return output;
            }
            case 2:
                return Inflate(r.Rest, size);
            case 3:
            {
                var output = Inflate(r.Rest, size);
                UndoPrediction(output, width, height, bytesPerSample);
                return output;
            }
            default:
                throw new Problem("A layer in this Photoshop file is compressed in a way Glimmer doesn't know.");
        }
    }

    /// <summary>zlib data into exactly <paramref name="size"/> bytes. The output buffer is the declared size and no larger,
    /// so a file that would expand without end stops there.</summary>
    internal static byte[] Inflate(ReadOnlyMemory<byte> data, int size)
    {
        if (data.Length <= 2 || size <= 0) throw Damaged();
        var output = new byte[size];
        int written = 0;
        try
        {
            // Past the two-byte zlib header: the stream itself is raw deflate, and the checksum after it is not read.
            using var source = ReadOnlyMemoryStream(data[2..]);
            using var inflater = new DeflateStream(source, CompressionMode.Decompress);
            while (written < size)
            {
                int n = inflater.Read(output, written, size - written);
                if (n == 0) break;
                written += n;
            }
            // One byte of room beyond the plane: a stream that holds more than the plane is damaged
            // or hostile, and is refused rather than cut to fit.
            Span<byte> beyond = stackalloc byte[1];
            if (written == size && inflater.Read(beyond) != 0) throw Damaged();
        }
        catch (InvalidDataException)
        {
            throw Damaged();
        }
        if (written != size) throw Damaged();
        return output;
    }

    private static MemoryStream ReadOnlyMemoryStream(ReadOnlyMemory<byte> data) =>
        MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(data.ToArray(), writable: false);

    /// <summary>"ZIP with prediction": each row holds differences from the sample to its left.</summary>
    internal static void UndoPrediction(Span<byte> p, int width, int height, int bytesPerSample)
    {
        if (width <= 1) return;
        int rowBytes = width * bytesPerSample;
        for (int y = 0; y < height; y++)
        {
            int row = y * rowBytes;
            if (bytesPerSample == 2)
            {
                for (int x = 1; x < width; x++)
                {
                    int a = row + x * 2, b = a - 2;
                    ushort sum = (ushort)((p[a] << 8 | p[a + 1]) + (p[b] << 8 | p[b + 1]));
                    p[a] = (byte)(sum >> 8); p[a + 1] = (byte)(sum & 0xFF);
                }
            }
            else if (bytesPerSample == 1)
            {
                for (int x = 1; x < width; x++) p[row + x] = (byte)(p[row + x] + p[row + x - 1]);
            }
            // 32-bit prediction interleaves bytes differently; 32-bit layers are not read in this version.
        }
    }

    // MARK: Time

    /// <summary>The time limit as a <see cref="Stopwatch"/> timestamp. (Swift compares <c>Date()</c>s; a monotonic clock
    /// here, so a change of the computer's clock can't end a read early or late.)</summary>
    private static long Deadline(double seconds)
    {
        long now = Stopwatch.GetTimestamp();
        double ticks = seconds * Stopwatch.Frequency;
        if (double.IsNaN(ticks) || ticks < -4e18) return long.MinValue;
        if (ticks > 4e18) return long.MaxValue;
        long t = (long)ticks;
        return t > 0 && now > long.MaxValue - t ? long.MaxValue : now + t;
    }

    private static bool InTime(long deadline) => Stopwatch.GetTimestamp() < deadline;
}

/// <summary>Content comparison for the byte parts of the PSD records (new: Swift's <c>Data</c> compares by content
/// itself; <see cref="ReadOnlyMemory{T}"/> and the immutable collections compare by reference).</summary>
internal static class ByteContent
{
    public static bool Equal(ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b) => a.Span.SequenceEqual(b.Span);

    public static bool Equal(ReadOnlyMemory<byte>? a, ReadOnlyMemory<byte>? b) =>
        a is null ? b is null : b is not null && Equal(a.Value, b.Value);

    public static bool Equal(ImmutableArray<ReadOnlyMemory<byte>> a, ImmutableArray<ReadOnlyMemory<byte>> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!Equal(a[i], b[i])) return false;
        return true;
    }

    public static bool Equal<TKey>(IReadOnlyDictionary<TKey, ReadOnlyMemory<byte>> a, IReadOnlyDictionary<TKey, ReadOnlyMemory<byte>> b) where TKey : notnull
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
            if (!b.TryGetValue(key, out var other) || !Equal(value, other)) return false;
        return true;
    }
}
#endif
