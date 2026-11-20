// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/AipsExporterTests.swift (with its ShapeStyleTests and the
// opt-in AipsSampleTests). textKeepsItsSettingsAndPath uses an installed bold font where the Swift uses Helvetica-Bold
// (a Mac font; a computer without it gets a stand-in, which the document then names). The JSON shape and family tests are
// new.
using System.Numerics;
using System.Text.Json;
using Glimmer.Core;
using SkiaSharp;
using static Glimmer.Formats.Tests.ExportedFiles;

namespace Glimmer.Formats.Tests;

/// <summary>A project written for the web app must read back the same.</summary>
public class AipsExporterTests
{
    private static byte[] Flat(DocumentState state)
    {
        using var image = ImageExporter.FlattenedSRGB(state);
        return Read(image).Pixels;
    }

    [Fact]
    public void LayersMasksAndSettingsRoundTrip()
    {
        const int size = 300;
        // A red layer hanging outside the canvas, masked to its left half; a blue one in a group; an adjustment.
        var red = new Layer("Red", new RasterContent(TileGrid.Solid(new Vector4(1, 0, 0, 1), new PixelRect(0, 0, 512, 256))),
                            opacity: 0.8, blendMode: BlendMode.Multiply,
                            mask: Selection.Rectangle(new CGRectLike(0, 0, 150, 300)).AsMask,
                            effects: new LayerEffects { Stroke = new StrokeEffect { Width = 5, Position = StrokePosition.Inside } });
        var blue = SolidLayer(new Vector4(0, 0, 1, 1));
        var curves = new Adjustment(AdjustmentKind.Curves);
        curves = curves with { Curves = curves.Curves.SetItem(CurveChannel.Rgb, [new CurvePoint(0, 0), new CurvePoint(0.5, 0.7), new CurvePoint(1, 1)]) };
        var group = new Layer("Group", new GroupContent(new LayerGroup([blue, new Layer("Curves", new AdjustmentContent(curves))], isExpanded: false)));
        var white = new Layer("White", new RasterContent(TileGrid.Solid(new Vector4(1, 1, 1, 1), new PixelRect(0, 0, 512, 512))));
        var state = new DocumentState(size, size, layers: [white, group, red]) with
        {
            ActiveLayerID = red.Id,
            Paths = [new VectorPath("Curve", [new Subpath([
                new PathAnchor(new Vec2(10, 20), handleOut: new Vec2(40, 20)), new PathAnchor(new Vec2(200, 220), handleIn: new Vec2(150, 220)),
            ], isClosed: false)])],
        };
        state = state with
        {
            Aids = state.Aids with
            {
                Guides = [new Guide(GuideAxis.Vertical, 120), new Guide(GuideAxis.Horizontal, 33)],
                Grid = state.Aids.Grid with { IsVisible = true, Spacing = 50 },
                Snap = false,
            },
        };

        var data = AipsExporter.ExportProject(state);
        var back = AipsImporter.ImportProject(data);

        Assert.True(back.Width == size && back.Height == size);
        Assert.Equal(new[] { "White", "Group", "Red" }, back.Layers.Select(l => l.Name));
        Assert.Equal("Red", back.ActiveLayer?.Name);
        var r = back.Layers[^1];
        Assert.True(r.Opacity == 0.8 && r.BlendMode == BlendMode.Multiply);
        Assert.True(r.Effects.Stroke?.Width == 5 && r.Effects.Stroke?.Position == StrokePosition.Inside);
        Assert.NotNull(r.Mask);
        var g = Assert.IsType<GroupContent>(back.Layers[1].Content).Group;
        Assert.True(!g.IsExpanded && g.Children.Select(c => c.Name).SequenceEqual(["L", "Curves"]));
        var a = Assert.IsType<AdjustmentContent>(g.Children[1].Content).Adjustment;
        Assert.Equal(AdjustmentKind.Curves, a.Kind);
        Assert.Equal(curves.Curves[CurveChannel.Rgb].ToArray(), a.Curves[CurveChannel.Rgb].ToArray());
        Assert.True(back.Paths.Length == 1 && back.Paths[0].Name == "Curve");
        Assert.Equal(state.Paths[0].Subpaths.Length, back.Paths[0].Subpaths.Length);
        Assert.Equal(state.Paths[0].Subpaths[0].IsClosed, back.Paths[0].Subpaths[0].IsClosed);
        Assert.Equal(state.Paths[0].Subpaths[0].Anchors.ToArray(), back.Paths[0].Subpaths[0].Anchors.ToArray());
        Assert.Equal(new[] { 120.0, 33 }, back.Aids.Guides.Select(x => x.Position));
        Assert.Equal(new[] { GuideAxis.Vertical, GuideAxis.Horizontal }, back.Aids.Guides.Select(x => x.Axis));
        Assert.True(back.Aids.Grid.IsVisible && back.Aids.Grid.Spacing == 50 && !back.Aids.Snap);

        // The picture is the same (8-bit PNG round trip: allow one level of rounding).
        var before = Flat(state);
        var after = Flat(back);
        int worst = before.Zip(after, (x, y) => Math.Abs(x - y)).Max();
        Assert.True(worst <= 2, $"largest channel difference {worst}");
    }

    /// <summary>An installed font with a bold member that names itself "…-Bold" (Helvetica-Bold where it is there).</summary>
    private static string? BoldFont()
    {
        var names = FontResolver.InstalledPostScriptNames;
        if (names.Contains("Helvetica-Bold")) return "Helvetica-Bold";
        return names.FirstOrDefault(n => n.EndsWith("-Bold", StringComparison.Ordinal) && FontResolver.Resolve(n).IsExact
                                          && FontResolver.WithBold(n, false) is { } regular && FontResolver.WithBold(regular, true) == n);
    }

    [Fact]
    public void TextKeepsItsSettingsAndPath()
    {
        string font = BoldFont() ?? "Helvetica-Bold";
        var path = new VectorPath("Arc", [new Subpath([new PathAnchor(new Vec2(20, 100)), new PathAnchor(new Vec2(280, 100))])]);
        var text = new TextLayer("Hello", new Vec2(30, 40), fontName: font, size: 40, color: new SRGBColor(0.2, 0.4, 0.6),
                                 alignment: TextAlignment.Center) with { OnPath = [.. path.Polygons().First()], OnPathID = path.Id };
        var state = new DocumentState(300, 200, layers: [new Layer("Hello", new TextContent(text))]) with { Paths = [path] };

        var back = AipsImporter.ImportProject(AipsExporter.ExportProject(state));
        var t = Assert.IsType<TextContent>(back.Layers.First().Content).Text;
        Assert.True(t.String == "Hello" && t.Size == 40 && t.Alignment == TextAlignment.Center);
        if (BoldFont() is not null) Assert.Equal(font, t.FontName);
        else Assert.True(FontResolver.IsBold(t.FontName), t.FontName);
        Assert.True(Math.Abs(t.Color.G - 0.4) < 1e-9);
        Assert.True(Math.Abs(t.Origin.X - 30) <= 1 && Math.Abs(t.Origin.Y - 40) <= 1);
        Assert.Equal(back.Paths.First().Id, t.OnPathID);
        Assert.Equal(back.Paths.First().Polygons().First(), t.OnPath!.Value.ToArray());
    }

    [Fact]
    public void FillLayersGoOutAsPixels()
    {
        var fill = new Layer("Fill", new FillContent(new Fill.Solid(new SRGBColor(0, 1, 0))));
        var state = new DocumentState(64, 64, layers: [fill]);
        var back = AipsImporter.ImportProject(AipsExporter.ExportProject(state));
        Assert.True(back.Layers.First().IsRaster);
        var px = Flat(back);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, px[..4]);
    }

    [Fact]
    public void TheFileHasTheWebAppsShape()
    {
        var hidden = SolidLayer(new Vector4(0, 0, 1, 1), size: 64) with { IsVisible = false, BlendMode = BlendMode.PassThrough };
        var masked = SolidLayer(new Vector4(1, 0, 0, 1), size: 64) with
        {
            // Half revealed by default, nothing stored: written as hidden (the Mac fills only defaults of one half and more).
            Mask = new LayerMask(defaultValue: 0.4f),
        };
        var state = new DocumentState(64, 32, layers: [hidden, masked]);
        using var json = JsonDocument.Parse(AipsExporter.ExportProject(state));
        var root = json.RootElement;
        Assert.Equal("aips", root.GetProperty("magic").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(masked.Id.ToString().ToUpperInvariant(), root.GetProperty("activeLayerId").GetString());
        var nodes = root.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, nodes[0].GetProperty("parentId").ValueKind);
        Assert.Equal("normal", nodes[0].GetProperty("blendMode").GetString());   // no pass-through in the web app
        Assert.False(nodes[0].GetProperty("visible").GetBoolean());
        Assert.StartsWith("data:image/png;base64,", nodes[0].GetProperty("pixels").GetString());
        var mask = nodes[1].GetProperty("mask");
        Assert.Equal((256, 256), (mask.GetProperty("width").GetInt32(), mask.GetProperty("height").GetInt32()));   // the layer's tile box
        Assert.All(Convert.FromBase64String(mask.GetProperty("data").GetString()!), b => Assert.Equal(0, b));
        Assert.Empty(root.GetProperty("paths").EnumerateArray());
    }

    [Fact]
    public void AFontThatIsntInstalledKeepsItsFamily()
    {
        Assert.Equal("Helvetica Neue", AipsExporter.SpelledFamily("HelveticaNeue-Bold"));
        Assert.Equal("Times New Roman", AipsExporter.SpelledFamily("TimesNewRomanPSMT"));
        Assert.Equal("SF Pro Display", AipsExporter.SpelledFamily(".SFProDisplay-Regular"));
        Assert.Equal("Futura", AipsExporter.SpelledFamily("Futura"));
    }

    /// <summary>Writes a project with one of everything to GLIMMER_AIPS_OUT, for opening in the web app by hand.</summary>
    [OptInFact("GLIMMER_AIPS_OUT")]
    public void WritesASample()
    {
        var path = new VectorPath("Arc", [new Subpath([
            new PathAnchor(new Vec2(60, 420), handleOut: new Vec2(200, 300)), new PathAnchor(new Vec2(560, 420), handleIn: new Vec2(420, 300)),
        ])]);
        var onPath = new TextLayer("Along the path", new Vec2(60, 380), fontName: "Helvetica-Bold", size: 36, color: new SRGBColor(1, 1, 1))
            with { OnPath = [.. path.Polygons().First()], OnPathID = path.Id };
        var plain = new TextLayer("Glimmer native", new Vec2(40, 30), fontName: "Helvetica-Bold", size: 48, color: new SRGBColor(1, 0.9, 0.2));
        var red = new Layer("Red, masked left half", new RasterContent(TileGrid.Solid(new Vector4(1, 0, 0, 1), new PixelRect(256, 0, 256, 256))),
                            opacity: 0.9, mask: Selection.Rectangle(new CGRectLike(256, 0, 384, 256)).AsMask,
                            effects: new LayerEffects { DropShadow = new ShadowEffect { Distance = 12 } });
        var smartSource = TileGrid.Solid(new Vector4(0, 0.6f, 0, 1), new PixelRect(0, 0, 256, 256));
        var smart = new Layer("Smart, rotated", new SmartContent(new SmartObject(smartSource, 256, 256,
            AffineTransform.Make(330, 250, 0.6, 0.4, 0.4, new Vec2(256, 256)))));
        var hue = new Adjustment(AdjustmentKind.HueSaturation);
        hue = hue with { Numbers = hue.Numbers.SetItem("hue", 60) };
        var group = new Layer("Group", new GroupContent(new LayerGroup([
            new Layer("Blue", new RasterContent(TileGrid.Solid(new Vector4(0, 0, 1, 1), new PixelRect(0, 256, 256, 256)))),
            new Layer("Hue +60", new AdjustmentContent(hue)),
        ])));
        var sky = new Layer("Grey", new RasterContent(TileGrid.Solid(new Vector4(0.2f, 0.2f, 0.2f, 1), new PixelRect(0, 0, 768, 512))));
        var state = new DocumentState(640, 480, layers:
        [
            sky, group, red, smart, new Layer("Title", new TextContent(plain)), new Layer("On path", new TextContent(onPath)),
        ]) with { Paths = [path] };
        state = state with { Aids = state.Aids with { Guides = [new Guide(GuideAxis.Vertical, 320)], RulersVisible = true } };
        string output = Environment.GetEnvironmentVariable("GLIMMER_AIPS_OUT")!;
        File.WriteAllBytes(output, AipsExporter.ExportProject(state));
        // What the native app shows, to compare with the web app's rendering.
        using var flat = ImageExporter.Flatten(state);
        File.WriteAllBytes(output + ".png", ImageExporter.Encode(flat, new ExportOptions(ExportFormat.Png)));
    }
}

/// <summary>Shapes are fill layers with a mask; their layer styles follow the mask's outline.</summary>
public class ShapeStyleTests
{
    [Fact]
    public void StrokeFollowsAShapesOutline()
    {
        var shape = Selection.Rectangle(new CGRectLike(100, 100, 200, 200));
        var layer = new Layer("Shape", new FillContent(new Fill.Solid(new SRGBColor(0, 0, 1))), mask: shape.AsMask,
                              effects: new LayerEffects { Stroke = new StrokeEffect { Color = new SRGBColor(1, 0, 0), Width = 10, Position = StrokePosition.Outside } });
        var state = new DocumentState(300, 300, layers: [layer]);
        using var image = ImageExporter.FlattenedSRGB(state);
        var px = Read(image).Pixels;
        byte[] At(int x, int y) => px[((y * 300 + x) * 4)..((y * 300 + x) * 4 + 4)];
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, At(150, 150));   // the fill
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(95, 150));    // the stroke, 5 px outside the left edge
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(150, 205));   // the stroke, below the bottom edge
        Assert.Equal(0, At(80, 150)[3]);                             // nothing beyond the stroke
    }
}
