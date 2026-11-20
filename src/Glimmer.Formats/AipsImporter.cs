// Ported from Packages/GlimmerKit/Sources/GlimmerCore/AipsImporter.swift
//
// Port notes. The Mac parses the project with JSONSerialization and reads it with Swift's casts of Foundation objects;
// the helpers at the end of this file reproduce those casts on System.Text.Json nodes (a number read as a Bool is true
// only when it is exactly 1 and false only when it is exactly 0, as NSNumber bridges; an array cast succeeds only when
// every element has the type). The adjustment parameters go through Adjustment.FromWebParameters in Glimmer.Core, which
// is this file's adjustment(_:_:). Fonts are found with FontResolver where the Mac asks Core Text. The UTType
// declaration (com.brattoo.glimmer.aips) is the app's file association on Windows. rasterizeText, the old way text
// came in as pixels, is not called anywhere in the original and is left out.
//
// Where the Mac would stop the program on a hostile file (an integer conversion out of range, groups nested deep
// enough to exhaust the stack), the port refuses the file with AipsImportError instead; see each place.
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glimmer.Core;

namespace Glimmer.Formats;

/// <summary>Why a Glimmer for the web project can't be opened (Swift's <c>AipsImportError</c>).</summary>
public sealed class AipsImportError : Exception
{
    // Swift's two cases; FileTooLarge is new (a .NET array holds at most 2 GB).
    public enum Kind { NotAnAipsFile, Invalid, FileTooLarge }

    private AipsImportError(Kind reason, string message, string? detail = null) : base(message)
    {
        Reason = reason;
        Detail = detail;
    }

    public Kind Reason { get; }

    /// <summary>What is damaged, for <see cref="Kind.Invalid"/> (Swift's associated value).</summary>
    public string? Detail { get; }

    public static AipsImportError NotAnAipsFile() => new(Kind.NotAnAipsFile, "The file is not a Glimmer for the web project (.aips).");

    public static AipsImportError Invalid(string reason) => new(Kind.Invalid, $"The .aips project is damaged: {reason}", reason);

    /// <summary>New: a file larger than a .NET array can hold.</summary>
    public static AipsImportError FileTooLarge() =>
        new(Kind.FileTooLarge, "The .aips project is larger than 2 GB, more than Glimmer can open.");
}

/// <summary>
/// Imports projects from Glimmer for the web (<c>web/src/engine/serialize.ts</c>):
/// a JSON file with a flat node list, PNG data URLs for pixels and base64 R8 masks.
/// </summary>
/// <remarks>Layers, masks, effects, editable text, vector paths, guides and the grid come across.</remarks>
public static class AipsImporter
{
    /// <summary>
    /// New: how deep layers may nest (a root layer is one deep). The Mac follows any depth, and deep enough nesting
    /// exhausts its stack; a .NET process can't recover from that, so a deeper project is refused (Photoshop files have
    /// the same limit).
    /// </summary>
    internal const int MaxGroupDepth = 64;

    /// <summary>The project in a file's bytes (Swift's <c>importProject(_:)</c>).</summary>
    /// <exception cref="AipsImportError">The bytes are no .aips project, or the project is damaged.</exception>
    /// <exception cref="ImageImportError">A layer's pixels are no picture Glimmer reads.</exception>
    public static DocumentState ImportProject(ReadOnlyMemory<byte> data)
    {
        var root = Parse(data) as JsonObject;
        if (root is null || Str(root["magic"]) != "aips") throw AipsImportError.NotAnAipsFile();
        long width = Int(root["width"]), height = Int(root["height"]);
        if (width < 1 || width > DocumentManifest.MaxDimension || height < 1 || height > DocumentManifest.MaxDimension)
            throw AipsImportError.Invalid(string.Create(CultureInfo.InvariantCulture, $"document size {width}×{height}"));
        var nodes = Dicts(root["nodes"]) ?? [];
        var byID = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (Str(node["id"]) is { } id) byID[id] = node;
        }

        var newIDs = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        Layer? LayerFor(string id)
        {
            if (!byID.TryGetValue(id, out var node) || visiting.Contains(id)) return null;
            if (visiting.Count >= MaxGroupDepth)
                throw AipsImportError.Invalid(string.Create(CultureInfo.InvariantCulture, $"layers nested more than {MaxGroupDepth} deep"));
            visiting.Add(id);
            try
            {
                var uuid = Guid.NewGuid();
                newIDs[id] = uuid;
                return MakeLayer(node, uuid, childIDs => [.. childIDs.Select(LayerFor).OfType<Layer>()]);
            }
            finally
            {
                visiting.Remove(id);
            }
        }

        var rootOrder = Strings(root["rootOrder"]) ?? [];
        var layers = rootOrder.Select(LayerFor).OfType<Layer>().ToList();
        var state = new DocumentState((int)width, (int)height, layers: layers);
        if (Str(root["activeLayerId"]) is { } active && newIDs.TryGetValue(active, out var activeID))
            state = state with { ActiveLayerID = activeID };

        // Vector paths, and the text layers that run along them.
        var pathIDs = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var p in Dicts(root["paths"]) ?? [])
        {
            var subpaths = (Dicts(p["subpaths"]) ?? []).Select(sub => new Subpath(
                (Dicts(sub["anchors"]) ?? []).Select(a =>
                {
                    var point = new Vec2(Double(a["x"], 0), Double(a["y"], 0));
                    return new PathAnchor(point, handleIn: new Vec2(Double(a["inX"], point.X), Double(a["inY"], point.Y)),
                                          handleOut: new Vec2(Double(a["outX"], point.X), Double(a["outY"], point.Y)));
                }),
                isClosed: Bool(sub["closed"]) ?? false));
            var path = new VectorPath(Str(p["name"]) ?? "Path", subpaths);
            if (Str(p["id"]) is { } pathRef) pathIDs[pathRef] = path.Id;
            state = state with { Paths = state.Paths.Add(path) };
        }
        foreach (var node in nodes)
        {
            if (Str(node["kind"]) != "text") continue;
            if (Str(node["id"]) is not { } old || !newIDs.TryGetValue(old, out var layerID)) continue;
            if (node["text"] is not JsonObject text || Str(text["pathId"]) is not { } pathRef || !pathIDs.TryGetValue(pathRef, out var pathID)) continue;
            var line = state.Paths.FirstOrDefault(p => p.Id == pathID)?.Polygons().FirstOrDefault();
            if (line is not { Length: > 1 }) continue;
            state = state.UpdateLayer(layerID, l => l.Content is TextContent t
                ? l with { Content = new TextContent(t.Text with { OnPath = [.. line], OnPathID = pathID }) }
                : l);
        }

        // Guides, grid, rulers and snapping.
        var aids = state.Aids;
        foreach (var g in Dicts(root["guides"]) ?? [])
        {
            aids = aids with { Guides = aids.Guides.Add(new Guide(Str(g["axis"]) == "v" ? GuideAxis.Vertical : GuideAxis.Horizontal, Double(g["pos"], 0))) };
        }
        if (root["grid"] is JsonObject grid)
        {
            // A missing "subdivisions" keeps the default; one that is there but not a number counts as 0, as on the Mac.
            long subdivisions = grid.ContainsKey("subdivisions") ? Int(grid["subdivisions"]) : aids.Grid.Subdivisions;
            aids = aids with
            {
                Grid = aids.Grid with
                {
                    IsVisible = Bool(grid["visible"]) ?? false,
                    Spacing = Math.Max(1, Double(grid["size"], aids.Grid.Spacing)),
                    Subdivisions = (int)Math.Clamp(subdivisions, 1, int.MaxValue),
                },
            };
        }
        if (Bool(root["rulersVisible"]) is { } rulers) aids = aids with { RulersVisible = rulers };
        if (Bool(root["snapEnabled"]) is { } snap) aids = aids with { Snap = snap };
        return state with { Aids = aids };
    }

    /// <summary>The project in a file (new: the Mac app reads the file itself and passes the bytes). A file over 2 GB is
    /// refused before it is read; other read errors are thrown as they are.</summary>
    public static DocumentState ImportProject(string path) => ImportProject(FileInput.Read(path, AipsImportError.FileTooLarge));

    /// <summary>
    /// The JSON in the file, or null when it is no JSON (the Mac throws JSONSerialization's own error there; the port
    /// says the file is no .aips project). A UTF-8 byte order mark is skipped, as JSONSerialization skips it, and a key
    /// that comes twice in an object keeps its last value, as in the dictionary JSONSerialization makes. (System.Text.Json's
    /// own <c>JsonObject</c> throws on the second, when the object is first read: the tree is built here instead.)
    /// </summary>
    private static JsonNode? Parse(ReadOnlyMemory<byte> data)
    {
        if (data.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) data = data[3..];   // the byte order mark
        try
        {
            var reader = new Utf8JsonReader(data.Span);
            var root = JsonElement.ParseValue(ref reader);
            if (reader.Read()) return null;   // more after the value
            return Build(root);
        }
        catch (JsonException) { return null; }

        static JsonNode? Build(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    var o = new JsonObject();
                    foreach (var property in e.EnumerateObject()) o[property.Name] = Build(property.Value);
                    return o;
                }
                case JsonValueKind.Array:
                {
                    var a = new JsonArray();
                    foreach (var item in e.EnumerateArray()) a.Add(Build(item));
                    return a;
                }
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                default:
                    return JsonValue.Create(e);
            }
        }
    }

    // MARK: Nodes

    private static Layer MakeLayer(JsonObject node, Guid id, Func<IReadOnlyList<string>, IReadOnlyList<Layer>> children)
    {
        string kind = Str(node["kind"]) ?? "";
        string name = Str(node["name"]) ?? "Layer";
        long x = Int(node["x"]), y = Int(node["y"]);
        var layer = new Layer(name, new RasterContent(TileGrid.Empty), id: id,
                              isVisible: Bool(node["visible"]) ?? true,
                              opacity: Math.Min(Math.Max(Double(node["opacity"], 1), 0), 1),
                              blendMode: RawValues<BlendMode>.Parse(Str(node["blendMode"]) ?? "normal") ?? BlendMode.Normal,
                              isClipped: Bool(node["clipping"]) ?? false,
                              effects: Effects(node["effects"] as JsonObject));
        if (layer.BlendMode == BlendMode.PassThrough) layer = layer with { BlendMode = BlendMode.Normal };

        switch (kind)
        {
            case "raster":
                layer = layer with { Content = new RasterContent(Pixels(node["pixels"], x, y)) };
                break;
            case "smart":
            {
                var natural = new Vec2(Int(node["naturalWidth"]), Int(node["naturalHeight"]));
                var t = node["transform"] as JsonObject ?? new JsonObject();
                var source = Pixels(node["pixels"], 0, 0);
                layer = layer with
                {
                    Content = new SmartContent(new SmartObject(source, Saturated((long)natural.X), Saturated((long)natural.Y),
                        AffineTransform.Make(Double(t["tx"], x), Double(t["ty"], y), Double(t["sx"], 1), Double(t["sy"], 1),
                                             Double(t["rot"], 0), natural))),
                };
                break;
            }
            case "text":
                layer = layer with { Content = new TextContent(TextLayer(node["text"] as JsonObject ?? new JsonObject(), x, y)) };
                break;
            case "adjustment":
            {
                var type = RawValues<AdjustmentKind>.Parse(Str(node["adjustmentType"]) ?? "") ?? AdjustmentKind.BrightnessContrast;
                layer = layer with { Content = new AdjustmentContent(Adjustment.FromWebParameters(type, node["params"] as JsonObject ?? new JsonObject())) };
                break;
            }
            case "group":
            {
                var kids = children(Strings(node["childrenIds"]) ?? []);
                layer = layer with { Content = new GroupContent(new LayerGroup(kids, isExpanded: !(Bool(node["collapsed"]) ?? false))) };
                break;
            }
            default:
                throw AipsImportError.Invalid($"unknown layer kind “{kind}”");
        }

        if (node["mask"] is JsonObject m)
        {
            // Pixel layers have layer-local masks; adjustment and group masks cover the document.
            bool docWide = kind == "adjustment" || kind == "group";
            layer = layer with { Mask = Mask(m, docWide ? 0 : x, docWide ? 0 : y) };
        }
        return layer;
    }

    private static TileGrid Pixels(JsonNode? value, long x, long y)
    {
        if (Str(value) is not { Length: > 0 } url) return TileGrid.Empty;
        int comma = url.IndexOf(',');
        if (comma < 0 || Base64(url.AsSpan(comma + 1)) is not { } data) throw AipsImportError.Invalid("unreadable pixels");
        var image = ImageImporter.Image(data);
        return ImageImporter.Tiles(image, Origin(x, y, image.Width, image.Height));
    }

    /// <summary>
    /// New: where a picture or mask goes, checked to lie in the space a document has (±2^30 pixels). The Mac takes any
    /// position, and one far enough out stops the program when the tiles are counted.
    /// </summary>
    private static (int X, int Y) Origin(long x, long y, long width, long height)
    {
        const long limit = 1L << 30;
        if (x < -limit || y < -limit || x + width > limit || y + height > limit)
            throw AipsImportError.Invalid(string.Create(CultureInfo.InvariantCulture, $"layer position {x}, {y}"));
        return ((int)x, (int)y);
    }

    /// <summary>R8 mask bytes (255 = visible) placed at the origin; everything else reveals.</summary>
    /// <remarks>
    /// Port note: a mask wider or taller than a document can be (<see cref="DocumentManifest.MaxDimension"/>) is treated as
    /// unreadable, as the Mac treats a mask whose bytes are missing: an empty mask. The Mac makes every tile such a mask
    /// covers, and a long thin one from a hostile file covers millions. Tiles that reveal everything are skipped
    /// before any pixel is written, so the work follows the mask's bytes, not its tiles.
    /// </remarks>
    private static LayerMask Mask(JsonObject m, long originX, long originY)
    {
        long w = Int(m["width"]), h = Int(m["height"]);
        bool enabled = Bool(m["enabled"]) ?? true;
        if (Str(m["data"]) is not { } b64 || Base64(b64) is not { } data || w <= 0 || h <= 0
            || w > DocumentManifest.MaxDimension || h > DocumentManifest.MaxDimension || data.Length < w * h)
        {
            return new LayerMask(isEnabled: enabled);
        }
        var (x, y) = Origin(originX, originY, w, h);
        var rect = new PixelRect(x, y, (int)w, (int)h);
        const int s = TileGeometry.Size;
        var coords = rect.TileCoords;
        var made = new Tile?[coords.Length];
        Parallel.For(0, coords.Length, i =>
        {
            var tileRect = coords[i].Rect;
            var part = tileRect.Intersection(rect);
            var src = data.Span;
            bool revealsAll = true, hidesAll = part == tileRect;
            for (int py = part.Y; py < part.MaxY && (revealsAll || hidesAll); py++)
            {
                var row = src.Slice((py - y) * (int)w + (part.X - x), part.Width);
                if (revealsAll && row.ContainsAnyExcept((byte)255)) revealsAll = false;
                if (hidesAll && row.ContainsAnyExcept((byte)0)) hidesAll = false;
            }
            // A fully revealing tile is the default; don't store it.
            if (revealsAll) return;
            if (hidesAll)
            {
                made[i] = Tile.Solid(Vector4.Zero);   // fully hidden
                return;
            }
            var buffer = new Half[TileGeometry.FloatCount];
            buffer.AsSpan().Fill((Half)1);
            for (int py = part.Y; py < part.MaxY; py++)
            {
                for (int px = part.X; px < part.MaxX; px++)
                {
                    var v = (Half)(src[(py - y) * (int)w + (px - x)] / 255f);
                    int o = ((py - tileRect.Y) * s + (px - tileRect.X)) * 4;
                    buffer[o] = v; buffer[o + 1] = v; buffer[o + 2] = v; buffer[o + 3] = v;
                }
            }
            // Uniform at a level between hidden and revealed: one colour. (Transparent can't happen here: some byte is
            // not 0, or the tile is partly outside the mask and reveals there.)
            made[i] = Tile.Make((ReadOnlySpan<Half>)buffer) ?? Tile.Solid(Vector4.Zero);
        });
        var grid = new TileGrid.Builder();
        for (int i = 0; i < coords.Length; i++)
            if (made[i] is { } tile) grid[coords[i]] = tile;
        return new LayerMask(grid.ToImmutable(), defaultValue: 1, isEnabled: enabled);
    }

    // MARK: Effects (the adjustments are Adjustment.FromWebParameters in Glimmer.Core)

    internal static LayerEffects Effects(JsonObject? fx)
    {
        if (fx is null) return new LayerEffects();
        static ShadowEffect? Shadow(JsonObject? d) => d is null ? null : new ShadowEffect
        {
            IsEnabled = Bool(d["enabled"]) ?? true,
            Color = Color(d["color"] as JsonObject),
            Opacity = Double(d["opacity"], 0.75),
            Angle = Double(d["angle"], 120),
            Distance = Double(d["distance"], 10),
            Size = Double(d["size"], 10),
            Spread = Double(d["spread"], 0),
            BlendMode = BlendMode.Normal,   // the web app composites effects with normal "over"
        };
        var e = new LayerEffects
        {
            DropShadow = Shadow(fx["dropShadow"] as JsonObject),
            InnerShadow = Shadow(fx["innerShadow"] as JsonObject),
        };
        if (fx["outerGlow"] is JsonObject glow)
        {
            e = e with
            {
                OuterGlow = new GlowEffect
                {
                    IsEnabled = Bool(glow["enabled"]) ?? true,
                    Color = Color(glow["color"] as JsonObject),
                    Opacity = Double(glow["opacity"], 0.75),
                    Size = Double(glow["size"], 10),
                    BlendMode = BlendMode.Normal,
                },
            };
        }
        if (fx["stroke"] is JsonObject stroke)
        {
            e = e with
            {
                Stroke = new StrokeEffect
                {
                    IsEnabled = Bool(stroke["enabled"]) ?? true,
                    Color = Color(stroke["color"] as JsonObject),
                    Width = Double(stroke["width"], 3),
                    Position = RawValues<StrokePosition>.Parse(Str(stroke["position"]) ?? "outside") ?? StrokePosition.Outside,
                },
            };
        }
        if (fx["colorOverlay"] is JsonObject overlay)
        {
            e = e with
            {
                ColorOverlay = new ColorOverlayEffect
                {
                    IsEnabled = Bool(overlay["enabled"]) ?? true,
                    Color = Color(overlay["color"] as JsonObject),
                    Opacity = Double(overlay["opacity"], 1),
                    BlendMode = RawValues<BlendMode>.Parse(Str(overlay["blendMode"]) ?? "normal") ?? BlendMode.Normal,
                },
            };
        }
        return e;
    }

    // MARK: Text

    /// <summary>Maps the web app's CSS text settings onto a text layer. The web drew
    /// with 0.25 em side and 0.3 em top padding inside the layer box.</summary>
    internal static TextLayer TextLayer(JsonObject t, long x, long y)
    {
        double size = Math.Max(1, Double(t["fontSize"], 48));
        var c = t["color"] as JsonObject;
        var align = RawValues<TextAlignment>.Parse(Str(t["align"]) ?? "left") ?? TextAlignment.Left;
        var layer = new TextLayer(Str(t["text"]) ?? "", new Vec2(x + size * 0.25, y + size * 0.1),
                                  fontName: FontName(Str(t["fontFamily"]), bold: Bool(t["bold"]) == true, italic: Bool(t["italic"]) == true),
                                  size: size, color: new SRGBColor(Double(c?["r"], 0), Double(c?["g"], 0), Double(c?["b"], 0)),
                                  alignment: align, lineHeight: Math.Max(0.1, Double(t["lineHeight"], 1.2)));
        if (t["warp"] is JsonObject w && RawValues<TextWarpStyle>.Parse(Str(w["style"]) ?? "") is { } style && style != TextWarpStyle.None)
            layer = layer with { Warp = new TextWarp(style, Double(w["bend"], 0), Double(w["horizontal"], 0), Double(w["vertical"], 0)) };
        return layer;
    }

    /// <summary>
    /// The PostScript name of the font the first family of a CSS font list names, made bold or italic as asked: the
    /// Mac's <c>CTFontCreateWithName</c> (Helvetica when no family is given), then
    /// <c>CTFontCreateCopyWithSymbolicTraits</c>. Here <see cref="FontResolver"/> finds the installed font, or the
    /// stand-in that is drawn for it. A style the family doesn't have is left out, as Core Text leaves the font as it is.
    /// </summary>
    internal static string FontName(string? fontFamily, bool bold, bool italic)
    {
        string family = (fontFamily ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim(' ', '"', '\'') ?? "";
        var font = FontResolver.Resolve(family.Length == 0 ? "Helvetica" : family);
        string name = font.PostScriptName ?? font.RequestedName;
        if (bold && font.Weight < 600 && FontResolver.WithBold(name, true) is { } boldName)
        {
            name = boldName;
            font = FontResolver.Resolve(name);
        }
        if (italic && !font.Italic)
        {
            // Asked for by family and style, the way FontResolver reads a PostScript name ("Georgia-BoldItalic"),
            // and kept only when the family has that member.
            string query = font.Family.Replace(" ", "", StringComparison.Ordinal) + "-" + WeightWord(font.Weight) + "Italic";
            var styled = FontResolver.Resolve(query);
            if (styled.Italic && string.Equals(styled.Family, font.Family, StringComparison.OrdinalIgnoreCase)
                && (styled.Weight >= 600) == (font.Weight >= 600) && styled.PostScriptName is { } italicName)
            {
                name = italicName;
            }
        }
        return name;
    }

    private static string WeightWord(int weight) => ((weight + 50) / 100) switch
    {
        <= 1 => "Thin",
        2 => "ExtraLight",
        3 => "Light",
        4 => "",
        5 => "Medium",
        6 => "SemiBold",
        7 => "Bold",
        8 => "ExtraBold",
        _ => "Black",
    };

    // MARK: Helpers (Swift's casts of JSONSerialization's objects)

    private static SRGBColor Color(JsonObject? c) => new(Double(c?["r"], 0), Double(c?["g"], 0), Double(c?["b"], 0));

    /// <summary><c>as? NSNumber</c>: a number, or a boolean as 1 or 0.</summary>
    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.Number => v.TryGetValue(out double d) ? d : double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
    }

    private static double Double(JsonNode? node, double fallback) => Number(node) ?? fallback;

    /// <summary>Swift's <c>Int((v as? NSNumber ?? 0).doubleValue.rounded())</c>: to the nearest, halves away from zero.
    /// Saturated where the Mac's conversion would stop the program.</summary>
    private static long Int(JsonNode? node)
    {
        double r = Math.Round(Number(node) ?? 0, MidpointRounding.AwayFromZero);
        if (double.IsNaN(r)) return 0;
        return r >= 9.2e18 ? long.MaxValue : r <= -9.2e18 ? long.MinValue : (long)r;
    }

    private static int Saturated(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    /// <summary><c>as? Bool</c>: a boolean, or a number that is exactly 1 (true) or 0 (false); anything else is null.</summary>
    private static bool? Bool(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        switch (v.GetValueKind())
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number:
                double d = Number(node)!.Value;
                return d == 1 ? true : d == 0 ? false : null;
            default: return null;
        }
    }

    /// <summary><c>as? String</c>.</summary>
    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary><c>as? [[String: Any]]</c>: null unless every element is an object.</summary>
    private static List<JsonObject>? Dicts(JsonNode? node)
    {
        if (node is not JsonArray array) return null;
        var list = new List<JsonObject>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject o) return null;
            list.Add(o);
        }
        return list;
    }

    /// <summary><c>as? [String]</c>: null unless every element is a string.</summary>
    private static List<string>? Strings(JsonNode? node)
    {
        if (node is not JsonArray array) return null;
        var list = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (Str(item) is not { } s) return null;
            list.Add(s);
        }
        return list;
    }

    /// <summary>
    /// Foundation's <c>Data(base64Encoded:options: .ignoreUnknownCharacters)</c>: characters outside the base64 alphabet
    /// are skipped; what is left must be whole, padded groups of four.
    /// </summary>
    internal static ReadOnlyMemory<byte>? Base64(ReadOnlySpan<char> text)
    {
        var bytes = new byte[text.Length / 4 * 3 + 3];
        if (Convert.TryFromBase64Chars(text, bytes, out int written)) return bytes.AsMemory(0, written);
        var kept = new char[text.Length];
        int n = 0;
        foreach (char c in text)
            if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=') kept[n++] = c;
        // (Written out: in "ok ? memory : null" the null becomes an empty Memory<byte>, through its conversion from an array.)
        if (!Convert.TryFromBase64Chars(kept.AsSpan(0, n), bytes, out written)) return null;
        return bytes.AsMemory(0, written);
    }
}
