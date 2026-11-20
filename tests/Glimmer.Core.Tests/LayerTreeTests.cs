// Ported from Packages/GlimmerKit/Tests/GlimmerCoreTests/LayerTreeTests.swift
//
// Text is laid out with Skia and HarfBuzz here and with Core Text on the Mac, and the fonts differ (a Mac name such as
// "HelveticaNeue" resolves to an installed stand-in), so the text tests check shapes and relations with margins, not
// the Mac's exact pixel counts. TextWarpTests.aipsTextWarpImports tests AipsImporter.swift and moves with that file.

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Glimmer.Core.Tests.TestTiles;

namespace Glimmer.Core.Tests;

public class LayerTreeTests
{
    private static Layer Raster(string name) => new(name, new RasterContent(TileGrid.Empty));

    /// <summary>root: [A, G[B, C], D]</summary>
    private static (DocumentState State, Guid A, Guid G, Guid B, Guid C, Guid D) Sample()
    {
        Layer a = Raster("A"), b = Raster("B"), c = Raster("C"), d = Raster("D");
        var g = new Layer("G", new GroupContent(new LayerGroup([b, c])), blendMode: BlendMode.PassThrough);
        return (new DocumentState(10, 10, layers: [a, g, d]), a.Id, g.Id, b.Id, c.Id, d.Id);
    }

    [Fact]
    public void PathsAndLookup()
    {
        var (s, a, g, b, c, _) = Sample();
        Assert.Equal([0], s.PathOf(a)!);
        Assert.Equal([1, 1], s.PathOf(c)!);
        Assert.Equal("B", s.LayerById(b)?.Name);
        Assert.Equal(5, s.AllLayers.Length);
        Assert.Equal(2, s.LayerById(g)?.Children.Length);
    }

    [Fact]
    public void InsertAndDeleteInsideGroups()
    {
        var (s, _, _, b, _, _) = Sample();
        s = s with { ActiveLayerID = b };
        var e = Raster("E");
        s = s.InsertLayerAboveActive(e);
        Assert.Equal([1, 1], s.PathOf(e.Id)!);
        s = s.DeleteActiveLayer();
        Assert.Null(s.PathOf(e.Id));
        Assert.Equal(b, s.ActiveLayerID);
    }

    [Fact]
    public void GroupAndUngroup()
    {
        var (s, a, _, _, _, _) = Sample();
        s = s with { ActiveLayerID = a };
        (s, var newGroup) = s.GroupActiveLayer("Group 1");
        Assert.Equal([a], s.LayerById(newGroup)?.Children.Select(l => l.Id));
        Assert.Equal([0, 0], s.PathOf(a)!);
        s = s.UngroupActiveLayer();
        Assert.Equal([0], s.PathOf(a)!);
        Assert.Null(s.LayerById(newGroup));
    }

    [Fact]
    public void MoveAcrossGroups()
    {
        var (s, a, g, b, _, d) = Sample();
        s = s.MoveLayerAbove(d, b);
        Assert.Equal([1, 1], s.PathOf(d)!);
        s = s.MoveLayerIntoTopOf(a, g);
        Assert.Equal(a, s.LayerById(g)?.Children[^1].Id);
        s = s.MoveLayerAbove(b, null);
        Assert.Equal([0], s.PathOf(b)!);
        // A group can't be moved into itself.
        s = s.MoveLayerIntoTopOf(g, g);
        Assert.NotNull(s.PathOf(g));
    }

    [Fact]
    public void DuplicateGroupGetsFreshIDs()
    {
        var (s, _, g, b, _, _) = Sample();
        s = s with { ActiveLayerID = g };
        (s, var copy) = s.DuplicateActiveLayer();
        Assert.Equal("G copy", copy?.Name);
        var ids = s.AllLayers.Select(l => l.Id).ToHashSet();
        Assert.Equal(s.AllLayers.Length, ids.Count);
        Assert.DoesNotContain(copy!.Children, l => l.Id == b);
    }

    [Fact]
    public void ClipBase()
    {
        var clipped = Raster("clip") with { IsClipped = true };
        var clipped2 = Raster("clip2") with { IsClipped = true };
        var @base = Raster("base");
        var s = new DocumentState(1, 1, layers: [@base, clipped, clipped2]);
        Assert.Equal(@base.Id, s.ClipBase([2])?.Id);
    }
}

public class AdjustmentModelTests
{
    [Fact]
    public void MonotoneCurve()
    {
        Assert.Equal(0.3, CurveMath.Evaluate(CurveMath.Identity, 0.3));
        CurvePoint[] pts = [new(0, 0), new(0.25, 0.5), new(0.75, 0.6), new(1, 1)];
        double last = -1;
        for (int i = 0; i <= 100; i++)
        {
            double v = CurveMath.Evaluate(pts, i / 100.0);
            Assert.True(v >= last - 1e-12, $"not monotone at {i}");
            last = v;
        }
        Assert.True(Math.Abs(CurveMath.Evaluate(pts, 0.25) - 0.5) < 1e-9);
    }

    [Fact]
    public void DefaultsComeFromTheSchema()
    {
        var bw = new Adjustment(AdjustmentKind.BlackWhite);
        Assert.Equal(0.4, bw.Number("red"));
        Assert.True(new Adjustment(AdjustmentKind.ColorBalance).Flag("preserveLuminosity"));
        Assert.Equal(1, new Adjustment(AdjustmentKind.ChannelMixer).Number("gg"));
        Assert.Equal(0, new Adjustment(AdjustmentKind.ChannelMixer).Number("gr"));
    }

    [Fact]
    public void LenientDecoding()
    {
        const string json = """{"kind": "exposure", "numbers": {"exposure": 1.5}}""";
        var a = SwiftJson.Deserialize<Adjustment>(json);
        Assert.Equal(1.5, a.Number("exposure"));
        Assert.Equal(1, a.Number("gamma"));
        Assert.True(GradientStop.BlackToWhite.SequenceEqual(a.Gradient));
    }

    [Fact]
    public void ShaderParameterCounts()
    {
        foreach (var kind in AdjustmentKind.AllCases) Assert.True(new Adjustment(kind).ShaderParameters.Length <= 28, $"{kind}");
    }
}

public class LayerTreePackageTests
{
    [Fact]
    public void RoundTripsEveryLayerKind()
    {
        var adj = new Adjustment(AdjustmentKind.Curves);
        adj = adj with { Curves = adj.Curves.SetItem(CurveChannel.Red, [new CurvePoint(0, 0.1), new CurvePoint(1, 0.9)]) };
        var mask = LayerMask.HideAll with { Tiles = TileGrid.Empty.SetItem(new TileCoord(0, 0), GradientTile()), Density = 0.7, Feather = 3 };
        var effects = new LayerEffects { DropShadow = new ShadowEffect(), Stroke = new StrokeEffect() };
        var masked = new Layer("masked", new RasterContent(TileGrid.Empty), isClipped: true) { Mask = mask, Effects = effects };

        var source = TileGrid.Empty.SetItem(new TileCoord(0, 0), GradientTile(0.2f));
        var smart = new Layer("smart", new SmartContent(new SmartObject(source, 200, 100,
            AffineTransform.Make(5, 6, 1.5, 1, 0.3, new Vec2(200, 100)))));
        var fill = new Layer("fill", new FillContent(new Fill.Gradient(new GradientFill(new Vec2(0, 0), new Vec2(10, 20)))));
        var group = new Layer("group", new GroupContent(new LayerGroup([masked, smart], isExpanded: false)), blendMode: BlendMode.PassThrough);
        var state = new DocumentState(300, 200, layers:
        [
            new Layer("adj", new AdjustmentContent(adj), opacity: 0.5, blendMode: BlendMode.Overlay), group, fill,
        ]);

        var read = GlimmerPackage.State(GlimmerPackage.Contents(state));
        Assert.Equal(state.AllLayers.Select(l => l.Id), read.AllLayers.Select(l => l.Id));
        var a = Assert.IsType<AdjustmentContent>(read.Layers[0].Content).Adjustment;
        Assert.Equal(adj, a);
        Assert.Equal(BlendMode.Overlay, read.Layers[0].BlendMode);
        var g = Assert.IsType<GroupContent>(read.Layers[1].Content).Group;
        Assert.True(!g.IsExpanded && read.Layers[1].BlendMode == BlendMode.PassThrough);
        var m = g.Children[0];
        Assert.True(m.IsClipped && m.Effects == effects);
        Assert.True(m.Mask?.DefaultValue == 0 && m.Mask?.Density == 0.7 && m.Mask?.Feather == 3);
        Assert.True(Pixels(m.Mask!.Tiles[new TileCoord(0, 0)]!).AsSpan().SequenceEqual(Pixels(mask.Tiles[new TileCoord(0, 0)]!)));
        var sm = Assert.IsType<SmartContent>(g.Children[1].Content).Smart;
        var orig = ((SmartContent)smart.Content).Smart;
        Assert.True(sm.Transform == orig.Transform && sm.Width == 200);
        Assert.True(Pixels(sm.Source[new TileCoord(0, 0)]!).AsSpan().SequenceEqual(Pixels(source[new TileCoord(0, 0)]!)));
        var f = Assert.IsType<FillContent>(read.Layers[2].Content).Fill;
        Assert.Equal(((FillContent)fill.Content).Fill, f);
    }
}

public class TextAndPathTests
{
    /// <summary>
    /// The Mac's numbers (box at the origin, wider and taller than 80 for "Hello\nGlimmer" at 40 px) hold for any
    /// reasonable font, so they are kept as they are.
    /// </summary>
    [Fact]
    public void TextLayoutAndRender()
    {
        var t = new TextLayer("Hello\nGlimmer", new Vec2(10, 20), size: 40);
        var b = t.Bounds;
        Assert.True(b.X == 10 && b.Y == 20 && b.Width > 80 && b.Height > 80, $"{b}");
        var image = t.Render();
        Assert.Equal(b.Width, image?.Width);
        // Not in the Mac's test: the picture has ink, and its tiles sit where the text is.
        Assert.Contains(image!.Pixels.Where((_, i) => i % 4 == 3), a => a > 128);
        var tiles = t.RenderTiles();
        Assert.False(tiles.IsEmpty);
        Assert.True(new PixelRect(b.X - 256, b.Y - 256, b.Width + 512, b.Height + 512).Contains(tiles.TileBounds!.Value));
    }

    [Fact]
    public void PathFlatteningAndCGPath()
    {
        var p = new VectorPath("p", [new Subpath([new PathAnchor(new Vec2(0, 0)), new PathAnchor(new Vec2(100, 0), handleIn: new Vec2(100, 50)),
                                                  new PathAnchor(new Vec2(100, 100))], isClosed: true)]);
        var poly = p.Polygons(steps: 10)[0];
        Assert.Equal(new Vec2(0, 0), poly[0]);
        Assert.Equal(1 + 10 + 1 + 1, poly.Length);   // curve segment sampled, straight ones not
        Assert.False(p.CGPath.IsEmpty);
    }

    [Fact]
    public void AffineDecomposeRoundTrips()
    {
        var size = new Vec2(200.0, 100.0);
        var t = AffineTransform.Make(30, 40, 1.5, 0.5, 0.4, size);
        var d = t.Decompose(size);
        Assert.True(Math.Abs(d.X - 30) < 1e-9 && Math.Abs(d.Y - 40) < 1e-9 && Math.Abs(d.ScaleX - 1.5) < 1e-9 && Math.Abs(d.ScaleY - 0.5) < 1e-9);
        Assert.True(Math.Abs(d.Rotation - 0.4) < 1e-9);
    }

    [Fact]
    public void TextAndPathsRoundTrip()
    {
        var state = new DocumentState(100, 100, layers: [new Layer("t", new TextContent(new TextLayer("x", new Vec2(1, 2))))])
        {
            Paths = [new VectorPath("Path 1", [new Subpath([new PathAnchor(new Vec2(1, 1))])])],
            Aids = new ViewAids { Guides = [new Guide(GuideAxis.Vertical, 50)], Grid = new GridSettings { IsVisible = true } },
        };
        var read = GlimmerPackage.State(GlimmerPackage.Contents(state));
        var t = Assert.IsType<TextContent>(read.Layers[0].Content).Text;
        Assert.Equal("x", t.String);
        Assert.True(read.Paths.SequenceEqual(state.Paths) && read.Aids == state.Aids);
    }
}

public class ProjectiveTransformTests
{
    private static readonly Vec2 Size = new(200.0, 100.0);
    private static readonly Vec2[] QuadCorners = [new(10.0, 20.0), new(250.0, 0.0), new(230.0, 180.0), new(30.0, 140.0)];

    [Fact]
    public void QuadMapsTheSourceCorners()
    {
        var t = AffineTransform.Quad(Size, QuadCorners);
        Vec2[] src = [new(0.0, 0.0), new(Size.X, 0), Size, new(0, Size.Y)];
        foreach (var (s, q) in src.Zip(QuadCorners)) Assert.True(Vec2.Distance(t.Apply(s), q) < 1e-6);
        Assert.False(t.IsAffine);
        var back = t.Inverse.Apply(QuadCorners[2]);
        Assert.True(Vec2.Distance(back, Size) < 1e-6);
    }

    [Fact]
    public void ParallelogramStaysAffine()
    {
        Vec2[] p = [new(0.0, 0.0), new(200.0, 0.0), new(250.0, 100.0), new(50.0, 100.0)];
        Assert.True(AffineTransform.Quad(Size, p).IsAffine);
    }

    [Fact]
    public void CodableIsBackwardCompatible()
    {
        const string old = """{"a":2,"b":0,"c":0,"d":2,"tx":5,"ty":6}""";
        var t = SwiftJson.Deserialize<AffineTransform>(old);
        Assert.True(t.IsAffine && t.Apply(new Vec2(1, 1)) == new Vec2(7, 8));
        var p = AffineTransform.Quad(Size, QuadCorners);
        var back = SwiftJson.Deserialize<AffineTransform>(SwiftJson.Serialize(p));
        Assert.Equal(p, back);
    }
}

public class TextOnPathTests
{
    [Fact]
    public void TextFollowsTheLine()
    {
        // A diagonal path: the rendered ink must lie near the diagonal, not in a horizontal band.
        var t = new TextLayer("GLIMMER ALONG A PATH", new Vec2(0, 0), size: 30) { OnPath = [new Vec2(50, 50), new Vec2(450, 450)] };
        var b = t.Bounds;
        Assert.True(b.X < 50 && b.MaxX > 450);
        var image = t.Render();
        Assert.NotNull(image);
        int near = 0, far = 0;
        for (int y = 0; y < image!.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (image.Pixels[(y * image.Width + x) * 4 + 3] <= 128) continue;
                double dx = b.X + x, dy = b.Y + y;
                if (Math.Abs(dx - dy) / Math.Sqrt(2.0) < 40) near++;
                else far++;
            }
        }
        Assert.True(near > 500, $"near {near}");
        Assert.True(far < near / 20, $"near {near} far {far}");
    }
}

public class SmartFilterStorageTests
{
    [Fact]
    public void SmartFiltersRoundTrip()
    {
        var smart = new SmartObject(TileGrid.Empty, 10, 10, AffineTransform.Identity,
                                    [new SmartFilter(new FilterSettings(FilterKind.GaussianBlur, [new("radius", 6)])) with { IsEnabled = false }]);
        var state = new DocumentState(10, 10, layers: [new Layer("s", new SmartContent(smart))]);
        var read = GlimmerPackage.State(GlimmerPackage.Contents(state));
        var s = Assert.IsType<SmartContent>(read.Layers[0].Content).Smart;
        Assert.True(smart.Filters.SequenceEqual(s.Filters));   // ImmutableArray's own Equals compares references
    }
}

public class VectorShapeTests
{
    [Fact]
    public void ShapesAndTransforms()
    {
        var r = new CGRectLike(10, 20, 110, 70);
        var e = VectorPath.Ellipse(r);
        var b = e.ControlBounds;
        Assert.NotNull(b);
        Assert.True(Math.Abs(b!.Value.MinX - 10) < 1e-9 && Math.Abs(b.Value.MaxY - 70) < 1e-9);
        var scaled = e.Applying(new AffineTransform(a: 2, d: 2));
        Assert.True(Math.Abs(scaled.ControlBounds!.Value.MaxX - 220) < 1e-9);
        var sel = VectorPath.RoundedRectangle(r, radius: 10).AsSelection;
        float inside = sel.Values(new PixelRect(60, 45, 1, 1))[0];
        float corner = sel.Values(new PixelRect(10, 20, 1, 1))[0];
        Assert.True(inside > 0.99 && corner < 0.1, $"inside {inside} corner {corner}");
    }

    [Fact]
    public void VectorMaskRoundTrips()
    {
        var path = VectorPath.Ellipse(new CGRectLike(0, 0, 50, 40));
        var state = new DocumentState(64, 64, layers:
        [
            new Layer("shape", new FillContent(new Fill.Solid(SRGBColor.Black)), mask: path.AsSelection.AsMask, vectorMask: path),
        ]);
        var read = GlimmerPackage.State(GlimmerPackage.Contents(state));
        Assert.Equal(path, read.Layers[0].VectorMask);
    }
}

public class TextWarpTests
{
    /// <summary>Column-wise ink centre (document y) for an image rendered at bounds.</summary>
    private static Dictionary<int, double> InkCentres(TextLayer t)
    {
        var b = t.Bounds;
        var image = t.Render();
        Assert.NotNull(image);
        var result = new Dictionary<int, double>();
        for (int x = 0; x < image!.Width; x++)
        {
            double sum = 0, n = 0;
            for (int y = 0; y < image.Height; y++)
            {
                if (image.Pixels[(y * image.Width + x) * 4 + 3] <= 128) continue;
                sum += y;
                n++;
            }
            if (n > 0) result[b.X + x] = b.Y + sum / n;
        }
        return result;
    }

    [Fact]
    public void FormulasMatchTheWebApp()
    {
        var arch = new TextWarp(TextWarpStyle.Arch, Bend: 1);
        Assert.Equal(new Vec2(0, 0.5), arch.Displacement(0.5, 0.3));
        Assert.Equal(0, arch.Displacement(0, 0.3).Y);
        var arc = new TextWarp(TextWarpStyle.Arc, Bend: 1);
        Assert.Equal(0, arc.Displacement(0.5, 1).Y);
        Assert.True(Math.Abs(new TextWarp(TextWarpStyle.Rise, Bend: 0.4).Displacement(1, 0).Y - 0.2) < 1e-12);
        var skew = new TextWarp(TextWarpStyle.Wave, Bend: 0, Horizontal: 1);
        Assert.Equal(-0.3, skew.Displacement(0.3, 0).X);
        Assert.True(!new TextWarp(TextWarpStyle.Arc, Bend: 0).IsActive && !new TextWarp(TextWarpStyle.None, Bend: 1).IsActive);
    }

    [Fact]
    public void ArchBendsTheBaseline()
    {
        var t = new TextLayer("WARPED TEXT", new Vec2(100, 100), size: 60);
        var flat = InkCentres(t);
        var flatBounds = t.Bounds;
        t = t with { Warp = new TextWarp(TextWarpStyle.Arch, Bend: 0.6) };
        var b = t.Bounds;
        Assert.True(b.Height > flatBounds.Height + 30 && b.Y < flatBounds.Y);
        var bent = InkCentres(t);
        // Average vertical shift of the ink against the flat text, near the ends and in the middle.
        var xs = flat.Keys.Where(bent.ContainsKey).Order().ToList();
        double Shift(double f)
        {
            int lo = (int)(xs.Count * (f - 0.05)), hi = (int)(xs.Count * (f + 0.05));
            var window = xs.Skip(Math.Max(lo, 0)).Take(Math.Min(hi, xs.Count) - Math.Max(lo, 0)).ToList();
            return window.Select(x => bent[x] - flat[x]).Sum() / window.Count;
        }
        double left = Shift(0.1), mid = Shift(0.5), right = Shift(0.9);
        // A positive arch pushes the middle down (web semantics) much more than the ends.
        Assert.True(mid > 12 && mid > left + 8 && mid > right + 8, $"left {left} mid {mid} right {right}");
    }

    [Fact]
    public void WarpRoundTripsAndOldFilesStillDecode()
    {
        var t = new TextLayer("A", Vec2.Zero) { Warp = new TextWarp(TextWarpStyle.Flag, Bend: -0.25, Vertical: 0.5) };
        var data = SwiftJson.Serialize(t);
        Assert.Equal(t, SwiftJson.Deserialize<TextLayer>(data));
        var legacy = JsonNode.Parse(data)!.AsObject();
        legacy.Remove("warp");
        var old = SwiftJson.Deserialize<TextLayer>(legacy.ToJsonString());
        Assert.Null(old.Warp);
    }
}

public class HistogramTests
{
    [Fact]
    public void CountsVisiblePixelsPerChannel()
    {
        // Two opaque pixels and one fully transparent one.
        var h = new Histogram([255, 0, 0, 255, 0, 0, 255, 255, 9, 9, 9, 0]);
        Assert.True(h.Red[255] == 1 && h.Red[0] == 1 && h.Red[9] == 0);
        Assert.True(h.Blue[255] == 1 && h.Green[0] == 2);
        Assert.True(h.Luminance[54] == 1 && h.Luminance[18] == 1);
        Assert.True(h.Bins(CurveChannel.Rgb) == h.Luminance && h.Bins(CurveChannel.Green) == h.Green);
    }

    /// <summary>The Mac draws a CGImage of white at half opacity; here the same premultiplied pixels are handed over.</summary>
    [Fact]
    public void ImageHistogramUnpremultiplies()
    {
        var px = new byte[4 * 4 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 128; px[i + 1] = 128; px[i + 2] = 128; px[i + 3] = 128; }
        var h = Histogram.FromImage(px, 4, 4);
        Assert.NotNull(h);
        Assert.Equal(16u, h!.Red[255] + h.Red[254]);
        // Scaled down first when bigger than the limit, as on the Mac.
        var big = new byte[1024 * 600 * 4];
        for (int i = 0; i < big.Length; i += 4) { big[i] = 64; big[i + 3] = 128; }
        var scaled = Histogram.FromImage(big, 1024, 600, maxSide: 512);
        Assert.Equal(512u * 300, (uint)scaled!.Red.Sum(v => v));
        Assert.Equal(512u * 300, scaled.Red[128] + scaled.Red[127] + scaled.Red[129]);
    }

    [Fact]
    public void BelowHidesTheLayerAndEverythingAbove()
    {
        var a = new Layer("A", new RasterContent(TileGrid.Empty));
        var b = new Layer("B", new RasterContent(TileGrid.Empty));
        var c = new Layer("C", new RasterContent(TileGrid.Empty));
        var group = new Layer("G", new GroupContent(new LayerGroup([b, c])));
        var d = new Layer("D", new RasterContent(TileGrid.Empty));
        var state = new DocumentState(10, 10, layers: [a, group, d]);
        var below = state.Below(c.Id);
        Assert.True(below.LayerById(a.Id)?.IsVisible == true && below.LayerById(group.Id)?.IsVisible == true);
        Assert.True(below.LayerById(b.Id)?.IsVisible == true);
        Assert.True(below.LayerById(c.Id)?.IsVisible == false && below.LayerById(d.Id)?.IsVisible == false);
    }
}

public class TileAccountingTests
{
    [Fact]
    public void CountsTextureTilesOnly()
    {
        var texture = GradientTile();
        var grid = TileGrid.Solid(new Vector4(1, 0, 0, 1), new PixelRect(0, 0, 512, 256)).SetItem(new TileCoord(0, 0), texture);
        var masked = new Layer("m", new RasterContent(TileGrid.Empty))
        {
            Mask = new LayerMask(TileGrid.Empty.SetItem(new TileCoord(3, 3), GradientTile(0.5f))),
        };
        var state = new DocumentState(512, 256, layers: [new Layer("a", new RasterContent(grid)), masked]);
        Assert.Equal(2, state.TextureTileIDs.Count);
    }
}

public class GraphicsMemoryTests
{
    [Fact]
    public void AShortageIsToldOnceAndSaysWhatToDo()
    {
        _ = GraphicsMemory.TakeShortage();
        Assert.False(GraphicsMemory.TakeShortage());
        GraphicsMemory.NoteShortage();
        GraphicsMemory.NoteShortage();
        Assert.True(GraphicsMemory.TakeShortage());
        Assert.False(GraphicsMemory.TakeShortage());
        Assert.Contains("merge some layers", new GraphicsMemoryException().Message);
    }
}
