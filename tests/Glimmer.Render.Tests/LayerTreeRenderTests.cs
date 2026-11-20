// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/LayerTreeRenderTests.swift
using System.Collections.Immutable;
using System.Numerics;
using Glimmer.Core;
using static Glimmer.Render.Tests.TestSupport;

namespace Glimmer.Render.Tests;

internal static class LayerTree
{
    public static Layer Gray(float v, int size = 512) => SolidLayer(new Vector4(v, v, v, 1), size: size);

    public static DocumentState Doc(IEnumerable<Layer> layers, int size = 512) => new(size, size, layers: layers);

    public static Layer Group(string name, IEnumerable<Layer> children, double opacity = 1, BlendMode mode = BlendMode.PassThrough) =>
        new(name, new GroupContent(new LayerGroup(children: children)), opacity: opacity, blendMode: mode);
}

public class AdjustmentRenderTests
{
    [Fact]
    public void InvertMatchesWebFormula()
    {
        var adj = new Adjustment(AdjustmentKind.Invert) { Numbers = ImmutableDictionary<string, double>.Empty };
        var state = LayerTree.Doc([LayerTree.Gray(0.5f), new Layer("inv", new AdjustmentContent(adj))]);
        var got = RenderPixel(state, 10, 10);
        float want = SrgbDecode(1 - SrgbEncode(0.5f));
        Assert.True(MathF.Abs(got.X - want) < 0.003f && MathF.Abs(got.W - 1) < 0.001f);
    }

    [Fact]
    public void BlendModeAndOpacityAreHonoured()
    {
        // Invert, blended with Multiply (the web app ignored adjustment blend modes).
        float inverted = SrgbDecode(1 - SrgbEncode(0.5f));
        var multiply = LayerTree.Doc([LayerTree.Gray(0.5f), new Layer("inv", new AdjustmentContent(new Adjustment(AdjustmentKind.Invert)), blendMode: BlendMode.Multiply)]);
        Assert.True(MathF.Abs(RenderPixel(multiply, 0, 0).X - 0.5f * inverted) < 0.003f);

        var half = LayerTree.Doc([LayerTree.Gray(0.5f), new Layer("inv", new AdjustmentContent(new Adjustment(AdjustmentKind.Invert)), opacity: 0.5)]);
        Assert.True(MathF.Abs(RenderPixel(half, 0, 0).X - (0.5f + inverted) / 2) < 0.003f);
    }

    /// <summary>
    /// Found with a file written by Photoshop (2026-11-16): a mask at 90 % along the edge of an Invert layer. In a
    /// document that blends in gamma, opacity and masks mix encoded values.
    /// </summary>
    [Fact]
    public void OpacityMixesInTheDocumentsBlendSpace()
    {
        var state = LayerTree.Doc([LayerTree.Gray(0.02f), new Layer("inv", new AdjustmentContent(new Adjustment(AdjustmentKind.Invert)), opacity: 0.5)])
            with { BlendSpace = BlendSpace.Gamma };
        float b = SrgbEncode(0.02f);
        Assert.True(MathF.Abs(RenderPixel(state, 0, 0).X - SrgbDecode((b + (1 - b)) / 2)) < 0.003f);
        // In linear light, as before: halfway between the two linear values (a much lighter grey).
        state = state with { BlendSpace = BlendSpace.Linear };
        Assert.True(MathF.Abs(RenderPixel(state, 0, 0).X - (0.02f + SrgbDecode(1 - b)) / 2) < 0.003f);
    }

    [Fact]
    public void MaskLimitsTheAdjustment()
    {
        var mask = LayerMask.RevealAll with { Tiles = Grid((new TileCoord(0, 0), Tile.Solid(Vector4.Zero))) };  // hide in the first tile
        var state = LayerTree.Doc([LayerTree.Gray(0.5f), new Layer("inv", new AdjustmentContent(new Adjustment(AdjustmentKind.Invert)), mask: mask)]);
        Assert.True(MathF.Abs(RenderPixel(state, 10, 10).X - 0.5f) < 0.002f);   // masked out
        Assert.True(RenderPixel(state, 300, 10).X < 0.1f);                      // inverted
    }

    [Fact]
    public void IdentityCurvesAndLevelsChangeNothing()
    {
        foreach (var kind in new[] { AdjustmentKind.Curves, AdjustmentKind.Levels, AdjustmentKind.BrightnessContrast, AdjustmentKind.HueSaturation, AdjustmentKind.Exposure })
        {
            var state = LayerTree.Doc([SolidLayer(new Vector4(0.3f, 0.1f, 0.6f, 1), size: 512), new Layer("a", new AdjustmentContent(new Adjustment(kind)))]);
            Assert.True(Close(RenderPixel(state, 5, 5), new Vector4(0.3f, 0.1f, 0.6f, 1)), kind.ToString());
        }
    }

    [Fact]
    public void CurvesBrighten()
    {
        var adj = new Adjustment(AdjustmentKind.Curves);
        adj = adj with { Curves = adj.Curves.SetItem(CurveChannel.Rgb, [new CurvePoint(0, 0), new CurvePoint(0.5, 0.75), new CurvePoint(1, 1)]) };
        var state = LayerTree.Doc([LayerTree.Gray(SrgbDecode(0.5f)), new Layer("c", new AdjustmentContent(adj))]);
        var got = RenderPixel(state, 0, 0);
        Assert.True(MathF.Abs(SrgbEncode(got.X) - 0.75f) < 0.01f);
    }

    [Fact]
    public void GradientMapUsesLuminance()
    {
        var adj = new Adjustment(AdjustmentKind.GradientMap) { Gradient = [new GradientStop(0, new SRGBColor(1, 0, 0)), new GradientStop(1, new SRGBColor(0, 0, 1))] };
        var state = LayerTree.Doc([LayerTree.Gray(1), new Layer("g", new AdjustmentContent(adj))]);
        var got = RenderPixel(state, 0, 0);
        Assert.True(got.Z > 0.99f && got.X < 0.01f);
    }

    /// <summary>Editing a curve point changes the rendered tile (the cache keys on every point, not just the curve count).</summary>
    [Fact]
    public void MovingACurvePointReRendersTheTile()
    {
        var compositor = new DocumentCompositor();
        Adjustment Curve(double y) => new Adjustment(AdjustmentKind.Curves) with
        {
            Curves = ImmutableDictionary<CurveChannel, ImmutableArray<CurvePoint>>.Empty.SetItem(CurveChannel.Rgb, [new CurvePoint(0, 0), new CurvePoint(0.5, y), new CurvePoint(1, 1)]),
        };
        DocumentState State(double y) => LayerTree.Doc([LayerTree.Gray(0.2f, 256), new Layer("c", new AdjustmentContent(Curve(y)), id: Guid.Empty)], size: 256);
        compositor.Update(State(0.6), null, [new TileCoord(0, 0)]);
        float first = compositor.Texture(new TileCoord(0, 0))!.Read(5, 5).X;
        compositor.Update(State(0.9), null, [new TileCoord(0, 0)]);
        float second = compositor.Texture(new TileCoord(0, 0))!.Read(5, 5).X;
        Assert.True(second > first + 0.05f, $"{first} → {second}");
    }
}

public class GroupRenderTests
{
    private static readonly Vector4 Red = new(0.8f, 0.2f, 0.2f, 1);

    [Fact]
    public void IsolatedGroupHidesChildBlendModes()
    {
        var child = SolidLayer(Red, size: 512, mode: BlendMode.Multiply);
        var group = LayerTree.Group("g", [child], mode: BlendMode.Normal);
        // Multiply over the group's transparent backdrop is the child itself.
        Assert.True(Close(RenderPixel(LayerTree.Doc([LayerTree.Gray(0.5f), group]), 1, 1), Red));
    }

    [Fact]
    public void PassThroughGroupBlendsWithBackdrop()
    {
        var child = SolidLayer(Red, size: 512, mode: BlendMode.Multiply);
        var group = LayerTree.Group("g", [child], mode: BlendMode.PassThrough);
        Assert.True(Close(RenderPixel(LayerTree.Doc([LayerTree.Gray(0.5f), group]), 1, 1), new Vector4(0.4f, 0.1f, 0.1f, 1)));

        var half = group with { Opacity = 0.5 };
        Assert.True(Close(RenderPixel(LayerTree.Doc([LayerTree.Gray(0.5f), half]), 1, 1), new Vector4(0.45f, 0.3f, 0.3f, 1)));
    }

    [Fact]
    public void GroupOpacityAndVisibility()
    {
        var group = LayerTree.Group("g", [SolidLayer(new Vector4(1, 1, 1, 1), size: 512)], opacity: 0.5, mode: BlendMode.Normal);
        Assert.True(Close(RenderPixel(LayerTree.Doc([LayerTree.Gray(0), group]), 1, 1), new Vector4(0.5f, 0.5f, 0.5f, 1)));
        var hidden = group with { IsVisible = false };
        Assert.True(Close(RenderPixel(LayerTree.Doc([LayerTree.Gray(0), hidden]), 1, 1), new Vector4(0, 0, 0, 1)));
    }

    [Fact]
    public void NestedGroups()
    {
        var inner = LayerTree.Group("in", [SolidLayer(Red, size: 512)], mode: BlendMode.Normal);
        var outer = LayerTree.Group("out", [inner], opacity: 0.5, mode: BlendMode.Normal);
        Assert.True(Close(RenderPixel(LayerTree.Doc([LayerTree.Gray(0), outer]), 300, 300), new Vector4(0.4f, 0.1f, 0.1f, 1)));
    }
}

public class ClippingRenderTests
{
    /// <summary>Base covers only the first tile; two clipped layers cover everything.</summary>
    private static DocumentState ClippedDoc(double secondOpacity = 1)
    {
        var baseGrid = Grid((new TileCoord(0, 0), Tile.Solid(new Vector4(0, 0, 1, 1))));
        // Fully transparent: must not become the base of the next one.
        var first = SolidLayer(new Vector4(0, 1, 0, 1), size: 512) with { IsClipped = true, Opacity = 0 };
        var second = SolidLayer(new Vector4(1, 0, 0, 1), size: 512, opacity: secondOpacity) with { IsClipped = true };
        return LayerTree.Doc([LayerTree.Gray(0), new Layer("base", new RasterContent(baseGrid)), first, second]);
    }

    [Fact]
    public void ClippedLayersClipToTheChainBase()
    {
        var state = ClippedDoc();
        Assert.True(Close(RenderPixel(state, 10, 10), new Vector4(1, 0, 0, 1)));   // inside base
        Assert.True(Close(RenderPixel(state, 300, 10), new Vector4(0, 0, 0, 1)));  // outside base
    }

    [Fact]
    public void HiddenBaseHidesTheChain()
    {
        var state = ClippedDoc();
        state = state with { Layers = state.Layers.SetItem(1, state.Layers[1] with { IsVisible = false }) };
        Assert.True(Close(RenderPixel(state, 10, 10), new Vector4(0, 0, 0, 1)));
    }

    /// <summary>A fill layer with styles is drawn as effects, fill, effects; the fill stays the base of the clipped layer above.</summary>
    [Fact]
    public void ClippedLayerOverAStyledShapeClipsToTheShape()
    {
        var shape = new Layer("shape", new FillContent(new Fill.Solid(new SRGBColor(0, 0, 1))),
                              mask: Selection.Rectangle(new CGRectLike(100, 100, 200, 200)).AsMask,
                              effects: new LayerEffects { Stroke = new StrokeEffect { Width = 4 } });
        var clipped = SolidLayer(new Vector4(0, 1, 0, 1), size: 512) with { IsClipped = true };
        var state = LayerTree.Doc([LayerTree.Gray(1), shape, clipped]);
        Assert.True(Close(RenderPixel(state, 150, 150), new Vector4(0, 1, 0, 1)));   // inside the shape: the clipped green
        Assert.True(Close(RenderPixel(state, 50, 50), new Vector4(1, 1, 1, 1)));     // outside: the white below
    }
}

public class MaskRenderTests
{
    [Fact]
    public void HideAllAndDensity()
    {
        var layer = SolidLayer(new Vector4(1, 1, 1, 1), size: 512) with { Mask = LayerMask.HideAll };
        Assert.True(Close(RenderPixel(LayerTree.Doc([LayerTree.Gray(0), layer]), 1, 1), new Vector4(0, 0, 0, 1)));
        layer = layer with { Mask = layer.Mask! with { Density = 0.5 } };
        Assert.True(Close(RenderPixel(LayerTree.Doc([LayerTree.Gray(0), layer]), 1, 1), new Vector4(0.5f, 0.5f, 0.5f, 1)));
        layer = layer with { Mask = layer.Mask! with { IsEnabled = false } };
        Assert.True(Close(RenderPixel(LayerTree.Doc([LayerTree.Gray(0), layer]), 1, 1), new Vector4(1, 1, 1, 1)));
    }

    [Fact]
    public void FeatherSoftensTileEdges()
    {
        var mask = LayerMask.RevealAll with { Tiles = Grid((new TileCoord(0, 0), Tile.Solid(Vector4.Zero))), Feather = 8 };
        var layer = SolidLayer(new Vector4(1, 1, 1, 1), size: 512) with { Mask = mask };
        var state = LayerTree.Doc([LayerTree.Gray(0), layer]);
        float edge = RenderPixel(state, 256, 100).X;
        Assert.True(edge > 0.3f && edge < 0.7f, $"edge {edge}");
        Assert.True(RenderPixel(state, 200, 100).X < 0.01f);
        Assert.True(RenderPixel(state, 320, 100).X > 0.99f);
    }

    [Fact]
    public void MovingALayerMovesItsMask()
    {
        var mask = LayerMask.HideAll with { Tiles = Grid((new TileCoord(0, 0), Tile.Solid(Vector4.One))) };  // reveal only tile 0
        var layer = SolidLayer(new Vector4(1, 1, 1, 1), size: 512) with { Mask = mask };
        var moved = TileOperations.Translate(layer, (256, 0));
        var state = LayerTree.Doc([LayerTree.Gray(0), moved], size: 1024);
        Assert.True(RenderPixel(state, 300, 10).X > 0.99f);
        Assert.True(RenderPixel(state, 10, 10).X < 0.01f);
    }

    /// <summary>Painting a mask (an override) shows before it is committed.</summary>
    [Fact]
    public void MaskOverrideShowsWhilePainting()
    {
        var layer = SolidLayer(new Vector4(1, 1, 1, 1), size: 512) with { Mask = LayerMask.RevealAll };
        var state = LayerTree.Doc([LayerTree.Gray(0), layer]);
        var painted = new LayerOverrides(masks: ImmutableDictionary<Guid, TileGrid>.Empty.Add(layer.Id, Grid((new TileCoord(0, 0), Tile.Solid(Vector4.Zero)))));
        Assert.True(RenderPixel(state, 10, 10, painted).X < 0.01f);
        Assert.True(RenderPixel(state, 10, 10).X > 0.99f);
    }
}

public class FillAndSmartRenderTests
{
    [Fact]
    public void SolidAndPatternFills()
    {
        var fill = new Layer("f", new FillContent(new Fill.Solid(new SRGBColor(1, 0, 0))));
        Assert.True(Close(RenderPixel(LayerTree.Doc([fill]), 400, 400), new Vector4(1, 0, 0, 1)));

        var pattern = new Layer("p", new FillContent(new Fill.Pattern(new PatternFill
        {
            Kind = PatternKind.Checkerboard, Scale = 10, Foreground = SRGBColor.Black, Background = SRGBColor.White,
        })));
        var a = RenderPixel(LayerTree.Doc([pattern]), 5, 5);
        var b = RenderPixel(LayerTree.Doc([pattern]), 15, 5);
        Assert.NotEqual(a.X, b.X);
    }

    [Fact]
    public void LinearGradientFill()
    {
        var g = new GradientFill(new Vec2(0, 0), new Vec2(256, 0));
        var state = LayerTree.Doc([new Layer("g", new FillContent(new Fill.Gradient(g)))]);
        // Interpolated in sRGB (like Photoshop): halfway is sRGB 0.5.
        var mid = RenderPixel(state, 128, 0);
        Assert.True(MathF.Abs(SrgbEncode(mid.X) - 0.5f) < 0.01f);
        Assert.True(RenderPixel(state, 400, 0).X > 0.99f);
    }

    [Fact]
    public void SmartObjectTransform()
    {
        var source = Grid((new TileCoord(0, 0), TextureTile(new Vector4(0, 1, 0, 1))));
        var smart = new SmartObject(source, 256, 256, AffineTransform.Make(100, 100, 0.5, 0.5, 0, new Vec2(256, 256)));
        var state = LayerTree.Doc([new Layer("s", new SmartContent(smart))]);
        Assert.True(RenderPixel(state, 160, 160).Y > 0.95f);
        Assert.True(RenderPixel(state, 90, 90).W < 0.01f);
        Assert.True(RenderPixel(state, 240, 240).W < 0.01f);
    }

    [Fact]
    public void AffineMakeMatchesWebSmartTransform()
    {
        var t = AffineTransform.Make(10, 20, 2, 1, Math.PI / 2, new Vec2(100, 50));
        // Box 200×50 at (10,20); centre (110, 45). Rotating 90° CW maps the source origin to the top-right.
        var o = t.Apply(new Vec2(0, 0));
        Assert.True(Math.Abs(o.X - 135) < 1e-9 && Math.Abs(o.Y - (-55)) < 1e-9);
        var back = t.Inverse.Apply(o);
        Assert.True(Math.Abs(back.X) < 1e-9 && Math.Abs(back.Y) < 1e-9);
    }

    /// <summary>A smart filter runs on the transformed pixels, in document space.</summary>
    [Fact]
    public void SmartFiltersRunAfterTheTransform()
    {
        var source = Grid((new TileCoord(0, 0), Tile.Solid(new Vector4(0, 1, 0, 1))));
        var placed = AffineTransform.Make(100, 100, 0.5, 0.5, 0, new Vec2(256, 256));
        var sharp = new SmartObject(source, 256, 256, placed);
        var blurred = sharp with { Filters = [new SmartFilter(new FilterSettings(FilterKind.GaussianBlur, new Dictionary<string, double> { ["radius"] = 8 }))] };
        float Edge(SmartObject smart) => RenderPixel(LayerTree.Doc([new Layer("s", new SmartContent(smart))]), 99, 160).W;
        Assert.True(Edge(sharp) < 0.01f);
        float soft = Edge(blurred);
        Assert.True(soft > 0.2f && soft < 0.8f, $"blurred edge alpha {soft}");
    }
}

public class LayerStyleRenderTests
{
    /// <summary>A 100×100 opaque square at (100, 100) on a white background.</summary>
    private static DocumentState SquareLayer(LayerEffects effects) =>
        LayerTree.Doc([LayerTree.Gray(1), new Layer("sq", new RasterContent(SquareGrid()), effects: effects)]);

    private static TileGrid SquareGrid()
    {
        const int s = TileGeometry.Size;
        var px = new Half[TileGeometry.FloatCount];
        for (int y = 100; y < 200; y++)
        {
            for (int x = 100; x < 200; x++)
            {
                int i = (y * s + x) * 4;
                px[i] = (Half)0; px[i + 1] = (Half)0; px[i + 2] = (Half)1; px[i + 3] = (Half)1;
            }
        }
        return Grid((new TileCoord(0, 0), Tile.Make(px)!));
    }

    [Fact]
    public void DropShadowFallsBelowAndOffset()
    {
        // Light from the top-left → shadow to the bottom-right.
        var effects = new LayerEffects { DropShadow = new ShadowEffect { Angle = 135, Distance = 20, Size = 0, Opacity = 1 } };
        var state = SquareLayer(effects);
        // Bottom-right, outside the square but inside the shadow: dark.
        Assert.True(RenderPixel(state, 210, 210).X < 0.05f);
        // Top-left outside the square: white.
        Assert.True(RenderPixel(state, 95, 95).X > 0.99f);
        // The square itself stays blue on top.
        Assert.True(RenderPixel(state, 150, 150).Z > 0.99f);
    }

    [Fact]
    public void OutsideStroke()
    {
        var effects = new LayerEffects { Stroke = new StrokeEffect { Width = 6, Color = new SRGBColor(1, 0, 0) } };
        var state = SquareLayer(effects);
        var ring = RenderPixel(state, 203, 150);
        Assert.True(ring.X > 0.95f && ring.Z < 0.05f);
        Assert.True(RenderPixel(state, 215, 150).Y > 0.99f);  // beyond the stroke: white
        Assert.True(RenderPixel(state, 150, 150).Z > 0.99f);  // inside: blue
    }

    [Fact]
    public void ColorOverlayReplacesColour()
    {
        var effects = new LayerEffects { ColorOverlay = new ColorOverlayEffect { Color = new SRGBColor(0, 1, 0) } };
        var px = RenderPixel(SquareLayer(effects), 150, 150);
        Assert.True(px.Y > 0.99f && px.Z < 0.01f);
    }

    [Fact]
    public void ColorOverlayRecoloursFillAndShapeLayers()
    {
        var effects = new LayerEffects { ColorOverlay = new ColorOverlayEffect { Color = new SRGBColor(0, 1, 0) } };
        // A shape: a blue fill masked to a square.
        var r = new CGRectLike(100, 100, 200, 200);
        var shape = new Layer("shape", new FillContent(new Fill.Solid(new SRGBColor(0, 0, 1))), mask: Selection.Rectangle(r).AsMask, effects: effects);
        var state = LayerTree.Doc([LayerTree.Gray(1), shape]);
        var inside = RenderPixel(state, 150, 150);
        Assert.True(inside.Y > 0.99f && inside.Z < 0.01f, "the shape takes the overlay colour");
        Assert.True(RenderPixel(state, 50, 50).Z > 0.99f, "outside the shape nothing changes");
        // Half opacity mixes the two.
        var half = effects with { ColorOverlay = effects.ColorOverlay! with { Opacity = 0.5 } };
        var soft = shape with { Effects = half };
        var mixed = RenderPixel(LayerTree.Doc([LayerTree.Gray(1), soft]), 150, 150);
        Assert.True(mixed.Y > 0.2f && mixed.Z > 0.2f && mixed.X < 0.05f);
    }

    /// <summary>A shape (masked fill) gets its styles from the mask's outline.</summary>
    [Fact]
    public void ShapeLayersGetStylesFromTheirOutline()
    {
        var shape = new Layer("shape", new FillContent(new Fill.Solid(new SRGBColor(0, 0, 1))),
                              mask: Selection.Rectangle(new CGRectLike(100, 100, 200, 200)).AsMask,
                              effects: new LayerEffects { Stroke = new StrokeEffect { Width = 6, Color = new SRGBColor(1, 0, 0) } });
        var state = LayerTree.Doc([LayerTree.Gray(1), shape]);
        var ring = RenderPixel(state, 203, 150);
        Assert.True(ring.X > 0.95f && ring.Z < 0.05f, $"stroke {ring}");
        Assert.True(RenderPixel(state, 150, 150).Z > 0.99f);
        Assert.True(RenderPixel(state, 215, 150).Y > 0.99f);
    }
}
