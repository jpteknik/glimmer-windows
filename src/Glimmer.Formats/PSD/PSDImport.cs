// Ported from Packages/GlimmerKit/Sources/GlimmerCore/PSD/PSDImport.swift
#if GLIMMER_PSD   // Directory.Build.props switches this (Config/Features.xcconfig on the Mac)
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Glimmer.Core;

namespace Glimmer.Formats;

/// <summary>
/// Turns a Photoshop file into a Glimmer document and says what did not survive.
/// </summary>
/// <remarks>
/// First version: pixel layers, groups, masks, opacity, visibility, clipping and the blend
/// modes Glimmer has, for RGB and grayscale files of 8 or 16 bits per channel. Anything else
/// in the file is named in the report: text, smart objects, effects, fill and adjustment
/// layers. Files in other colour modes or depths come in as the file's own flattened
/// picture, converted by the system.
/// <para>
/// Port notes. The Mac's "converted by the system" is ImageIO; Windows has no reader for Photoshop files, so the
/// flattened picture is read here (<see cref="FlattenedImage"/>): RGB and grey at 8, 16 and 32 bits with their
/// profile, indexed colour, bitmap, Lab (converted exactly), duotone and multichannel as grey, and CMYK with an
/// uncalibrated conversion (no colour management for inks). Colour conversion of layers is <see cref="SourceColor"/>'s,
/// where the Mac draws with CoreGraphics.
/// </para>
/// </remarks>
public static class PSDImport
{
    internal const int MaxGroupDepth = 64;

    internal static readonly IReadOnlyDictionary<string, BlendMode> BlendModes = new Dictionary<string, BlendMode>(StringComparer.Ordinal)
    {
        ["pass"] = BlendMode.PassThrough, ["norm"] = BlendMode.Normal, ["dark"] = BlendMode.Darken, ["mul "] = BlendMode.Multiply,
        ["idiv"] = BlendMode.ColorBurn, ["lbrn"] = BlendMode.LinearBurn, ["dkCl"] = BlendMode.DarkerColor, ["lgCl"] = BlendMode.LighterColor,
        ["lite"] = BlendMode.Lighten, ["scrn"] = BlendMode.Screen, ["div "] = BlendMode.ColorDodge, ["lddg"] = BlendMode.LinearDodge,
        ["over"] = BlendMode.Overlay, ["sLit"] = BlendMode.SoftLight, ["hLit"] = BlendMode.HardLight, ["vLit"] = BlendMode.VividLight,
        ["lLit"] = BlendMode.LinearLight, ["pLit"] = BlendMode.PinLight, ["hMix"] = BlendMode.HardMix, ["diff"] = BlendMode.Difference,
        ["smud"] = BlendMode.Exclusion, ["fsub"] = BlendMode.Subtract, ["fdiv"] = BlendMode.Divide, ["hue "] = BlendMode.Hue,
        ["sat "] = BlendMode.Saturation, ["colr"] = BlendMode.Color, ["lum "] = BlendMode.Luminosity,
    };

    internal static readonly IReadOnlyDictionary<string, string> BlendNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["diss"] = "Dissolve" };

    /// <summary>The additional blocks that say a layer is more than its pixels, and what is said about each.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Adjustments = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["brit"] = "Brightness/Contrast", ["levl"] = "Levels", ["curv"] = "Curves", ["expA"] = "Exposure", ["hue2"] = "Hue/Saturation",
        ["hue "] = "Hue/Saturation", ["vibA"] = "Vibrance", ["blnc"] = "Color Balance", ["blwh"] = "Black & White", ["phfl"] = "Photo Filter",
        ["mixr"] = "Channel Mixer", ["nvrt"] = "Invert", ["post"] = "Posterize", ["thrs"] = "Threshold", ["grdm"] = "Gradient Map",
        ["selc"] = "Selective Color", ["clrL"] = "Color Lookup",
    };

    internal static readonly IReadOnlyDictionary<string, string> Fills = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SoCo"] = "solid color", ["GdFl"] = "gradient", ["PtFl"] = "pattern",
    };

    private static readonly string[] EffectKeys = ["lfx2", "lrFX", "lmfx"];

    /// <summary>The report while it is made (Swift mutates its <c>PSDImportReport</c>; here the record is made at the end).</summary>
    private sealed class ReportDraft(string source)
    {
        public string Source = source;
        public int LayersInFile, LayersKept, GroupsKept;
        public bool Flattened;
        public bool? FileHasNoPicture;
        public readonly List<PSDImportReport.Note> Notes = [];

        public void Note(PSDImportReport.Kind kind, string? layer = null, string? detail = null, bool changesLook = false) =>
            Notes.Add(new PSDImportReport.Note(kind, layer, detail, changesLook));

        public PSDImportReport Build() => new(Source)
        {
            LayersInFile = LayersInFile, LayersKept = LayersKept, GroupsKept = GroupsKept, Flattened = Flattened,
            Notes = [.. Notes], FileHasNoPicture = FileHasNoPicture,
        };
    }

    /// <summary>The document a Photoshop file makes, and the report on what came through (Swift's <c>document(from:limits:)</c>).</summary>
    /// <exception cref="PSDFile.Problem">The file can't be read; the message says why.</exception>
    public static (DocumentState State, PSDImportReport Report) Document(ReadOnlyMemory<byte> data, PSDFile.Limits? limits = null)
    {
        var header = PSDFile.ReadHeader(data);
        var report = new ReportDraft(Describe(header));
        bool layered = (header.Mode == PSDFile.ColorMode.Rgb || header.Mode == PSDFile.ColorMode.Grayscale) && (header.Depth == 8 || header.Depth == 16);
        if (!layered) return Finish(Flattened(data, header, report, why: true), report);
        var file = new PSDFile(data, limits);
        if (PSDFile.HasFlattenedPicture(data) == false) report.FileHasNoPicture = true;
        report.LayersInFile = file.Layers.Count(l => l.Group != PSDFile.GroupMark.End);
        if (file.Layers.IsEmpty) return Finish(Flattened(data, header, report, why: false), report);
        var space = ColorSpace(file);
        // The file lists layers bottom to top; a group is everything between its end mark and its own record.
        var stack = new List<List<Layer>> { new() };
        foreach (var source in file.Layers)
        {
            switch (source.Group)
            {
                case PSDFile.GroupMark.End:
                    if (stack.Count > MaxGroupDepth) throw new PSDFile.Problem("This Photoshop file nests its groups deeper than Glimmer reads.");
                    stack.Add([]);
                    break;
                case PSDFile.GroupMark.Open:
                case PSDFile.GroupMark.Closed:
                {
                    List<Layer> children = [];
                    if (stack.Count > 1)
                    {
                        children = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                    }
                    else
                    {
                        report.Note(PSDImportReport.Kind.GroupEndMissing, layer: source.Name);
                    }
                    var blend = BlendModeOf(source, isGroup: true, report);
                    var group = new Layer(source.Name, new GroupContent(new LayerGroup(children, isExpanded: source.Group == PSDFile.GroupMark.Open)),
                                          isVisible: source.IsVisible, opacity: source.Opacity / 255.0, blendMode: blend, isClipped: source.IsClipped)
                    {
                        Mask = MaskOf(source),
                    };
                    NoteExtras(source, isGroup: true, report);
                    stack[^1].Add(group);
                    report.GroupsKept += 1;
                    break;
                }
                default:
                    if (PixelLayer(source, file, space, report) is { } layer)
                    {
                        stack[^1].Add(layer);
                        report.LayersKept += 1;
                    }
                    break;
            }
        }
        // Groups the file opened and never closed: their layers are kept, at the level above.
        while (stack.Count > 1)
        {
            var orphans = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            stack[^1].AddRange(orphans);
            report.Note(PSDImportReport.Kind.GroupRecordMissing);
        }
        report.LayersKept += report.GroupsKept;
        // Photoshop blends in gamma-encoded values; the document keeps that, so the layers look as they did.
        var state = new DocumentState(header.Width, header.Height, blendSpace: BlendSpace.Gamma, layers: stack[0]);
        return Finish(state, report);
    }

    /// <summary>The same for a file on disk (new). A file over 2 GB is refused before it is read: a .NET array holds no more.</summary>
    public static (DocumentState State, PSDImportReport Report) Document(string path, PSDFile.Limits? limits = null) =>
        Document(FileInput.Read(path, () => new PSDFile.Problem("This Photoshop file is larger than 2 GB, more than Glimmer can open.")), limits);

    private static (DocumentState State, PSDImportReport Report) Finish(DocumentState state, ReportDraft report)
    {
        var built = report.Build();
        return (state with { ActiveLayerID = state.Layers.IsEmpty ? null : state.Layers[^1].Id, ImportReport = built }, built);
    }

    internal static string Describe(PSDFile.FileHeader header)
    {
        string mode = header.Mode switch
        {
            PSDFile.ColorMode.Bitmap => "Bitmap",
            PSDFile.ColorMode.Grayscale => "Grayscale",
            PSDFile.ColorMode.Indexed => "Indexed color",
            PSDFile.ColorMode.Rgb => "RGB",
            PSDFile.ColorMode.Cmyk => "CMYK",
            PSDFile.ColorMode.Multichannel => "Multichannel",
            PSDFile.ColorMode.Duotone => "Duotone",
            _ => "Lab",
        };
        return string.Create(CultureInfo.InvariantCulture, $"{mode}, {header.Depth} {(header.Depth == 1 ? "bit" : "bits")} per channel")
               + (header.IsLarge ? ", large document format" : "");
    }

    /// <summary>The file's own flattened picture as one layer (on the Mac, converted by the system, which honours the
    /// embedded profile; here by <see cref="FlattenedImage"/>).</summary>
    private static DocumentState Flattened(ReadOnlyMemory<byte> data, PSDFile.FileHeader header, ReportDraft report, bool why)
    {
        // A file saved without its flattened picture holds a blank one in its place: opening that would show an empty page as if it were the image.
        if (PSDFile.HasFlattenedPicture(data) == false)
            throw new PSDFile.Problem($"Glimmer can't read the layers of this Photoshop file ({report.Source}), and the file was saved without a flattened picture to show instead. Save it again from Photoshop with Maximize Compatibility on, or as RGB with 8 or 16 bits per channel.");
        DecodedImage image;
        try { image = ImageImporter.Image(data); }
        catch (ImageImportError)
        {
            throw new PSDFile.Problem($"This Photoshop file carries no flattened picture that Glimmer can read, and its layers are in a form Glimmer doesn't read ({report.Source}).");
        }
        report.Flattened = true;
        if (why) report.Note(PSDImportReport.Kind.LayersNotRead, detail: report.Source);
        var layer = new Layer("Background", new RasterContent(ImageImporter.Tiles(image)));
        return new DocumentState(image.Width, image.Height, layers: [layer]);
    }

    private static SourceColor ColorSpace(PSDFile file)
    {
        bool gray = file.Header.Mode == PSDFile.ColorMode.Grayscale;
        if (file.IccProfile is { Length: > 0 } icc && SourceColor.FromIcc(icc.Span, gray) is { } space) return space;
        return gray ? SourceColor.GrayGamma22 : SourceColor.Srgb;
    }

    private static BlendMode BlendModeOf(PSDFile.Layer source, bool isGroup, ReportDraft report)
    {
        if (BlendModes.TryGetValue(source.BlendKey, out var mode)) return mode == BlendMode.PassThrough && !isGroup ? BlendMode.Normal : mode;
        string name = BlendNames.TryGetValue(source.BlendKey, out var known) ? known : $"“{source.BlendKey.Trim(' ', '\t')}”";
        report.Note(PSDImportReport.Kind.BlendModeBecameNormal, layer: source.Name, detail: name, changesLook: true);
        return BlendMode.Normal;
    }

    /// <summary>
    /// The layer's mask as a Glimmer mask, in whole tiles, so that what lies outside the mask's rectangle inside a tile
    /// gets the mask's default.
    /// </summary>
    /// <remarks>
    /// The Mac makes the tiles with <c>Selection.rasterize</c>, which leaves out a tile that comes out all zero; inside a
    /// mask whose default is to show, such a tile then shows what the file hides (a black area of 256 × 256 pixels or
    /// more). Here a tile that hides everything is kept, as one colour, as the .aips import keeps it.
    /// </remarks>
    internal static LayerMask? MaskOf(PSDFile.Layer source)
    {
        if (source.Mask is not { } mask) return null;
        float outside = mask.DefaultColor / 255f;
        if (mask.Pixels is not { } pixels || mask.Rect.Width <= 0 || mask.Rect.Height <= 0)
        {
            // A mask with no pixels is all its default colour. White everywhere is no mask at all.
            return mask.DefaultColor == 255 ? null : new LayerMask(defaultValue: outside, isEnabled: !mask.IsDisabled);
        }
        // The plane was read, so its size is bounded; its position is not (only the layer's rectangle is checked).
        if (Math.Abs((long)mask.Rect.Left) > 1 << 30 || Math.Abs((long)mask.Rect.Top) > 1 << 30)
            throw new PSDFile.Problem("A layer in this Photoshop file has a damaged rectangle.");
        var rect = new PixelRect(mask.Rect.Left, mask.Rect.Top, (int)mask.Rect.Width, (int)mask.Rect.Height);
        const int s = TileGeometry.Size;
        int x0 = PixelRect.FloorDiv(rect.X, s) * s, y0 = PixelRect.FloorDiv(rect.Y, s) * s;
        int x1 = (PixelRect.FloorDiv(rect.MaxX - 1, s) + 1) * s, y1 = (PixelRect.FloorDiv(rect.MaxY - 1, s) + 1) * s;
        var coords = new PixelRect(x0, y0, x1 - x0, y1 - y0).TileCoords;
        var made = new Tile[coords.Length];
        var values = pixels;
        Parallel.For(0, coords.Length, i =>
        {
            var tileRect = coords[i].Rect;
            var buffer = new float[TileGeometry.FloatCount];
            var span = values.Span;
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    int px = tileRect.X + x, py = tileRect.Y + y;
                    float v = rect.Contains(px, py) ? span[(int)((long)(py - rect.Y) * rect.Width + (px - rect.X))] / 255f : outside;
                    int o = (y * s + x) * 4;
                    buffer[o] = v; buffer[o + 1] = v; buffer[o + 2] = v; buffer[o + 3] = v;
                }
            }
            made[i] = Tile.Make((ReadOnlySpan<float>)buffer) ?? Tile.Solid(Vector4.Zero);
        });
        var grid = new TileGrid.Builder();
        for (int i = 0; i < coords.Length; i++) grid[coords[i]] = made[i];
        return new LayerMask(grid.ToImmutable(), defaultValue: outside, isEnabled: !mask.IsDisabled);
    }

    private static void NoteExtras(PSDFile.Layer source, bool isGroup, ReportDraft report)
    {
        var keys = source.ExtraKeys;
        if (keys.Any(k => EffectKeys.Contains(k)))
            report.Note(PSDImportReport.Kind.EffectsNotRead, layer: source.Name, changesLook: true);
        if (!isGroup && (keys.Contains("vmsk") || keys.Contains("vsms")) && !keys.Any(Fills.ContainsKey))
            report.Note(PSDImportReport.Kind.VectorMaskNotRead, layer: source.Name, changesLook: true);
        if (keys.Contains("SoLd") || keys.Contains("SoLE") || keys.Contains("PlLd") || keys.Contains("plLd"))
            report.Note(PSDImportReport.Kind.WasSmartObject, layer: source.Name);
        if (keys.Contains("TySh") || keys.Contains("tySh"))
            report.Note(PSDImportReport.Kind.WasText, layer: source.Name, detail: Text(source));
    }

    /// <summary>A layer of pixels, or null for a layer that has none and can't be drawn (it is named in the report).</summary>
    private static Layer? PixelLayer(PSDFile.Layer source, PSDFile file, SourceColor space, ReportDraft report)
    {
        var keys = source.ExtraKeys;
        if (AdjustmentOf(source) is { } settings)
        {
            var blend = BlendModeOf(source, isGroup: false, report);
            return new Layer(source.Name, new AdjustmentContent(settings), isVisible: source.IsVisible, opacity: source.Opacity / 255.0,
                             blendMode: blend, isClipped: source.IsClipped) { Mask = MaskOf(source) };
        }
        if (FillLayer(source, file, report) is { } fill) return fill;
        // The first such block in the file's order. (Swift takes the first of a Set, whose order is not fixed; a layer
        // has one of them in practice.)
        if (keys.FirstOrDefault(Adjustments.ContainsKey) is { } adjustmentKey)
        {
            report.Note(PSDImportReport.Kind.AdjustmentLeftOut, layer: source.Name, detail: Adjustments[adjustmentKey], changesLook: source.IsVisible);
            return null;
        }
        if (keys.FirstOrDefault(Fills.ContainsKey) is { } fillKey)
        {
            report.Note(PSDImportReport.Kind.FillLeftOut, layer: source.Name, detail: Fills[fillKey], changesLook: source.IsVisible);
            return null;
        }
        var layerBlend = BlendModeOf(source, isGroup: false, report);
        var layer = new Layer(source.Name, new RasterContent(TileGrid.Empty), isVisible: source.IsVisible, opacity: source.Opacity / 255.0,
                              blendMode: layerBlend, isClipped: source.IsClipped)
        {
            Mask = MaskOf(source),
            FormerText = Text(source),
        };
        NoteExtras(source, isGroup: false, report);
        if (source.Rect.Width > 0 && source.Rect.Height > 0)
        {
            if (ImageOf(source, gray: file.Header.Mode == PSDFile.ColorMode.Grayscale, space) is not { } image)
            {
                report.Note(PSDImportReport.Kind.PixelsIncomplete, layer: source.Name, changesLook: source.IsVisible);
                return layer;
            }
            layer = layer with { Content = new RasterContent(ImageImporter.Tiles(image, (source.Rect.Left, source.Rect.Top))) };
        }
        return layer;
    }

    // MARK: Layers that are more than pixels

    /// <summary>The words of a text layer, when its type block gives them readably (null otherwise).</summary>
    internal static string? Text(PSDFile.Layer source)
    {
        if (!source.Blocks.TryGetValue("TySh", out var block)) return null;
        var r = new PSDBytes(block);
        // Version, the six numbers of the transform, the text version and the descriptor's version come first.
        if (!r.TrySkip(2 + 48 + 2 + 4)) return null;
        int budget = PSDDescriptor.MaxItems;
        PSDValue data;
        try { data = PSDDescriptor.Read(ref r, ref budget); } catch (PSDFile.Problem) { return null; }
        foreach (var value in data.All)
        {
            if (value is PSDValue.String { Value: { Length: > 0 } words })
                return PrefixCharacters(words.Replace("\r", " ", StringComparison.Ordinal), 200);
        }
        return null;
    }

    /// <summary>The first <paramref name="count"/> characters as a person counts them (Swift's <c>String.prefix</c>
    /// counts grapheme clusters, not UTF-16 units).</summary>
    private static string PrefixCharacters(string text, int count)
    {
        var e = StringInfo.GetTextElementEnumerator(text);
        int taken = 0, end = 0;
        while (taken < count && e.MoveNext()) { taken += 1; end = e.ElementIndex + ((string)e.Current).Length; }
        return text[..end];
    }

    /// <summary>The adjustment layers whose data the specification lays out and whose arithmetic is the
    /// same everywhere: Invert, Levels, and Curves (with a note: its points are the same, the
    /// curve between them need not be).</summary>
    internal static Adjustment? AdjustmentOf(PSDFile.Layer source)
    {
        if (source.ExtraKeys.Contains("nvrt")) return new Adjustment(AdjustmentKind.Invert);
        if (source.Blocks.TryGetValue("levl", out var levl) && Levels(levl.Span) is { } perChannel)
            return new Adjustment(AdjustmentKind.Levels) { Levels = perChannel };
        if (source.Blocks.TryGetValue("curv", out var curv) && Curves(curv.Span) is { } curves)
        {
            // Drawn between the points as Photoshop draws it.
            return new Adjustment(AdjustmentKind.Curves) { Curves = curves, Flags = ImmutableDictionary<string, bool>.Empty.Add(Adjustment.NaturalCurveFlag, true) };
        }
        return null;
    }

    internal static readonly CurveChannel[] Channels = [CurveChannel.Rgb, CurveChannel.Red, CurveChannel.Green, CurveChannel.Blue];

    /// <summary>Levels: version 2, then record sets of five shorts (input floor and ceiling, output
    /// floor and ceiling, gamma × 100). The first is the master, the next three red, green, blue.</summary>
    internal static ImmutableDictionary<CurveChannel, LevelsChannel>? Levels(ReadOnlySpan<byte> block)
    {
        if (block.Length < 2 || BinaryPrimitives.ReadUInt16BigEndian(block) != 2) return null;
        var result = ImmutableDictionary.CreateBuilder<CurveChannel, LevelsChannel>();
        int at = 2;
        foreach (var channel in Channels)
        {
            if (at + 10 > block.Length) return null;
            int inFloor = BinaryPrimitives.ReadUInt16BigEndian(block[at..]), inCeiling = BinaryPrimitives.ReadUInt16BigEndian(block[(at + 2)..]);
            int outFloor = BinaryPrimitives.ReadUInt16BigEndian(block[(at + 4)..]), outCeiling = BinaryPrimitives.ReadUInt16BigEndian(block[(at + 6)..]);
            int gamma = BinaryPrimitives.ReadUInt16BigEndian(block[(at + 8)..]);
            at += 10;
            if (!(inFloor <= 253 && inCeiling is >= 2 and <= 255 && inFloor < inCeiling && outFloor <= 255 && outCeiling <= 255 && gamma is >= 10 and <= 999)) return null;
            result[channel] = new LevelsChannel(inFloor / 255.0, inCeiling / 255.0, gamma / 100.0, outFloor / 255.0, outCeiling / 255.0);
        }
        return result.ToImmutable();
    }

    /// <summary>Curves. In files from Photoshop the block is: one byte, the version (1 or 4), then for
    /// version 1 a four-byte bit map of which curves are there, for version 4 their count; each
    /// curve is a count of 2 to 19 points, each point its output then its input, 0 to 255.
    /// (The specification's page describes the load-file form, without the leading byte and
    /// with a two-byte bit map; the layout here is what real files hold, seen 2026-11-16.)
    /// Bit 0 is the composite curve, bits 1 to 3 red, green and blue.</summary>
    internal static ImmutableDictionary<CurveChannel, ImmutableArray<CurvePoint>>? Curves(ReadOnlySpan<byte> block)
    {
        var r = new PSDBytes(block.ToArray());
        if (!r.TrySkip(1) || !r.TryU16(out ushort version) || (version != 1 && version != 4)) return null;
        var present = new List<int>();
        if (version == 1)
        {
            if (!r.TryU32(out uint map)) return null;
            for (int i = 0; i < 32; i++) if ((map & (1u << i)) != 0) present.Add(i);
        }
        else
        {
            if (!r.TryU16(out ushort count) || count > 19) return null;
            for (int i = 0; i < count; i++) present.Add(i);
        }
        var result = ImmutableDictionary.CreateBuilder<CurveChannel, ImmutableArray<CurvePoint>>();
        foreach (int index in present)
        {
            if (!r.TryU16(out ushort count) || count is < 2 or > 19) return null;
            var points = ImmutableArray.CreateBuilder<CurvePoint>(count);
            for (int i = 0; i < count; i++)
            {
                if (!r.TryU16(out ushort output) || !r.TryU16(out ushort input) || output > 255 || input > 255) return null;
                points.Add(new CurvePoint(input / 255.0, output / 255.0));
            }
            for (int i = 1; i < points.Count; i++) if (!(points[i - 1].X < points[i].X)) return null;
            if (index >= Channels.Length) return null;   // a curve for a channel Glimmer has no curve for: not guessed at
            result[Channels[index]] = points.MoveToImmutable();
        }
        return result.Count == 0 ? null : result.ToImmutable();
    }

    /// <summary>A solid colour fill, with its vector mask when it is a shape: a real fill layer, editable.</summary>
    private static Layer? FillLayer(PSDFile.Layer source, PSDFile file, ReportDraft report)
    {
        if (!source.Blocks.TryGetValue("SoCo", out var block)) return null;
        var r = new PSDBytes(block);
        if (!r.TrySkip(4)) return null;   // the version
        int budget = PSDDescriptor.MaxItems;
        PSDValue settings;
        try { settings = PSDDescriptor.Read(ref r, ref budget); } catch (PSDFile.Problem) { return null; }
        if (ColorIn(settings) is not { } fillColor) return null;
        var blend = BlendModeOf(source, isGroup: false, report);
        var layer = new Layer(source.Name, new FillContent(new Fill.Solid(fillColor)), isVisible: source.IsVisible, opacity: source.Opacity / 255.0,
                              blendMode: blend, isClipped: source.IsClipped)
        {
            Mask = MaskOf(source),
        };
        if (source.Blocks.TryGetValue("vmsk", out var vector) || source.Blocks.TryGetValue("vsms", out vector))
        {
            var v = new PSDBytes(vector);
            // Version, then flags. The specification numbers the flags "bit 1 = invert, bit 2 = not
            // link, bit 3 = disable"; read here as the three lowest bits in that order.
            if (!v.TrySkip(4) || !v.TryU32(out uint flags)) return null;
            var subpaths = PSDPath.Subpaths(v.Rest, file.Header.Width, file.Header.Height);
            if (subpaths.Length == 0 || (flags & 1) != 0)
            {
                // No outline that could be read, or an inverted one, which Glimmer's shapes don't have:
                // the layer is left out, and the caller says so.
                return null;
            }
            var path = new VectorPath(source.Name, subpaths);
            bool outlineOn = (flags & 4) == 0;
            if (layer.Mask is { IsEnabled: true } painted && outlineOn)
            {
                // An outline and a painted mask on the same shape (seen in a file from Photoshop,
                // 2026-11-16; the painted one used to be dropped without a word). A Glimmer layer
                // has one mask: the two are multiplied into it, and the report says the outline
                // is no longer a path.
                var a = path.AsSelection;
                var b = new Selection(painted.Tiles, painted.DefaultValue);
                var coords = a.Tiles.Coords.Union(b.Tiles.Coords).ToArray();
                var made = new Tile[coords.Length];
                Parallel.For(0, coords.Length, i =>
                {
                    var x = a.TileAt(coords[i]).ToHalfArray();
                    var y = b.TileAt(coords[i]).ToHalfArray();
                    var output = new Half[x.Length];
                    for (int j = 0; j < x.Length; j += 4)
                    {
                        var value = (Half)((float)x[j + 3] * (float)y[j + 3]);
                        output[j] = value; output[j + 1] = value; output[j + 2] = value; output[j + 3] = value;
                    }
                    made[i] = Tile.Make((ReadOnlySpan<Half>)output) ?? Tile.Solid(Vector4.Zero);
                });
                var grid = new TileGrid.Builder();
                for (int i = 0; i < coords.Length; i++) grid[coords[i]] = made[i];
                layer = layer with { Mask = new LayerMask(grid.ToImmutable(), defaultValue: a.DefaultValue * b.DefaultValue) };
                report.Note(PSDImportReport.Kind.MasksMerged, layer: source.Name);
            }
            else if (!outlineOn && layer.Mask is not null)
            {
                // The outline is switched off and there is a painted mask: the painted mask stays as it is.
                report.Note(PSDImportReport.Kind.VectorMaskNotRead, layer: source.Name);
            }
            else
            {
                layer = layer with { Mask = path.AsSelection.AsMask with { IsEnabled = outlineOn }, VectorMask = path };
            }
        }
        if (source.ExtraKeys.Any(k => EffectKeys.Contains(k)))
            report.Note(PSDImportReport.Kind.EffectsNotRead, layer: source.Name, changesLook: true);
        return layer;
    }

    /// <summary>The colour in a fill's settings: an object holding red, green and blue from 0 to 255.
    /// The specification does not name the keys; red, green and blue are taken by the keys
    /// other files use ("Rd  ", "Grn ", "Bl  ") when present, and otherwise in that order.</summary>
    internal static SRGBColor? ColorIn(PSDValue settings)
    {
        foreach (var value in settings.All)
        {
            if (value is not PSDValue.Object o) continue;
            var numbers = o.Items.Where(i => i.Value.Number is not null).Select(i => (i.Key, Value: i.Value.Number!.Value)).ToList();
            if (numbers.Count != 3 || numbers.Count != o.Items.Length || !numbers.All(n => n.Value >= 0 && n.Value <= 255)) continue;
            var named = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (key, number) in numbers) named.TryAdd(key, number);
            if (named.TryGetValue("Rd  ", out var red) && named.TryGetValue("Grn ", out var green) && named.TryGetValue("Bl  ", out var blue))
                return new SRGBColor(red / 255, green / 255, blue / 255);
            return new SRGBColor(numbers[0].Value / 255, numbers[1].Value / 255, numbers[2].Value / 255);
        }
        return null;
    }

    /// <summary>The layer's planes as one picture: colour (or gray) with its transparency, not premultiplied.</summary>
    internal static DecodedImage? ImageOf(PSDFile.Layer source, bool gray, SourceColor space)
    {
        int width = (int)source.Rect.Width, height = (int)source.Rect.Height, bytes = source.BytesPerSample;
        if (bytes is not (1 or 2)) return null;
        int[] colorIDs = gray ? [0] : [0, 1, 2];
        long count = (long)width * height * bytes;
        var planes = new List<ReadOnlyMemory<byte>>();
        foreach (int id in colorIDs)
        {
            if (!source.Planes.TryGetValue(id, out var plane) || plane.Length != count) return null;
            planes.Add(plane);
        }
        ReadOnlyMemory<byte>? alpha = null;
        if (source.Planes.TryGetValue(-1, out var transparency))
        {
            if (transparency.Length != count) return null;
            alpha = transparency;
        }
        return PSDFlattenedImage.Interleaved(width, height, bytes, planes, alpha, space);
    }

}
#endif
