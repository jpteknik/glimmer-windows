// Ported from Packages/GlimmerKit/Sources/GlimmerRender/AipsExporter.swift. The Mac builds the file with
// JSONSerialization; here System.Text.Json writes the same keys and values (key order differs: Foundation's dictionaries
// have none). Pixels go out through ImageExporter as 8-bit sRGB PNG data URLs, as on the Mac. A mask's bytes are read
// from its tiles directly, which is what the Mac's route (render the mask alone, keep the alpha channel) gives. Fonts:
// the family is the installed font's (Core Text's CTFontCopyFamilyName), or the family the stored name spells when the
// font isn't installed here, so a document keeps asking for its font.
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glimmer.Core;
using Glimmer.Render;

namespace Glimmer.Formats;

/// <summary>
/// Writes projects for Glimmer for the web (<c>web/src/engine/serialize.ts</c>), the reverse of
/// <see cref="AipsImporter"/>.
/// </summary>
/// <remarks>
/// The web app has no fill layers, perspective transforms or smart filters, so those layers are written as pixels.
/// Everything else keeps its settings.
/// </remarks>
public static class AipsExporter
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] ExportProject(DocumentState state)
    {
        var nodes = new JsonArray();
        void Add(Layer layer, Guid? parent)
        {
            var node = Node(layer, state);
            node["id"] = Uuid(layer.Id);
            node["name"] = layer.Name;
            node["visible"] = layer.IsVisible;
            node["opacity"] = layer.Opacity;
            // The web app has no pass-through groups.
            node["blendMode"] = RawValues<BlendMode>.Of(layer.BlendMode.WebMode());
            node["parentId"] = parent is { } p ? Uuid(p) : null;
            if (layer.Content is GroupContent g)
            {
                node["childrenIds"] = new JsonArray([.. g.Group.Children.Select(c => (JsonNode)Uuid(c.Id))]);
                foreach (var child in g.Group.Children) Add(child, layer.Id);
            }
            nodes.Add(node);
        }
        foreach (var layer in state.Layers) Add(layer, null);

        var file = new JsonObject
        {
            ["magic"] = "aips",
            ["version"] = 1,
            ["width"] = state.Width,
            ["height"] = state.Height,
            ["activeLayerId"] = state.ActiveLayerID is { } active ? Uuid(active) : null,
            ["rootOrder"] = new JsonArray([.. state.Layers.Select(l => (JsonNode)Uuid(l.Id))]),
            ["nodes"] = nodes,
            ["paths"] = new JsonArray([.. state.Paths.Select(path => (JsonNode)new JsonObject
            {
                ["id"] = Uuid(path.Id),
                ["name"] = path.Name,
                ["subpaths"] = new JsonArray([.. path.Subpaths.Select(sub => (JsonNode)new JsonObject
                {
                    ["closed"] = sub.IsClosed,
                    ["anchors"] = new JsonArray([.. sub.Anchors.Select(a => (JsonNode)new JsonObject
                    {
                        ["x"] = a.Point.X, ["y"] = a.Point.Y, ["inX"] = a.HandleIn.X, ["inY"] = a.HandleIn.Y,
                        ["outX"] = a.HandleOut.X, ["outY"] = a.HandleOut.Y,
                    })]),
                })]),
            })]),
            ["guides"] = new JsonArray([.. state.Aids.Guides.Select(g => (JsonNode)new JsonObject
            {
                ["id"] = Uuid(g.Id), ["axis"] = g.Axis == GuideAxis.Vertical ? "v" : "h", ["pos"] = g.Position,
            })]),
            ["grid"] = new JsonObject
            {
                ["visible"] = state.Aids.Grid.IsVisible, ["size"] = state.Aids.Grid.Spacing, ["subdivisions"] = state.Aids.Grid.Subdivisions,
            },
            ["rulersVisible"] = state.Aids.RulersVisible,
            ["snapEnabled"] = state.Aids.Snap,
        };
        return JsonSerializer.SerializeToUtf8Bytes(file, Options);
    }

    /// <summary>Swift's <c>uuidString</c>: upper case.</summary>
    private static string Uuid(Guid id) => id.ToString("D").ToUpperInvariant();

    // MARK: Nodes

    private static JsonObject Node(Layer layer, DocumentState state)
    {
        var node = new JsonObject { ["clipping"] = layer.IsClipped };
        // Where the layer's own box is; masks of pixel layers are stored relative to it.
        var box = state.Bounds;
        switch (layer.Content)
        {
            case GroupContent g:
                node = new JsonObject { ["kind"] = "group", ["collapsed"] = !g.Group.IsExpanded };
                break;
            case AdjustmentContent a:
                node["kind"] = "adjustment";
                node["adjustmentType"] = RawValues<AdjustmentKind>.Of(a.Adjustment.Kind);
                node["params"] = Params(a.Adjustment);
                break;
            case TextContent { Text: var t } when layer.Mask is null && t.Transform is null && !t.HasStyledRuns:
                // (Transformed text goes out as pixels: the web app can't rotate or skew text.)
                // (So does text with styled words: the web app has one style per text layer.)
                // (Masked text goes out as pixels: the web app sizes text boxes itself, so a mask wouldn't line up.)
                node["kind"] = "text";
                // The web app draws text with 0.25 em side and 0.1 em top padding inside its box.
                box = new PixelRect((int)Math.Round(t.Origin.X - t.Size * 0.25, MidpointRounding.AwayFromZero),
                                    (int)Math.Round(t.Origin.Y - t.Size * 0.1, MidpointRounding.AwayFromZero), t.Bounds.Width, t.Bounds.Height);
                node["text"] = Text(t);
                break;
            case SmartContent { Smart: var s } when s.Transform.IsAffine && s.Filters.IsEmpty:
            {
                var d = s.Transform.Decompose(new Vec2(s.Width, s.Height));
                node["kind"] = "smart";
                node["naturalWidth"] = s.Width;
                node["naturalHeight"] = s.Height;
                node["transform"] = new JsonObject { ["tx"] = d.X, ["ty"] = d.Y, ["sx"] = d.ScaleX, ["sy"] = d.ScaleY, ["rot"] = d.Rotation };
                node["pixels"] = Png(s.Source, new PixelRect(0, 0, Math.Max(s.Width, 1), Math.Max(s.Height, 1)));
                box = s.Bounds;
                break;
            }
            default:
            {
                // Pixels, and everything the web app can't describe (fills, shapes, warped smart objects).
                var grid = layer.Content is RasterContent r
                    ? r.Tiles
                    : DocumentCompositor.Rasterize(layer with
                    {
                        Mask = null, Effects = LayerEffects.None, Opacity = 1, BlendMode = BlendMode.Normal, IsClipped = false,
                    }, state.Bounds, state.BlendSpace);
                box = grid.TileBounds ?? new PixelRect(0, 0, 1, 1);
                node["kind"] = "raster";
                node["pixels"] = Png(grid, box);
                break;
            }
        }
        string kind = (string)node["kind"]!;
        if (kind != "group" && kind != "adjustment")
        {
            node["x"] = box.X;
            node["y"] = box.Y;
            node["width"] = box.Width;
            node["height"] = box.Height;
            if (Effects(layer.Effects) is { } fx) node["effects"] = fx;
        }
        if (layer.Mask is { } mask)
        {
            bool docWide = layer.IsGroup || layer.IsAdjustment;
            node["mask"] = Mask(mask, docWide ? state.Bounds : box);
        }
        return node;
    }

    /// <summary>The grid's pixels inside <paramref name="rect"/> as a PNG data URL.</summary>
    private static string Png(TileGrid grid, PixelRect rect)
    {
        var moved = rect.X == 0 && rect.Y == 0 ? grid : TileOperations.Translate(grid, (-rect.X, -rect.Y));
        var single = new DocumentState(rect.Width, rect.Height, layers: [new Layer("pixels", new RasterContent(moved))]);
        using var flat = ImageExporter.Flatten(single);
        return "data:image/png;base64," + Convert.ToBase64String(ImageExporter.Encode(flat, new ExportOptions(ExportFormat.Png)));
    }

    /// <summary>One byte per pixel, 255 = visible, base64 encoded.</summary>
    private static JsonObject Mask(LayerMask mask, PixelRect rect)
    {
        // The mask value is in every channel of its tiles, so alpha carries it exactly. Where no tile is stored the
        // mask is its default: revealed when that is at least one half, as the Mac fills those tiles.
        int w = rect.Width, h = rect.Height;
        var bytes = new byte[(long)w * h];
        byte missing = mask.DefaultValue >= 0.5 ? (byte)255 : (byte)0;
        var coords = rect.TileCoords;
        Parallel.For(0, coords.Length, i =>
        {
            var tileRect = coords[i].Rect;
            var part = tileRect.Intersection(rect);
            var tile = mask.Tiles[coords[i]];
            var pixels = tile is null || tile.IsSolid ? ReadOnlyMemory<Half>.Empty : tile.Pixels;
            byte solid = tile is null ? missing : tile.IsSolid ? Byte(tile.SolidColor.W) : (byte)0;
            for (int y = part.Y; y < part.MaxY; y++)
            {
                var row = bytes.AsSpan((int)((long)(y - rect.Y) * w + (part.X - rect.X)), part.Width);
                if (pixels.IsEmpty)
                {
                    row.Fill(solid);
                    continue;
                }
                var src = pixels.Span;
                int at = ((y - tileRect.Y) * TileGeometry.Size + (part.X - tileRect.X)) * 4 + 3;
                for (int x = 0; x < row.Length; x++) row[x] = Byte((float)src[at + x * 4]);
            }
        });
        return new JsonObject { ["width"] = w, ["height"] = h, ["enabled"] = mask.IsEnabled, ["data"] = Convert.ToBase64String(bytes) };

        static byte Byte(float v) => float.IsNaN(v) ? (byte)0 : (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
    }

    // MARK: Settings

    private static JsonObject Color(SRGBColor c, double alpha = 1) => new() { ["r"] = c.R, ["g"] = c.G, ["b"] = c.B, ["a"] = alpha };

    private static JsonObject Text(TextLayer t)
    {
        var font = FontResolver.Resolve(t.FontName);
        var o = new JsonObject
        {
            ["text"] = t.String,
            ["fontFamily"] = font.IsExact ? font.Family : SpelledFamily(t.FontName),
            ["fontSize"] = t.Size,
            ["color"] = Color(t.Color),
            ["align"] = RawValues<TextAlignment>.Of(t.Alignment),
            ["bold"] = font.Weight >= 600,
            ["italic"] = font.Italic,
            ["lineHeight"] = t.LineHeight,
            ["pathId"] = t.OnPathID is { } path ? Uuid(path) : null,
        };
        if (t.Warp is { } w)
            o["warp"] = new JsonObject { ["style"] = RawValues<TextWarpStyle>.Of(w.Style), ["bend"] = w.Bend, ["horizontal"] = w.Horizontal, ["vertical"] = w.Vertical };
        return o;
    }

    /// <summary>The family a PostScript name spells ("HelveticaNeue-Bold" → "Helvetica Neue"), for a font that isn't
    /// installed: the family part, without a vendor suffix, its words spaced (the start of FontResolver's own reading).</summary>
    internal static string SpelledFamily(string postScriptName)
    {
        var trimmed = postScriptName.TrimStart('.');
        int dash = trimmed.IndexOf('-');
        string family = dash >= 0 ? trimmed[..dash] : trimmed;
        foreach (var suffix in new[] { "PSMT", "MT", "PS" })
        {
            if (family.Length > suffix.Length && family.EndsWith(suffix, StringComparison.Ordinal))
            {
                family = family[..^suffix.Length];
                break;
            }
        }
        var spaced = new System.Text.StringBuilder(family.Length + 4);
        for (int i = 0; i < family.Length; i++)
        {
            if (i > 0 && char.IsUpper(family[i]) && (char.IsLower(family[i - 1]) || (i + 1 < family.Length && char.IsLower(family[i + 1]) && char.IsUpper(family[i - 1]))))
                spaced.Append(' ');
            spaced.Append(family[i]);
        }
        return spaced.ToString();
    }

    /// <summary>The reverse of AipsImporter's adjustment (Adjustment.FromWebParameters).</summary>
    private static JsonObject Params(Adjustment a)
    {
        var p = new JsonObject();
        foreach (var (k, v) in a.Numbers.OrderBy(e => e.Key, StringComparer.Ordinal)) p[k] = v;
        foreach (var (k, v) in a.Flags.OrderBy(e => e.Key, StringComparer.Ordinal)) p[k] = v;
        switch (a.Kind)
        {
            case AdjustmentKind.ColorBalance:
                foreach (var tone in new[] { "shadows", "midtones", "highlights" })
                {
                    p[tone] = new JsonArray([.. new[] { "R", "G", "B" }.Select(ch => (JsonNode)(a.Numbers.TryGetValue(tone + ch, out var v) ? v : 0))]);
                    foreach (var ch in new[] { "R", "G", "B" }) p.Remove(tone + ch);
                }
                break;
            case AdjustmentKind.PhotoFilter:
                if (a.Colors.TryGetValue("color", out var c)) p["color"] = Color(c);
                break;
            case AdjustmentKind.Curves:
                foreach (var (key, channel) in new[] { ("rgb", CurveChannel.Rgb), ("r", CurveChannel.Red), ("g", CurveChannel.Green), ("b", CurveChannel.Blue) })
                {
                    if (a.Curves.TryGetValue(channel, out var points))
                        p[key] = new JsonArray([.. points.Select(pt => (JsonNode)new JsonObject { ["x"] = pt.X, ["y"] = pt.Y })]);
                }
                break;
            case AdjustmentKind.Levels:
                if (a.Levels.TryGetValue(CurveChannel.Rgb, out var l))
                {
                    p["inBlack"] = l.InBlack;
                    p["inWhite"] = l.InWhite;
                    p["gamma"] = l.Gamma;
                    p["outBlack"] = l.OutBlack;
                    p["outWhite"] = l.OutWhite;
                }
                break;
            case AdjustmentKind.GradientMap:
                p["stops"] = new JsonArray([.. a.Gradient.Select(s => (JsonNode)new JsonObject { ["pos"] = s.Position, ["color"] = Color(s.Color, s.Opacity) })]);
                break;
        }
        return p;
    }

    private static JsonObject? Effects(LayerEffects e)
    {
        var fx = new JsonObject();
        static JsonObject Shadow(ShadowEffect s) => new()
        {
            ["enabled"] = s.IsEnabled, ["color"] = Color(s.Color), ["opacity"] = s.Opacity, ["angle"] = s.Angle,
            ["distance"] = s.Distance, ["size"] = s.Size, ["spread"] = s.Spread,
        };
        if (e.DropShadow is { } drop) fx["dropShadow"] = Shadow(drop);
        if (e.InnerShadow is { } inner) fx["innerShadow"] = Shadow(inner);
        if (e.OuterGlow is { } g)
            fx["outerGlow"] = new JsonObject { ["enabled"] = g.IsEnabled, ["color"] = Color(g.Color), ["opacity"] = g.Opacity, ["size"] = g.Size };
        if (e.Stroke is { } s)
        {
            fx["stroke"] = new JsonObject
            {
                ["enabled"] = s.IsEnabled, ["color"] = Color(s.Color), ["opacity"] = s.Opacity, ["width"] = s.Width,
                ["position"] = RawValues<StrokePosition>.Of(s.Position),
            };
        }
        if (e.ColorOverlay is { } o)
        {
            fx["colorOverlay"] = new JsonObject
            {
                ["enabled"] = o.IsEnabled, ["color"] = Color(o.Color), ["opacity"] = o.Opacity, ["blendMode"] = RawValues<BlendMode>.Of(o.BlendMode.WebMode()),
            };
        }
        return fx.Count == 0 ? null : fx;
    }
}
