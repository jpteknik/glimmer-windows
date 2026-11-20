// Ported from Packages/GlimmerKit/Sources/GlimmerCore/PSD/PSDWriter.swift
#if GLIMMER_PSD   // Directory.Build.props switches this (Config/Features.xcconfig on the Mac)
using System.Text;

namespace Glimmer.Formats;

/// <summary>
/// Writes a Photoshop file from its parts, by the same specification the reader follows and
/// as separate code. It refuses to write what its own reader would refuse to read: sizes,
/// counts and plane lengths are checked before a byte is produced.
/// </summary>
/// <remarks>
/// Port notes. Swift builds the file in one <c>Data</c>. Here each part that has its length in front of it (the
/// compressed planes, the flattened picture's rows) is made first, as there, and the file is then written to a
/// <see cref="Stream"/> in one pass, so a file larger than an array can hold (2 GB) can still be written. The 32-bit
/// lengths a PSD has are checked: Swift's <c>UInt32(_:)</c> would stop the program on a larger one.
/// </remarks>
public static class PSDWriter
{
    /// <summary>The file as bytes (Swift's <c>data(for:)</c>).</summary>
    public static byte[] Data(PSDFile file)
    {
        using var stream = new MemoryStream();
        Write(file, stream);
        return stream.ToArray();
    }

    /// <summary>Writes the file to <paramref name="destination"/> (new: for files too large to hold as one array).
    /// Nothing is written when the file is refused.</summary>
    public static void Write(PSDFile file, Stream destination)
    {
        var header = file.Header;
        if (header.IsLarge) throw new PSDFile.Problem("Glimmer doesn't write the large document format yet.");
        if (header.Width is < 1 or > 30_000 || header.Height is < 1 or > 30_000)
            throw new PSDFile.Problem(FormattableString.Invariant($"A Photoshop file can be at most 30,000 pixels on a side. This document is {header.Width} × {header.Height}."));
        if (header.Depth is not (8 or 16) || header.Mode is not (PSDFile.ColorMode.Rgb or PSDFile.ColorMode.Grayscale))
            throw new PSDFile.Problem("Glimmer writes Photoshop files in RGB or grayscale with 8 or 16 bits per channel.");
        int bytes = header.Depth / 8;
        int colorChannels = header.Mode == PSDFile.ColorMode.Rgb ? 3 : 1;
        long planeBytes = (long)header.Width * header.Height * bytes;
        if (file.Composite.Length != colorChannels || file.Composite.Any(plane => plane.Length != planeBytes))
            throw new PSDFile.Problem("The flattened picture doesn't fit the document.");
        if (file.Layers.Length > 8_000) throw new PSDFile.Problem("A Photoshop file from Glimmer can hold at most 8,000 layers.");
        foreach (var layer in file.Layers) Check(layer, bytes);

        var resources = new ByteWriter();
        if (file.IccProfile is { Length: > 0 } icc)
        {
            resources.Ascii("8BIM"); resources.BE16(1039); resources.Zeros(2); resources.BE32((uint)icc.Length); resources.Write(icc.Span);
            if (icc.Length % 2 == 1) resources.U8(0);
        }

        // The layer information: the count, every record, then every record's planes, padded to an even length.
        // (The planes are compressed in parallel, each on its own, so the bytes are the same however many run at once.)
        var jobs = new List<(ReadOnlyMemory<byte> Raw, long Width, long Height, int Bytes)>();
        var layout = new List<(int[] Ids, int First, bool HasMask)>();
        foreach (var layer in file.Layers)
        {
            var ids = layer.Planes.Keys.Order().ToArray();
            layout.Add((ids, jobs.Count, layer.Mask?.Pixels is not null));
            foreach (int id in ids) jobs.Add((layer.Planes[id], layer.Rect.Width, layer.Rect.Height, bytes));
            if (layer.Mask is { Pixels: { } maskPixels } mask) jobs.Add((MaskPlane(maskPixels, bytes), mask.Rect.Width, mask.Rect.Height, bytes));
        }
        var packedPlanes = new ByteWriter?[jobs.Count];
        Parallel.For(0, jobs.Count, j => packedPlanes[j] = Plane(jobs[j].Raw.Span, jobs[j].Width, jobs[j].Height, jobs[j].Bytes));
        var records = new ByteWriter();
        var pixels = new List<ByteWriter>();
        long infoLength = 0;
        for (int i = 0; i < file.Layers.Length; i++)
        {
            var layer = file.Layers[i];
            var (ids, first, hasMask) = layout[i];
            int count = ids.Length + (hasMask ? 1 : 0);
            for (int j = first; j < first + count; j++)
            {
                // Only a mask row of more than about 65,000 bytes (as many pixels at 8 bits, half as many at 16) can
                // pack to more than its two-byte count says; Swift's UInt16(_:) would stop the program there.
                if (packedPlanes[j] is null) throw new PSDFile.Problem($"The mask of the layer “{layer.Name}” doesn't fit a Photoshop file.");
            }
            var planes = packedPlanes.AsSpan(first, ids.Length).ToArray().Select(p => p!).ToList();
            ByteWriter? maskPlane = hasMask ? packedPlanes[first + ids.Length] : null;
            Record(records, layer, ids, planes.Select(p => (long)p.Length).ToArray(), maskPlane?.Length);
            pixels.AddRange(planes);
            if (maskPlane is not null) pixels.Add(maskPlane);
        }
        if (!file.Layers.IsEmpty) infoLength = 2 + records.Length + pixels.Sum(p => (long)p.Length);
        bool infoPadded = infoLength % 2 == 1;
        if (infoPadded) infoLength += 1;
        // 16-bit documents keep their layers in an 'Lr16' block after an empty layer info and global mask.
        bool inBlock = header.Depth == 16 && infoLength > 0;
        long blockLength = 12 + infoLength, blockPad = (4 - blockLength % 4) % 4;
        long sectionLength = inBlock ? 8 + blockLength + blockPad : 4 + infoLength + 4;
        if (infoLength > uint.MaxValue || sectionLength > uint.MaxValue)
            throw new PSDFile.Problem("The layers of this document take more than 4 GB, more than a Photoshop file from Glimmer can hold.");

        // The flattened picture, run-length compressed: the counts of every row of every channel, then the rows.
        int rowBytes = header.Width * bytes;
        // Each channel on its own, in parallel; its rows' counts are kept apart and put first in channel order.
        // (At most 60,000 bytes a row, so at most 60,469 packed: a count always fits its two bytes.)
        var channels = new (ByteWriter Counts, ByteWriter Rows)[file.Composite.Length];
        Parallel.For(0, channels.Length, c =>
        {
            var channelCounts = new ByteWriter(2 * header.Height);
            var packed = new ByteWriter(rowBytes * header.Height / 2 + 64);
            var plane = file.Composite[c].Span;
            for (int y = 0; y < header.Height; y++) channelCounts.BE16((ushort)PackBits(plane.Slice(y * rowBytes, rowBytes), packed));
            channels[c] = (channelCounts, packed);
        });
        var counts = new ByteWriter(2 * header.Height * channels.Length);
        foreach (var channel in channels) counts.Write(channel.Counts.Written);
        var rows = channels.Select(c => c.Rows).ToList();

        var head = new ByteWriter(64);
        head.Ascii("8BPS"); head.BE16(1); head.Zeros(6);
        head.BE16((ushort)colorChannels); head.BE32((uint)header.Height); head.BE32((uint)header.Width); head.BE16((ushort)header.Depth); head.BE16((ushort)header.Mode);
        head.BE32(0);                                             // colour mode data
        head.BE32((uint)resources.Length);
        head.CopyTo(destination);
        resources.CopyTo(destination);

        var section = new ByteWriter(32);
        section.BE32((uint)sectionLength);
        if (inBlock)
        {
            section.BE32(0); section.BE32(0);
            section.Ascii("8BIM"); section.Ascii("Lr16"); section.BE32((uint)infoLength);
        }
        else
        {
            section.BE32((uint)infoLength);
        }
        if (infoLength > 0) section.BE16((ushort)file.Layers.Length);
        section.CopyTo(destination);
        records.CopyTo(destination);
        foreach (var plane in pixels) plane.CopyTo(destination);
        var tail = new ByteWriter(8);
        if (infoPadded) tail.U8(0);
        if (inBlock) tail.Zeros((int)blockPad); else tail.BE32(0);   // the block's padding, or the global mask's empty length
        tail.BE16(1);                                             // the flattened picture: run-length compressed
        tail.CopyTo(destination);
        counts.CopyTo(destination);
        foreach (var packed in rows) packed.CopyTo(destination);
    }

    /// <summary>
    /// A mask's samples at the document's depth, as the file stores every channel: in a 16-bit document each of the
    /// mask's bytes becomes the 16-bit value that stands for the same level (v × 257). The Mac writes masks in 8 bits
    /// at every depth, the writer's half of docs/UPSTREAM-NOTES.md, "PSD reader", item 1; the reader here reads them at
    /// the document's depth and brings them back to the same bytes.
    /// </summary>
    internal static ReadOnlyMemory<byte> MaskPlane(ReadOnlyMemory<byte> pixels, int bytes)
    {
        if (bytes == 1) return pixels;
        var span = pixels.Span;
        var wide = new byte[span.Length * 2];
        for (int i = 0; i < span.Length; i++)
        {
            wide[2 * i] = span[i];        // big-endian: v × 257 has v in both bytes
            wide[2 * i + 1] = span[i];
        }
        return wide;
    }

    private static void Check(PSDFile.Layer layer, int bytes)
    {
        var r = layer.Rect;
        if (r.Bottom < r.Top || r.Right < r.Left || !Within(r))
            throw new PSDFile.Problem($"The layer “{layer.Name}” has a rectangle a Photoshop file can't hold.");
        // (The plane ids are checked as well: Swift's Int16(_:) would stop the program on one outside -32768…32767.)
        if (layer.Planes.Count > 56 || layer.Planes.Values.Any(p => p.Length != r.Width * r.Height * bytes) || Encoding.UTF8.GetByteCount(layer.BlendKey) != 4
            || layer.Planes.Keys.Any(id => id is < short.MinValue or > short.MaxValue))
            throw new PSDFile.Problem($"The layer “{layer.Name}” doesn't fit a Photoshop file.");
        if (layer.Mask is { Pixels: { } pixels } mask)
        {
            if (mask.Rect.Bottom < mask.Rect.Top || mask.Rect.Right < mask.Rect.Left || pixels.Length != mask.Rect.Width * mask.Rect.Height)
                throw new PSDFile.Problem($"The mask of the layer “{layer.Name}” doesn't fit a Photoshop file.");
        }
        // A row a kilobyte wide can't make a run-length count overflow its two bytes, but a very wide one could.
        if (!(r.Width * bytes <= 32_000 || layer.Planes.IsEmpty)) throw new PSDFile.Problem($"The layer “{layer.Name}” is too wide for a Photoshop file.");
    }

    private static bool Within(PSDFile.Rect r) =>
        Math.Abs((long)r.Top) <= 1_000_000 && Math.Abs((long)r.Left) <= 1_000_000 && Math.Abs((long)r.Bottom) <= 1_000_000 && Math.Abs((long)r.Right) <= 1_000_000;

    private static void Record(ByteWriter d, PSDFile.Layer layer, int[] ids, long[] lengths, long? maskLength)
    {
        d.BE32((uint)layer.Rect.Top); d.BE32((uint)layer.Rect.Left); d.BE32((uint)layer.Rect.Bottom); d.BE32((uint)layer.Rect.Right);
        d.BE16((ushort)(ids.Length + (maskLength is null ? 0 : 1)));
        for (int i = 0; i < ids.Length; i++) { d.BE16((ushort)(short)ids[i]); d.BE32((uint)lengths[i]); }
        if (maskLength is { } m) { d.BE16(unchecked((ushort)(short)-2)); d.BE32((uint)m); }
        d.Ascii("8BIM"); d.Ascii(layer.BlendKey);
        d.U8(layer.Opacity); d.U8(layer.IsClipped ? (byte)1 : (byte)0); d.U8(layer.IsVisible ? (byte)0 : (byte)2); d.U8(0);

        var extra = new ByteWriter();
        if (layer.Mask is { } mask)
        {
            extra.BE32(20);
            extra.BE32((uint)mask.Rect.Top); extra.BE32((uint)mask.Rect.Left); extra.BE32((uint)mask.Rect.Bottom); extra.BE32((uint)mask.Rect.Right);
            extra.U8(mask.DefaultColor); extra.U8(mask.IsDisabled ? (byte)2 : (byte)0); extra.Zeros(2);
        }
        else
        {
            extra.BE32(0);
        }
        extra.BE32(0);                                            // blending ranges
        // The name as a Pascal string, padded to a multiple of four with its length byte; the full name follows in Unicode.
        var shortName = Encoding.UTF8.GetBytes(layer.Name).Take(255).Where(b => b < 0x80).ToArray();
        extra.U8((byte)shortName.Length); extra.Write(shortName);
        int nameLength = 1 + shortName.Length;
        while (nameLength % 4 != 0) { extra.U8(0); nameLength += 1; }
        var units = layer.Name.AsSpan(0, Math.Min(layer.Name.Length, 1_000));   // UTF-16 code units, as Swift's name.utf16.prefix(1_000)
        var luni = new ByteWriter(4 + units.Length * 2);
        luni.BE32((uint)units.Length);
        foreach (char unit in units) luni.BE16(unit);
        var blocks = new List<(string Key, ReadOnlyMemory<byte> Body)> { ("luni", luni.ToArray()) };
        switch (layer.Group)
        {
            case PSDFile.GroupMark.Open: blocks.Add(("lsct", Divider(1, layer.BlendKey))); break;
            case PSDFile.GroupMark.Closed: blocks.Add(("lsct", Divider(2, layer.BlendKey))); break;
            case PSDFile.GroupMark.End: blocks.Add(("lsct", new byte[] { 0, 0, 0, 3 })); break;
            case PSDFile.GroupMark.None: break;
        }
        foreach (var key in layer.Blocks.Keys.Order(StringComparer.Ordinal))
            if (Encoding.UTF8.GetByteCount(key) == 4 && key != "luni" && key != "lsct") blocks.Add((key, layer.Blocks[key]));
        foreach (var (key, body) in blocks)
        {
            extra.Ascii("8BIM"); extra.Ascii(key); extra.BE32((uint)(body.Length + body.Length % 2)); extra.Write(body.Span);
            if (body.Length % 2 == 1) extra.U8(0);
        }
        d.BE32((uint)extra.Length);
        d.Write(extra.Written);
    }

    private static byte[] Divider(uint kind, string blendKey)
    {
        var d = new ByteWriter(12);
        d.BE32(kind); d.Ascii("8BIM"); d.Ascii(blendKey);
        return d.ToArray();
    }

    /// <summary>One plane, run-length compressed: the two-byte count of each row, then the rows. Null when a row
    /// packs to more than its two-byte count can say (new: Swift's <c>UInt16(_:)</c> would stop the program).</summary>
    private static ByteWriter? Plane(ReadOnlySpan<byte> raw, long width, long height, int bytes)
    {
        long rowBytes = width * bytes;
        var output = new ByteWriter(raw.Length / 2 + 2 * (int)Math.Max(height, 0) + 16);
        if (rowBytes <= 0 || height <= 0) { output.BE16(0); return output; }
        output.BE16(1);
        int counts = output.Reserve(2 * (int)height);
        for (int y = 0; y < height; y++)
        {
            int n = PackBits(raw.Slice((int)(y * rowBytes), (int)rowBytes), output);
            if (n > ushort.MaxValue) return null;
            output.PatchBE16(counts + 2 * y, (ushort)n);
        }
        return output;
    }

    /// <summary>PackBits: runs of three or more equal bytes as a count and the byte, the rest as literals.</summary>
    internal static byte[] PackBits(ReadOnlySpan<byte> row)
    {
        var output = new ByteWriter(row.Length + row.Length / 128 + 16);
        PackBits(row, output);
        return output.ToArray();
    }

    /// <summary>The same, appended to <paramref name="output"/>; returns how many bytes it took.</summary>
    internal static int PackBits(ReadOnlySpan<byte> bytes, ByteWriter output)
    {
        int start = output.Length;
        int i = 0;
        while (i < bytes.Length)
        {
            int run = 1;
            while (i + run < bytes.Length && run < 128 && bytes[i + run] == bytes[i]) run += 1;
            if (run >= 3)
            {
                output.U8(unchecked((byte)(sbyte)(1 - run))); output.U8(bytes[i]); i += run;
                continue;
            }
            // Literals up to the next run of three, at most 128.
            int end = i;
            while (end < bytes.Length && end - i < 128)
            {
                if (end + 2 < bytes.Length && bytes[end] == bytes[end + 1] && bytes[end] == bytes[end + 2]) break;
                end += 1;
            }
            output.U8((byte)(end - i - 1)); output.Write(bytes[i..end]); i = end;
        }
        return output.Length - start;
    }
}
#endif
