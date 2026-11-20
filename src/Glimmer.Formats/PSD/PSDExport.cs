// Ported from Packages/GlimmerKit/Sources/GlimmerRender/PSDExport.swift. The Mac embeds the sRGB profile CoreGraphics
// keeps; here it is IccProfile's sRGB, Skia's profile. Pixels are read from the flattened bitmap through LinearPixels
// where the Mac reads the CGImage's bytes, and the sRGB curve is Glimmer.Render's table (within 1.5e-6 of the formula
// the Mac computes, below a 16-bit step).
#if GLIMMER_PSD   // Directory.Build.props switches this (Config/Features.xcconfig on the Mac)
using System.Collections.Immutable;
using System.Globalization;
using Glimmer.Core;
using Glimmer.Render;
using SkiaSharp;

namespace Glimmer.Formats;

/// <summary>What a Photoshop file written from a Glimmer document holds as it was, what it holds as pixels, and what it
/// does not hold. Made by the same call that makes the file.</summary>
public sealed record PSDExportReport
{
    public enum Kind
    {
        /// <summary>Written as a layer of pixels showing what it looks like: text, smart objects, gradient and pattern
        /// fills, layers with effects.</summary>
        [RawValue("writtenAsPixels")] WrittenAsPixels,
        /// <summary>An adjustment layer of a kind the file can't carry from Glimmer: not written as a layer (the flattened
        /// picture still shows it).</summary>
        [RawValue("adjustmentLeftOut")] AdjustmentLeftOut,
        /// <summary>A mask's feather or density is not written: the mask is written plain.</summary>
        [RawValue("maskSimplified")] MaskSimplified,
        /// <summary>The layer reaches outside the canvas; what is outside is not written.</summary>
        [RawValue("clippedToCanvas")] ClippedToCanvas,
        /// <summary>The document blends in linear light; another program blends these layers in gamma.</summary>
        [RawValue("linearBlending")] LinearBlending,
        /// <summary>The document's colours are Display P3; the file is written in sRGB.</summary>
        [RawValue("writtenInSRGB")] WrittenInSRGB,
        /// <summary>By choice: everything merged into one layer.</summary>
        [RawValue("flattenedByChoice")] FlattenedByChoice,
        /// <summary>A Curves layer made in Glimmer: written with points added along the curve, so that a program that
        /// draws curves its own way follows the same line.</summary>
        [RawValue("curveDrawnDifferently")] CurveDrawnDifferently,
    }

    public sealed record Note(Kind Kind, string? Layer = null, string? Detail = null, bool ChangesLook = false)
    {
        /// <summary>The note in English, for the agent API and the tests. The app says it in the person's language.</summary>
        public string What
        {
            get
            {
                string detail = Detail ?? "";
                return Kind switch
                {
                    Kind.WrittenAsPixels => $"It is {detail} in Glimmer and is written as a layer of pixels that looks the same.",
                    Kind.AdjustmentLeftOut => $"It is a {detail} adjustment layer, which Glimmer can't write as one. It is not in the file's layers; the flattened picture in the file still shows it.",
                    Kind.MaskSimplified => "Its mask is written without its feather and density.",
                    Kind.ClippedToCanvas => "It reaches outside the canvas, and what is outside is not written.",
                    Kind.LinearBlending => "This document blends colors in linear light. Other programs blend these layers in gamma, so the layers will look different there; the flattened picture in the file is right.",
                    Kind.WrittenInSRGB => "The document's colors are Display P3; the file is written in sRGB.",
                    Kind.CurveDrawnDifferently => "It is written as a Curves layer with points added along the curve, so that it looks the same in programs that draw curves their own way. It has more points to edit there.",
                    _ => "All layers are merged into one, as chosen: the file looks right everywhere and has no layers to edit.",
                };
            }
        }
    }

    public int LayersWritten { get; init; }
    public IReadOnlyList<Note> Notes { get; init; } = [];
    /// <summary>For a document that blends in linear light: how far its layers, blended in gamma as another program will,
    /// are from the document's own picture (mean on 0…255, share of pixels off by more than 8). Null for a document that
    /// blends in gamma already.</summary>
    public (double Mean, double ShareVisible)? GammaDifference { get; init; }

    // As the Swift's ==: the counts, the notes and the mean.
    public bool Equals(PSDExportReport? other) => other is not null && LayersWritten == other.LayersWritten
        && Notes.SequenceEqual(other.Notes) && GammaDifference?.Mean == other.GammaDifference?.Mean;

    public override int GetHashCode() => HashCode.Combine(LayersWritten, Notes.Count, GammaDifference?.Mean);
}

/// <summary>
/// A Glimmer document as a Photoshop file: layers that have a Photoshop form are written as that, everything else as
/// pixels showing what it looks like, with a flattened picture that any program can show. First version: RGB, 8 or 16
/// bits per channel, sRGB.
/// </summary>
public static class PSDExport
{
    internal static readonly IReadOnlyDictionary<BlendMode, string> BlendKeys = new Dictionary<BlendMode, string>
    {
        [BlendMode.PassThrough] = "pass", [BlendMode.Normal] = "norm", [BlendMode.DarkerColor] = "dkCl", [BlendMode.LighterColor] = "lgCl",
        [BlendMode.Darken] = "dark", [BlendMode.Multiply] = "mul ", [BlendMode.ColorBurn] = "idiv", [BlendMode.LinearBurn] = "lbrn",
        [BlendMode.Lighten] = "lite", [BlendMode.Screen] = "scrn", [BlendMode.ColorDodge] = "div ", [BlendMode.LinearDodge] = "lddg",
        [BlendMode.Overlay] = "over", [BlendMode.SoftLight] = "sLit", [BlendMode.HardLight] = "hLit", [BlendMode.VividLight] = "vLit",
        [BlendMode.LinearLight] = "lLit", [BlendMode.PinLight] = "pLit", [BlendMode.HardMix] = "hMix", [BlendMode.Difference] = "diff",
        [BlendMode.Exclusion] = "smud", [BlendMode.Subtract] = "fsub", [BlendMode.Divide] = "fdiv", [BlendMode.Hue] = "hue ",
        [BlendMode.Saturation] = "sat ", [BlendMode.Color] = "colr", [BlendMode.Luminosity] = "lum ",
    };

    private static readonly PSDFile.Rect NoRect = new(0, 0, 0, 0);

    /// <summary><paramref name="flattened"/>: write one merged layer instead of the document's layers (for a document
    /// whose layers would blend differently in another program).</summary>
    public static (byte[] Data, PSDExportReport Report) Data(DocumentState state, int depth = 8, bool flattened = false)
    {
        var (file, report) = File(state, depth, flattened);
        return (PSDWriter.Data(file), report);
    }

    public static (PSDFile File, PSDExportReport Report) File(DocumentState state, int depth = 8, bool flattened = false)
    {
        if (depth is not (8 or 16)) throw new PSDFile.Problem("Glimmer writes Photoshop files with 8 or 16 bits per channel.");
        if (state.Width is < 1 or > 30_000 || state.Height is < 1 or > 30_000)
            throw new PSDFile.Problem(string.Create(CultureInfo.InvariantCulture,
                $"A Photoshop file can be at most 30,000 pixels on a side. This document is {state.Width} × {state.Height}."));
        var notes = new List<PSDExportReport.Note>();
        if (state.ColorSpace == WorkingColorSpace.DisplayP3) notes.Add(new(PSDExportReport.Kind.WrittenInSRGB));
        var header = new PSDFile.FileHeader(false, 3, state.Width, state.Height, depth, PSDFile.ColorMode.Rgb);
        var srgb = (ReadOnlyMemory<byte>)IccProfile.Rgb(WorkingColorSpace.SRGB);
        if (flattened)
        {
            // One layer holding the document's own picture, transparency kept.
            notes.Add(new(PSDExportReport.Kind.FlattenedByChoice));
            using var image = ImageExporter.Flatten(state);
            var merged = new Pixels(image, depth, overWhite: false);
            var layer = new PSDFile.Layer("Merged", NoRect, "norm", 255, false, true, PSDFile.GroupMark.None, null, [],
                                          EmptyPlanes, depth / 8);
            if (merged.Box is { } box)
            {
                layer = layer with { Rect = new PSDFile.Rect(box.Y, box.X, box.MaxY, box.MaxX), Planes = merged.Planes(box) };
            }
            var flat = new Pixels(image, depth, overWhite: true);
            return (new PSDFile(header, [layer], [flat.Red, flat.Green, flat.Blue], srgb), new PSDExportReport { LayersWritten = 1, Notes = notes });
        }
        (double Mean, double ShareVisible)? gammaDifference = null;
        if (state.BlendSpace == BlendSpace.Linear)
        {
            notes.Add(new(PSDExportReport.Kind.LinearBlending, ChangesLook: true));
            try
            {
                using var ours = ImageExporter.FlattenedSRGB(state);
                using var theirs = ImageExporter.FlattenedSRGB(state with { BlendSpace = BlendSpace.Gamma });
                if (PSDAppearance.OnWhite(ours) is { } a && PSDAppearance.OnWhite(theirs) is { } b) gammaDifference = PSDAppearance.Difference(a, b);
            }
            catch (Exception e) when (e is RenderError or OutOfMemoryException) { }
        }
        var layers = new List<PSDFile.Layer>();
        int written = 0;
        Append(state.Layers, layers, state, depth, notes, ref written, nesting: 0);
        // The flattened picture, over white where the document is transparent.
        using var picture = ImageExporter.Flatten(state);
        var white = new Pixels(picture, depth, overWhite: true);
        return (new PSDFile(header, layers, [white.Red, white.Green, white.Blue], srgb),
                new PSDExportReport { LayersWritten = written, Notes = notes, GammaDifference = gammaDifference });
    }

    private static readonly ImmutableDictionary<int, ReadOnlyMemory<byte>> EmptyPlanes = new Dictionary<int, ReadOnlyMemory<byte>>
    {
        [-1] = ReadOnlyMemory<byte>.Empty, [0] = ReadOnlyMemory<byte>.Empty, [1] = ReadOnlyMemory<byte>.Empty, [2] = ReadOnlyMemory<byte>.Empty,
    }.ToImmutableDictionary();

    /// <summary>Bottom to top, as the file stores them: a group is its end mark, its children, then its own record.</summary>
    private static void Append(IReadOnlyList<Layer> list, List<PSDFile.Layer> output, DocumentState state, int depth,
                               List<PSDExportReport.Note> notes, ref int written, int nesting)
    {
        if (nesting > 60) throw new PSDFile.Problem("This document nests its groups deeper than a Photoshop file from Glimmer can hold.");
        foreach (var layer in list)
        {
            string blendKey = BlendKeys.TryGetValue(layer.BlendMode, out var key) ? key : "norm";
            if (blendKey == "pass" && !layer.IsGroup) blendKey = "norm";
            var record = new PSDFile.Layer(layer.Name, NoRect, blendKey,
                                           (byte)Math.Round(Math.Clamp(layer.Opacity, 0, 1) * 255, MidpointRounding.AwayFromZero),
                                           layer.IsClipped, layer.IsVisible, PSDFile.GroupMark.None, Mask(layer, state, notes), [],
                                           ImmutableDictionary<int, ReadOnlyMemory<byte>>.Empty, depth / 8);
            switch (layer.Content)
            {
                case GroupContent g:
                    output.Add(new PSDFile.Layer("</Layer group>", record.Rect, "norm", 255, false, true, PSDFile.GroupMark.End, null, [],
                                                 ImmutableDictionary<int, ReadOnlyMemory<byte>>.Empty, depth / 8));
                    Append(g.Group.Children, output, state, depth, notes, ref written, nesting + 1);
                    record = record with { Group = g.Group.IsExpanded ? PSDFile.GroupMark.Open : PSDFile.GroupMark.Closed };
                    break;
                case AdjustmentContent { Adjustment: var adjustment }:
                {
                    // A curve made in Glimmer runs between its points in Glimmer's own way; Photoshop draws its own curve
                    // through the same points. So such a curve is written with points added along it, enough for the
                    // other program's curve to follow the same line.
                    if (adjustment.Kind == AdjustmentKind.Curves && !adjustment.UsesNaturalCurve && adjustment.Curves.Values.Any(c => c.Length > 2))
                        notes.Add(new(PSDExportReport.Kind.CurveDrawnDifferently, layer.Name));
                    if (Settings(adjustment) is not { } block)
                    {
                        notes.Add(new(PSDExportReport.Kind.AdjustmentLeftOut, layer.Name, adjustment.Kind.Title(), layer.IsVisible));
                        continue;
                    }
                    record = record with { Blocks = record.Blocks.SetItem(block.Key, block.Body) };
                    break;
                }
                case FillContent { Fill: Fill.Solid solid } when !layer.Effects.HasEnabled:
                    record = record with { Blocks = record.Blocks.SetItem("SoCo", SolidColor(solid.Color)) };
                    if (layer.VectorMask is { } path)
                    {
                        record = record with
                        {
                            Blocks = record.Blocks.SetItem("vmsk", VectorMask(path, state.Width, state.Height)),
                            Mask = null,   // the outline is the mask
                        };
                    }
                    break;
                default:
                {
                    // Everything else as what it looks like. A pixel layer without effects is itself.
                    string? kind = layer.Content switch
                    {
                        RasterContent => layer.Effects.HasEnabled ? "a layer with effects" : null,
                        TextContent => "text",
                        SmartContent => "a smart object",
                        FillContent { Fill: Fill.Gradient } => "a gradient fill",
                        FillContent { Fill: Fill.Pattern } => "a pattern fill",
                        _ => "a layer with effects",
                    };
                    if (kind is not null) notes.Add(new(PSDExportReport.Kind.WrittenAsPixels, layer.Name, kind));
                    var solo = layer with { IsVisible = true, Opacity = 1, BlendMode = BlendMode.Normal, Mask = null, IsClipped = false };
                    // A fill's own outline stays in its pixels; a plain layer's mask is written as a mask.
                    if (layer.Content is FillContent && layer.VectorMask is not null)
                    {
                        solo = solo with { Mask = layer.Mask };
                        record = record with { Mask = null };
                    }
                    using var image = ImageExporter.Flatten(state with { Layers = [solo] });
                    var pixels = new Pixels(image, depth, overWhite: false);
                    // Pixels are stored in tiles; a tile that lies wholly outside the canvas holds something that is not written.
                    if (layer.Content is RasterContent r && r.Tiles.TileBounds is { } bounds)
                    {
                        const int size = TileGeometry.Size;
                        var canvasTiles = new PixelRect(0, 0, (state.Width + size - 1) / size * size, (state.Height + size - 1) / size * size);
                        if (bounds.Intersection(canvasTiles) != bounds) notes.Add(new(PSDExportReport.Kind.ClippedToCanvas, layer.Name));
                    }
                    record = pixels.Box is { } box
                        ? record with { Rect = new PSDFile.Rect(box.Y, box.X, box.MaxY, box.MaxX), Planes = pixels.Planes(box) }
                        // Nothing visible: an empty layer still has its four planes, of no size.
                        : record with { Planes = EmptyPlanes };
                    break;
                }
            }
            output.Add(record);
            written += 1;
        }
    }

    // MARK: Settings blocks

    internal static byte[] Be16(ushort v) => [(byte)(v >> 8), (byte)(v & 0xFF)];
    internal static byte[] Be32(uint v) => [.. Be16((ushort)(v >> 16)), .. Be16((ushort)(v & 0xFFFF))];
    internal static byte[] Double(double v) => [.. Be32((uint)(BitConverter.DoubleToUInt64Bits(v) >> 32)), .. Be32((uint)(BitConverter.DoubleToUInt64Bits(v) & 0xFFFF_FFFF))];

    internal static byte[] Id(string key)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(key);
        return bytes.Length == 4 ? [.. Be32(0), .. bytes] : [.. Be32((uint)bytes.Length), .. bytes];
    }

    internal static byte[] Descriptor(string classId, IReadOnlyList<(string Key, string Type, byte[] Value)> items)
    {
        var d = new List<byte>();
        d.AddRange(Be32(0)); d.AddRange(Id(classId)); d.AddRange(Be32((uint)items.Count));   // an empty name, the class, the items
        foreach (var (key, type, value) in items)
        {
            d.AddRange(Id(key));
            d.AddRange(System.Text.Encoding.UTF8.GetBytes(type));
            d.AddRange(value);
        }
        return [.. d];
    }

    /// <summary>A solid colour's block: version 16 and a descriptor with the colour as red, green and blue from 0 to
    /// 255.</summary>
    internal static byte[] SolidColor(SRGBColor color)
    {
        static byte[] Channel(double v) => Double(Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero));
        var rgb = Descriptor("RGBC", [("Rd  ", "doub", Channel(color.R)), ("Grn ", "doub", Channel(color.G)), ("Bl  ", "doub", Channel(color.B))]);
        return [.. Be32(16), .. Descriptor("null", [("Clr ", "Objc", rgb)])];
    }

    /// <summary>A vector mask's block: version 3, no flags, a fill rule record, then each subpath as its length record
    /// and its knots (control before, anchor, control after; vertical first; 8.24 fixed point shares).</summary>
    internal static byte[] VectorMask(VectorPath path, int width, int height)
    {
        static byte[] Fixed(double share) =>
            Be32(unchecked((uint)(int)Math.Round(Math.Clamp(share, -15.9, 15.9) * 16_777_216, MidpointRounding.AwayFromZero)));
        byte[] Point(Vec2 p) => [.. Fixed(p.Y / height), .. Fixed(p.X / width)];
        var d = new List<byte>();
        d.AddRange(Be32(3)); d.AddRange(Be32(0));
        d.AddRange(Be16(6)); d.AddRange(new byte[24]);
        foreach (var sub in path.Subpaths.Where(s => s.Anchors.Length >= 2))
        {
            var anchors = sub.Anchors.Take(60_000).ToList();
            d.AddRange(Be16((ushort)(sub.IsClosed ? 0 : 3))); d.AddRange(Be16((ushort)anchors.Count)); d.AddRange(new byte[22]);
            foreach (var a in anchors)
            {
                d.AddRange(Be16((ushort)(sub.IsClosed ? 2 : 5)));
                d.AddRange(Point(a.HandleIn)); d.AddRange(Point(a.Point)); d.AddRange(Point(a.HandleOut));
            }
        }
        return [.. d];
    }

    /// <summary>Invert, Levels and Curves have a form in the file; the other kinds don't from Glimmer.</summary>
    internal static (string Key, byte[] Body)? Settings(Adjustment adjustment)
    {
        CurveChannel[] channels = [CurveChannel.Rgb, CurveChannel.Red, CurveChannel.Green, CurveChannel.Blue];
        static ushort Byte(double v) => (ushort)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
        switch (adjustment.Kind)
        {
            case AdjustmentKind.Invert:
                return ("nvrt", []);
            case AdjustmentKind.Levels:
            {
                var d = new List<byte>(Be16(2));
                for (int i = 0; i < 29; i++)
                {
                    var l = i < channels.Length && adjustment.Levels.TryGetValue(channels[i], out var set) ? set : new LevelsChannel();
                    // The file's ranges: floor 0…253, ceiling 2…255 above the floor, gamma 0.10…9.99.
                    ushort floor = Math.Min(Byte(l.InBlack), (ushort)253);
                    ushort ceiling = (ushort)Math.Max(Math.Min(Byte(l.InWhite), (ushort)255), floor + 2 > 255 ? 255 : Math.Max(floor + 1, 2));
                    d.AddRange(Be16(floor)); d.AddRange(Be16(ceiling)); d.AddRange(Be16(Byte(l.OutBlack))); d.AddRange(Be16(Byte(l.OutWhite)));
                    d.AddRange(Be16((ushort)Math.Round(Math.Clamp(l.Gamma, 0.1, 9.99) * 100, MidpointRounding.AwayFromZero)));
                }
                return ("levl", [.. d]);
            }
            case AdjustmentKind.Curves:
            {
                // As files from Photoshop hold it: one byte, version 1, a four-byte bit map of the curves that follow.
                var d = new List<byte> { 0 };
                d.AddRange(Be16(1)); d.AddRange(Be32(0b1111));
                foreach (var channel in channels)
                {
                    var source = (adjustment.Curves.TryGetValue(channel, out var c) ? c : []).OrderBy(p => p.X).ToList();
                    if (!adjustment.UsesNaturalCurve && source.Count > 2)
                    {
                        // Glimmer's curve sampled at its own points and at even steps between: up to 19 points in all.
                        var xs = new HashSet<ushort>(source.Select(p => Byte(p.X)));
                        for (int i = 0; i <= 16 && xs.Count < 19; i++) xs.Add((ushort)Math.Round(i * 255.0 / 16, MidpointRounding.AwayFromZero));
                        ushort lo = Byte(source[0].X), hi = Byte(source[^1].X);
                        var curve = source;
                        source = [.. xs.Where(x => x >= lo && x <= hi).Order().Select(x => new CurvePoint(x / 255.0, CurveMath.Evaluate(curve, x / 255.0)))];
                    }
                    var points = new List<(ushort Input, ushort Output)>();
                    // Inputs must rise; points that land on the same input after rounding are dropped. A curve needs 2 to 19 points.
                    foreach (var p in source.Select(p => (Byte(p.X), Byte(p.Y))))
                        if (points.Count == 0 || points[^1].Input < p.Item1) points.Add(p);
                    if (points.Count < 2) points = [(0, 0), (255, 255)];
                    if (points.Count > 19) points = points[..19];
                    d.AddRange(Be16((ushort)points.Count));
                    foreach (var (input, output) in points) { d.AddRange(Be16(output)); d.AddRange(Be16(input)); }
                }
                return ("curv", [.. d]);
            }
            default:
                return null;
        }
    }

    // MARK: Masks

    /// <summary>The layer's mask as the file stores one: a byte per pixel over the part of the canvas its tiles
    /// cover.</summary>
    private static PSDFile.Mask? Mask(Layer layer, DocumentState state, List<PSDExportReport.Note> notes)
    {
        if (layer.Mask is not { } mask) return null;
        if (mask.Feather > 0 || mask.Density < 1) notes.Add(new(PSDExportReport.Kind.MaskSimplified, layer.Name, ChangesLook: true));
        byte outside = (byte)Math.Round(Math.Clamp(mask.DefaultValue, 0, 1) * 255, MidpointRounding.AwayFromZero);
        if (mask.Tiles.TileBounds?.Intersection(state.Bounds) is not { IsEmpty: false } box)
            return new PSDFile.Mask(NoRect, outside, !mask.IsEnabled, null);
        var pixels = new byte[(long)box.Width * box.Height];
        Array.Fill(pixels, outside);
        const int s = TileGeometry.Size;
        foreach (var coord in box.TileCoords)
        {
            if (mask.Tiles[coord] is not { } tile) continue;
            var part = coord.Rect.Intersection(box);
            if (tile.IsSolid)
            {
                byte v = Level(tile.SolidColor.X);
                for (int y = part.Y; y < part.MaxY; y++) pixels.AsSpan((y - box.Y) * box.Width + (part.X - box.X), part.Width).Fill(v);
                continue;
            }
            var values = tile.Pixels.Span;
            for (int y = part.Y; y < part.MaxY; y++)
                for (int x = part.X; x < part.MaxX; x++)
                    pixels[(y - box.Y) * box.Width + (x - box.X)] = Level((float)values[((y - coord.Rect.Y) * s + (x - coord.Rect.X)) * 4]);
        }
        return new PSDFile.Mask(new PSDFile.Rect(box.Y, box.X, box.MaxY, box.MaxX), outside, !mask.IsEnabled, pixels);

        static byte Level(float v) => float.IsNaN(v) ? (byte)0 : (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
    }

    // MARK: Pixels

    /// <summary>A flattened picture from Glimmer (linear light, premultiplied) as the planes a Photoshop file holds:
    /// sRGB-encoded, straight (not premultiplied), 8 or 16 bits, big-endian.</summary>
    internal sealed class Pixels
    {
        public int Width { get; }
        public int Height { get; }
        public int Bytes { get; }
        public byte[] Red { get; }
        public byte[] Green { get; }
        public byte[] Blue { get; }
        public byte[] Alpha { get; }
        /// <summary>The box around everything that isn't fully transparent, null when nothing is.</summary>
        public PixelRect? Box { get; }
        public bool ReachesEdge { get; }

        public Pixels(SKBitmap image, int depth, bool overWhite)
        {
            using var source = LinearPixels.Of(image);
            Width = source.Width; Height = source.Height; Bytes = depth / 8;
            long count = (long)Width * Height * Bytes;
            if (count > Array.MaxLength) throw new PSDFile.Problem("Glimmer couldn't read its own picture of the document.");
            Red = new byte[count]; Green = new byte[count]; Blue = new byte[count]; Alpha = new byte[count];
            float top = depth == 16 ? 65_535f : 255f;
            int width = Width, bytes = Bytes;
            var rows = new (int MinX, int MaxX)[Height];
            var red = Red; var green = Green; var blue = Blue; var alpha = Alpha;
            Parallel.For(0, Height, () => new float[width * 4], (y, _, row) =>
            {
                System.Numerics.Tensors.TensorPrimitives.ConvertToSingle(source.Row(y), row);
                int minX = width, maxX = -1;
                void Store(byte[] plane, long i, float value)
                {
                    float v = float.IsNaN(value) ? 0 : Math.Clamp(value, 0f, 1f);
                    ushort n = (ushort)(v * top + 0.5f);
                    if (bytes == 2) { plane[i * 2] = (byte)(n >> 8); plane[i * 2 + 1] = (byte)(n & 0xFF); }
                    else plane[i] = (byte)n;
                }
                static float Encode(float v) => SrgbTransfer.EncodeFast(float.IsNaN(v) ? 0 : Math.Clamp(v, 0f, 1f));
                for (int x = 0; x < width; x++)
                {
                    float a = row[x * 4 + 3];
                    float r = row[x * 4], g = row[x * 4 + 1], b = row[x * 4 + 2];
                    long i = (long)y * width + x;
                    if (overWhite)
                    {
                        // Over white in linear light, where the picture is premultiplied.
                        float clear = 1 - Math.Clamp(float.IsNaN(a) ? 0 : a, 0f, 1f);
                        r += clear; g += clear; b += clear;
                        Store(alpha, i, 1);
                    }
                    else
                    {
                        if (a > 0) { r /= a; g /= a; b /= a; } else { r = 0; g = 0; b = 0; }
                        Store(alpha, i, a);
                        if (a > 0.5f / top) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
                    }
                    Store(red, i, Encode(r)); Store(green, i, Encode(g)); Store(blue, i, Encode(b));
                }
                rows[y] = (minX, maxX);
                return row;
            }, _ => { });
            int minXAll = Width, minY = Height, maxXAll = -1, maxY = -1;
            for (int y = 0; y < Height; y++)
            {
                if (rows[y].MaxX < 0) continue;
                minXAll = Math.Min(minXAll, rows[y].MinX); maxXAll = Math.Max(maxXAll, rows[y].MaxX);
                minY = Math.Min(minY, y); maxY = y;
            }
            if (maxXAll >= minXAll && maxY >= minY)
            {
                Box = new PixelRect(minXAll, minY, maxXAll - minXAll + 1, maxY - minY + 1);
                ReachesEdge = minXAll == 0 || minY == 0 || maxXAll == Width - 1 || maxY == Height - 1;
            }
        }

        public byte[] Cropped(byte[] plane, PixelRect box)
        {
            var output = new byte[(long)box.Width * box.Height * Bytes];
            for (int y = box.Y; y < box.MaxY; y++)
            {
                plane.AsSpan((int)(((long)y * Width + box.X) * Bytes), box.Width * Bytes)
                     .CopyTo(output.AsSpan((int)((long)(y - box.Y) * box.Width * Bytes)));
            }
            return output;
        }

        /// <summary>The four planes a layer record holds, cropped to <paramref name="box"/>.</summary>
        public ImmutableDictionary<int, ReadOnlyMemory<byte>> Planes(PixelRect box) => new Dictionary<int, ReadOnlyMemory<byte>>
        {
            [-1] = Cropped(Alpha, box), [0] = Cropped(Red, box), [1] = Cropped(Green, box), [2] = Cropped(Blue, box),
        }.ToImmutableDictionary();
    }
}
#endif
