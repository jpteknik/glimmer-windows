// Ported from Packages/GlimmerKit/Tests/Support/PSDTestSupport/PSDTestWriter.swift
#if GLIMMER_PSD
using System.IO.Compression;
using System.Text;
using Glimmer.Formats;

namespace Glimmer.Formats.Tests;

/// <summary>
/// Writes Photoshop files for the tests, from the same specification as the reader but as
/// separate code: valid ones to read back, and the pieces hostile ones are made of.
/// </summary>
/// <remarks>A Swift struct of <c>var</c>s; here a class, made fresh by every call to <see cref="Sample"/> and
/// <see cref="Mixed"/>, so a test changes only its own copy.</remarks>
public sealed class PSDTestWriter
{
    public sealed class Layer
    {
        public Layer(string name = "Layer", PSDFile.Rect? rect = null, string blendKey = "norm", byte opacity = 255, bool clipped = false, bool visible = true,
                     uint? group = null, IEnumerable<(int Id, ushort[] Samples)>? planes = null, ushort compression = 0)
        {
            Name = name; Rect = rect ?? new PSDFile.Rect(0, 0, 2, 2); BlendKey = blendKey; Opacity = opacity; Clipped = clipped;
            Visible = visible; Group = group; Planes = planes is null ? [] : [.. planes]; Compression = compression;
        }

        public string Name;
        public PSDFile.Rect Rect;
        public string BlendKey;
        public byte Opacity;
        public bool Clipped;
        public bool Visible;
        /// <summary>1 open, 2 closed, 3 end.</summary>
        public uint? Group;
        public List<(int Id, ushort[] Samples)> Planes;
        /// <summary>The painted mask, one byte a pixel; written at the document's depth, as Photoshop stores it.</summary>
        public (PSDFile.Rect Rect, byte Color, byte[] Pixels)? Mask;
        /// <summary>New in the port: the longer mask record's second rectangle (the painted mask once more, with its own
        /// background), its plane written as channel -3.</summary>
        public (PSDFile.Rect Rect, byte Color, byte[] Pixels)? RealMask;
        /// <summary>New in the port: mask parameters after the mask record (its flags then say they are there): which
        /// ones (bit 0, the painted mask's density), then their values.</summary>
        public byte[]? MaskParameters;
        public ushort Compression;
        public List<(string Key, byte[] Body)> ExtraBlocks = [];
    }

    public bool Large;
    public int Depth = 8;
    public ushort Mode = 3;
    public int Width = 2, Height = 2;
    public int Channels = 3;
    public List<Layer> Layers = [];
    public List<ushort[]> Composite = [];
    public ushort CompositeCompression;
    public byte[]? IccProfile;
    /// <summary>New in the port: the colour mode data (an indexed file's palette); none when null.</summary>
    public byte[]? ColorModeData;
    /// <summary>New in the port: the flattened picture's planes as bytes, written uncompressed in place of
    /// <see cref="Composite"/> (for 32-bit samples and packed 1-bit rows).</summary>
    public List<byte[]>? CompositeBytes;

    public static byte[] Be16(ushort v) => [(byte)(v >> 8), (byte)(v & 0xFF)];
    public static byte[] Be32(uint v) => [.. Be16((ushort)(v >> 16)), .. Be16((ushort)(v & 0xFFFF))];
    public byte[] Length(long n) => Large ? [.. Be32((uint)(n >> 32)), .. Be32((uint)(n & 0xFFFF_FFFF))] : Be32((uint)n);

    public byte[] Samples(IEnumerable<ushort> values, int bytes)
    {
        var d = new List<byte>();
        foreach (var v in values) { if (bytes == 2) d.AddRange(Be16(v)); else d.Add(unchecked((byte)v)); }
        return [.. d];
    }

    public static byte[] PackBits(ReadOnlySpan<byte> row)
    {
        // Literal runs only, at most 128 bytes each: valid PackBits, if not the smallest.
        var output = new List<byte>();
        int i = 0;
        while (i < row.Length)
        {
            int n = Math.Min(128, row.Length - i);
            output.Add((byte)(n - 1)); output.AddRange(row.Slice(i, n).ToArray()); i += n;
        }
        return [.. output];
    }

    /// <summary>A zlib header, a raw deflate stream and a checksum nobody reads, as the Swift helper writes them
    /// (Apple's COMPRESSION_ZLIB gives the raw stream; DeflateStream does here).</summary>
    public static byte[] Zlib(ReadOnlySpan<byte> data)
    {
        using var stream = new MemoryStream();
        using (var deflate = new DeflateStream(stream, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(data);
        return [0x78, 0x9C, .. stream.ToArray(), 0, 0, 0, 0];
    }

    public byte[] Plane(ushort[] values, long width, long height, int bytes, ushort compression)
    {
        var raw = Samples(values, bytes);
        switch (compression)
        {
            case 1:
            {
                long rowBytes = width * bytes;
                var counts = new List<byte>();
                var rows = new List<byte>();
                for (long y = 0; y < height; y++)
                {
                    var packed = PackBits(raw.AsSpan((int)(y * rowBytes), (int)rowBytes));
                    counts.AddRange(Large ? Be32((uint)packed.Length) : Be16((ushort)packed.Length));
                    rows.AddRange(packed);
                }
                return [.. Be16(1), .. counts, .. rows];
            }
            case 2:
                return [.. Be16(2), .. Zlib(raw)];
            case 3:
            {
                // Differences from the sample to the left, row by row.
                long rowBytes = width * bytes;
                for (long y = 0; y < height; y++)
                {
                    for (long x = width - 1; x > 0; x--)
                    {
                        int a = (int)(y * rowBytes + x * bytes);
                        if (bytes == 2)
                        {
                            ushort cur = (ushort)(raw[a] << 8 | raw[a + 1]), prev = (ushort)(raw[a - 2] << 8 | raw[a - 1]);
                            ushort d = unchecked((ushort)(cur - prev));
                            raw[a] = (byte)(d >> 8); raw[a + 1] = (byte)(d & 0xFF);
                        }
                        else
                        {
                            raw[a] = unchecked((byte)(raw[a] - raw[a - 1]));
                        }
                    }
                }
                return [.. Be16(3), .. Zlib(raw)];
            }
            default:
                return [.. Be16(0), .. raw];
        }
    }

    public byte[] Record(Layer layer, List<byte[]> planes, byte[]? maskPlane, byte[]? realMaskPlane = null)
    {
        var d = new List<byte>();
        foreach (var v in new[] { layer.Rect.Top, layer.Rect.Left, layer.Rect.Bottom, layer.Rect.Right }) d.AddRange(Be32(unchecked((uint)v)));
        d.AddRange(Be16((ushort)(planes.Count + (maskPlane is null ? 0 : 1) + (realMaskPlane is null ? 0 : 1))));
        for (int i = 0; i < planes.Count; i++) { d.AddRange(Be16(unchecked((ushort)(short)layer.Planes[i].Id))); d.AddRange(Length(planes[i].Length)); }
        if (maskPlane is not null) { d.AddRange(Be16(unchecked((ushort)(short)-2))); d.AddRange(Length(maskPlane.Length)); }
        if (realMaskPlane is not null) { d.AddRange(Be16(unchecked((ushort)(short)-3))); d.AddRange(Length(realMaskPlane.Length)); }
        d.AddRange("8BIM"u8.ToArray()); d.AddRange(Encoding.UTF8.GetBytes(layer.BlendKey));
        d.Add(layer.Opacity); d.Add(layer.Clipped ? (byte)1 : (byte)0); d.Add(layer.Visible ? (byte)0 : (byte)2); d.Add(0);
        var extra = new List<byte>();
        if (layer.Mask is { } mask)
        {
            var m = new List<byte>();
            foreach (var v in new[] { mask.Rect.Top, mask.Rect.Left, mask.Rect.Bottom, mask.Rect.Right }) m.AddRange(Be32(unchecked((uint)v)));
            m.Add(mask.Color); m.Add(layer.MaskParameters is null ? (byte)0 : (byte)16);
            // The order psd-tools reads and files from Photoshop hold: the second rectangle, then the parameters.
            if (layer.RealMask is { } real)
            {
                m.Add(0); m.Add(real.Color);
                foreach (var v in new[] { real.Rect.Top, real.Rect.Left, real.Rect.Bottom, real.Rect.Right }) m.AddRange(Be32(unchecked((uint)v)));
            }
            if (layer.MaskParameters is { } parameters) m.AddRange(parameters);
            if (layer.RealMask is null && layer.MaskParameters is null) m.AddRange([0, 0]);   // the short form: 20 bytes
            extra.AddRange(Be32((uint)m.Count)); extra.AddRange(m);
        }
        else
        {
            extra.AddRange(Be32(0));
        }
        extra.AddRange(Be32(0));                                  // blending ranges
        var utf8 = Encoding.UTF8.GetBytes(layer.Name);
        var name = new List<byte> { (byte)Math.Min(utf8.Length, 255) };
        name.AddRange(utf8.Take(255));
        while (name.Count % 4 != 0) name.Add(0);
        extra.AddRange(name);
        var blocks = new List<(string Key, byte[] Body)>(layer.ExtraBlocks);
        var luni = new List<byte>(Be32((uint)layer.Name.Length));
        foreach (char unit in layer.Name) luni.AddRange(Be16(unit));
        blocks.Add(("luni", [.. luni]));
        if (layer.Group is { } group) blocks.Add(("lsct", Be32(group)));
        foreach (var (key, block) in blocks)
        {
            var body = new List<byte>(block);
            if (body.Count % 2 == 1) body.Add(0);
            extra.AddRange("8BIM"u8.ToArray()); extra.AddRange(Encoding.UTF8.GetBytes(key)); extra.AddRange(Be32((uint)body.Count)); extra.AddRange(body);
        }
        return [.. d, .. Be32((uint)extra.Count), .. extra];
    }

    public byte[] Header()
    {
        var d = new List<byte>();
        d.AddRange("8BPS"u8.ToArray()); d.AddRange(Be16(Large ? (ushort)2 : (ushort)1)); d.AddRange(new byte[6]);
        d.AddRange(Be16((ushort)Channels)); d.AddRange(Be32((uint)Height)); d.AddRange(Be32((uint)Width)); d.AddRange(Be16((ushort)Depth)); d.AddRange(Be16(Mode));
        return [.. d];
    }

    public byte[] LayerInfo()
    {
        int bytes = Math.Max(1, Depth / 8);
        var records = new List<byte>();
        var pixels = new List<byte>();
        foreach (var layer in Layers)
        {
            var planes = layer.Planes.Select(p => Plane(p.Samples, layer.Rect.Width, layer.Rect.Height, bytes, layer.Compression)).ToList();
            // Masks at the document's depth, like every other channel (a 16-bit level is the byte × 257). The Mac's
            // helper writes them in 8 bits, as its reader reads them: docs/UPSTREAM-NOTES.md, "PSD reader", item 1.
            byte[]? MaskPlane((PSDFile.Rect Rect, byte Color, byte[] Pixels)? mask) => mask is { } m
                ? Plane([.. m.Pixels.Select(b => (ushort)(bytes == 2 ? b * 257 : b))], m.Rect.Width, m.Rect.Height, bytes, layer.Compression)
                : null;
            byte[]? maskPlane = MaskPlane(layer.Mask), realMaskPlane = MaskPlane(layer.RealMask);
            records.AddRange(Record(layer, planes, maskPlane, realMaskPlane));
            foreach (var p in planes) pixels.AddRange(p);
            if (maskPlane is not null) pixels.AddRange(maskPlane);
            if (realMaskPlane is not null) pixels.AddRange(realMaskPlane);
        }
        var info = new List<byte>(Be16((ushort)Layers.Count));
        info.AddRange(records); info.AddRange(pixels);
        if (info.Count % 2 == 1) info.Add(0);
        return [.. info];
    }

    public byte[] Data()
    {
        var d = new List<byte>(Header());
        d.AddRange(Be32((uint)(ColorModeData?.Length ?? 0)));     // colour mode data
        if (ColorModeData is { } modeData) d.AddRange(modeData);
        var resources = new List<byte>();
        if (IccProfile is { } icc)
        {
            resources.AddRange("8BIM"u8.ToArray()); resources.AddRange(Be16(1039)); resources.AddRange([0, 0]); resources.AddRange(Be32((uint)icc.Length)); resources.AddRange(icc);
            if (icc.Length % 2 == 1) resources.Add(0);
        }
        d.AddRange(Be32((uint)resources.Count)); d.AddRange(resources);
        var info = LayerInfo();
        var section = new List<byte>();
        if (Depth == 16)
        {
            // 16-bit documents: no layer info here; the layers sit in an 'Lr16' block after the global mask.
            section.AddRange(Length(0)); section.AddRange(Be32(0));
            var block = new List<byte>("8BIM"u8.ToArray());
            block.AddRange("Lr16"u8.ToArray()); block.AddRange(Length(info.Length)); block.AddRange(info);
            while (block.Count % 4 != 0) block.Add(0);
            section.AddRange(block);
        }
        else
        {
            section.AddRange(Length(info.Length)); section.AddRange(info); section.AddRange(Be32(0));
        }
        d.AddRange(Length(section.Count)); d.AddRange(section);
        if (CompositeBytes is { } planes)
        {
            d.AddRange(Be16(0));
            foreach (var plane in planes) d.AddRange(plane);
            return [.. d];
        }
        d.AddRange(Be16(CompositeCompression));
        int bytes = Math.Max(1, Depth / 8);
        if (CompositeCompression == 1)
        {
            var counts = new List<byte>();
            var rows = new List<byte>();
            foreach (var channel in Composite)
            {
                var raw = Samples(channel, bytes);
                int rowBytes = Width * bytes;
                for (int y = 0; y < Height; y++)
                {
                    var packed = PackBits(raw.AsSpan(y * rowBytes, rowBytes));
                    counts.AddRange(Large ? Be32((uint)packed.Length) : Be16((ushort)packed.Length)); rows.AddRange(packed);
                }
            }
            d.AddRange(counts); d.AddRange(rows);
        }
        else
        {
            foreach (var channel in Composite) d.AddRange(Samples(channel, bytes));
        }
        return [.. d];
    }

    /// <summary>A small document with a little of everything: two pixel layers in a group, a mask, a hidden layer.</summary>
    public static PSDTestWriter Sample(int depth = 8, bool large = false, ushort compression = 0)
    {
        var w = new PSDTestWriter { Large = large, Depth = depth, Width = 4, Height = 3 };
        var full = new PSDFile.Rect(0, 0, 3, 4);
        ushort[] Ramp(ushort start) => [.. Enumerable.Range(0, 12).Select(i => unchecked((ushort)(start + i * (depth == 16 ? 2570 : 10))))];
        var back = new Layer(name: "Background copy", rect: full, planes: [(0, Ramp(10)), (1, Ramp(20)), (2, Ramp(30))], compression: compression);
        var end = new Layer(name: "</Layer group>", rect: new PSDFile.Rect(0, 0, 0, 0), group: 3);
        var inner = new Layer(name: "Blå himmel", rect: new PSDFile.Rect(1, 1, 3, 3), blendKey: "mul ", opacity: 128,
                              planes: [(-1, [255, 255, 0, 255]), (0, [1, 2, 3, 4]), (1, [5, 6, 7, 8]), (2, [9, 10, 11, 12])], compression: compression)
        {
            Mask = (new PSDFile.Rect(1, 1, 2, 3), 255, [0, 200]),
        };
        var hidden = new Layer(name: "Hidden", rect: full, visible: false, planes: [(0, Ramp(1)), (1, Ramp(2)), (2, Ramp(3))], compression: compression)
        {
            Clipped = true,
            ExtraBlocks = [("TySh", [1, 2, 3])],
        };
        var folder = new Layer(name: "Group 1", rect: new PSDFile.Rect(0, 0, 0, 0), blendKey: "pass", group: 1);
        w.Layers = [back, end, inner, hidden, folder];
        w.Composite = [Ramp(100), Ramp(110), Ramp(120)];
        return w;
    }

    /// <summary>A 64 × 48 document with things that come through as more than pixels (a Levels layer, a
    /// shape) and things an import can't keep (a text layer, a Vibrance layer, a layer in
    /// Lighter Color), inside a group, over a plain background.</summary>
    public static PSDTestWriter Mixed()
    {
        var w = new PSDTestWriter { Width = 64, Height = 48 };
        var full = new PSDFile.Rect(0, 0, 48, 64);
        ushort[] PlaneOf(Func<int, int, int> value) => [.. Enumerable.Range(0, 64 * 48).Select(i => (ushort)value(i % 64, i / 64))];
        var back = new Layer(name: "Background", rect: full, planes: [(0, PlaneOf((x, _) => 60 + x * 3)), (1, PlaneOf((_, y) => 60 + y * 4)), (2, PlaneOf((_, _) => 150))], compression: 1);
        var end = new Layer(name: "</Layer group>", rect: new PSDFile.Rect(0, 0, 0, 0), group: 3);
        var box = new PSDFile.Rect(12, 16, 36, 48);
        List<(int, ushort[])> Solid(int r, int g, int b)
        {
            int n = (int)(box.Width * box.Height);
            return [(-1, Enumerable.Repeat((ushort)255, n).ToArray()), (0, Enumerable.Repeat((ushort)r, n).ToArray()),
                    (1, Enumerable.Repeat((ushort)g, n).ToArray()), (2, Enumerable.Repeat((ushort)b, n).ToArray())];
        }
        var title = new Layer(name: "Title", rect: box, planes: Solid(250, 240, 200), compression: 1) { ExtraBlocks = [("TySh", TypeTool("Summer Sale"))] };
        var levels = new Layer(name: "Levels 1", rect: new PSDFile.Rect(0, 0, 0, 0)) { ExtraBlocks = [("levl", Levels([(10, 240, 0, 255, 120)]))] };
        var vibrance = new Layer(name: "Vibrance 1", rect: new PSDFile.Rect(0, 0, 0, 0)) { ExtraBlocks = [("vibA", [0, 0, 0, 16])] };
        var badge = new Layer(name: "Badge", rect: new PSDFile.Rect(0, 0, 0, 0))
        {
            ExtraBlocks = [("SoCo", SolidColor(red: 220, green: 40, blue: 60)), ("vmsk", VectorMask([(0.7, 0.1), (0.95, 0.1), (0.95, 0.4), (0.7, 0.4)]))],
        };
        var glow = new Layer(name: "Glow", rect: box, blendKey: "diss", opacity: 200, planes: Solid(255, 200, 120), compression: 2);
        var folder = new Layer(name: "Heading", rect: new PSDFile.Rect(0, 0, 0, 0), blendKey: "pass", group: 1);
        w.Layers = [back, end, title, levels, vibrance, badge, glow, folder];
        w.Composite = [PlaneOf((x, _) => 60 + x * 3), PlaneOf((_, y) => 60 + y * 4), PlaneOf((_, _) => 150)];
        return w;
    }

    // MARK: Settings blocks (descriptors, paths, adjustments), as the specification lays them out

    public static byte[] Be64(ulong v) => [.. Be32((uint)(v >> 32)), .. Be32((uint)(v & 0xFFFF_FFFF))];
    public static byte[] Double(double v) => Be64(BitConverter.DoubleToUInt64Bits(v));

    public static byte[] Unicode(string text)
    {
        var d = new List<byte>(Be32((uint)text.Length));
        foreach (char unit in text) d.AddRange(Be16(unit));
        return [.. d];
    }

    /// <summary>"4 bytes (length), followed either by string or (if length is zero) 4-byte ID".</summary>
    public static byte[] Id(string key)
    {
        var utf8 = Encoding.UTF8.GetBytes(key);
        return utf8.Length == 4 ? [.. Be32(0), .. utf8] : [.. Be32((uint)utf8.Length), .. utf8];
    }

    public static byte[] Descriptor(string classID, IEnumerable<(string Key, string Type, byte[] Value)> items)
    {
        var list = items.ToList();
        var d = new List<byte>();
        d.AddRange(Unicode("")); d.AddRange(Id(classID)); d.AddRange(Be32((uint)list.Count));
        foreach (var item in list) { d.AddRange(Id(item.Key)); d.AddRange(Encoding.UTF8.GetBytes(item.Type)); d.AddRange(item.Value); }
        return [.. d];
    }

    /// <summary>A solid colour fill's block ('SoCo'): version 16 and a descriptor holding the colour.</summary>
    public static byte[] SolidColor(double red, double green, double blue)
    {
        var color = Descriptor("RGBC", [("Rd  ", "doub", Double(red)), ("Grn ", "doub", Double(green)), ("Bl  ", "doub", Double(blue))]);
        return [.. Be32(16), .. Descriptor("null", [("Clr ", "Objc", color)])];
    }

    /// <summary>A vector mask's block ('vmsk'): version 3, flags, then 26-byte path records for one closed
    /// path with corner points, given as shares of the image (x, y from 0 to 1).</summary>
    public static byte[] VectorMask(IEnumerable<(double X, double Y)> corners, uint flags = 0)
    {
        byte[] Fixed(double share) => Be32(unchecked((uint)(int)Math.Round(share * 16_777_216, MidpointRounding.AwayFromZero)));
        var list = corners.ToList();
        var d = new List<byte>(Be32(3));
        d.AddRange(Be32(flags));
        d.AddRange(Be16(6)); d.AddRange(new byte[24]);                                    // the fill rule record
        d.AddRange(Be16(0)); d.AddRange(Be16((ushort)list.Count)); d.AddRange(new byte[22]);   // a closed subpath of that many knots
        foreach (var c in list)
        {
            byte[] point = [.. Fixed(c.Y), .. Fixed(c.X)];                                 // vertical first
            d.AddRange(Be16(1)); d.AddRange(point); d.AddRange(point); d.AddRange(point);  // control before, anchor, control after
        }
        return [.. d];
    }

    /// <summary>A Levels block ('levl'): version 2 and 29 sets of five shorts; the first ones are given.</summary>
    public static byte[] Levels(IReadOnlyList<(ushort InFloor, ushort InCeiling, ushort OutFloor, ushort OutCeiling, ushort Gamma)> sets)
    {
        var d = new List<byte>(Be16(2));
        for (int i = 0; i < 29; i++)
        {
            var set = i < sets.Count ? sets[i] : ((ushort)0, (ushort)255, (ushort)0, (ushort)255, (ushort)100);
            d.AddRange(Be16(set.Item1)); d.AddRange(Be16(set.Item2)); d.AddRange(Be16(set.Item3)); d.AddRange(Be16(set.Item4)); d.AddRange(Be16(set.Item5));
        }
        return [.. d];
    }

    /// <summary>A Curves block ('curv'): each curve is its points as (input, output), 0 to 255.</summary>
    public static byte[] Curves(IReadOnlyList<IReadOnlyList<(ushort Input, ushort Output)>> curves)
    {
        // The layout files from Photoshop hold: one byte, version 1, a four-byte bit map of the curves present.
        var d = new List<byte> { 0 };
        d.AddRange(Be16(1)); d.AddRange(Be32((uint)((1 << curves.Count) - 1)));
        foreach (var curve in curves)
        {
            d.AddRange(Be16((ushort)curve.Count));
            foreach (var point in curve) { d.AddRange(Be16(point.Output)); d.AddRange(Be16(point.Input)); }   // output first
        }
        return [.. d];
    }

    /// <summary>A type layer's block ('TySh') holding these words.</summary>
    public static byte[] TypeTool(string words)
    {
        var d = new List<byte>(Be16(1));
        foreach (var v in new[] { 1.0, 0, 0, 1, 0, 0 }) d.AddRange(Double(v));
        d.AddRange(Be16(50)); d.AddRange(Be32(16)); d.AddRange(Descriptor("TxLr", [("Txt ", "TEXT", Unicode(words))]));
        d.AddRange(Be16(1)); d.AddRange(Be32(16)); d.AddRange(Descriptor("warp", []));
        foreach (var v in new[] { 0.0, 0, 0, 0 }) d.AddRange(Double(v));
        return [.. d];
    }
}
#endif
