// Ported from Packages/GlimmerKit/Tests/GlimmerToolsTests/PaintingTests.swift
using System.Numerics;
using Glimmer.Core;
using Glimmer.Render;

namespace Glimmer.Tools.Tests;

internal static class Painting
{
    public static Vector4 Value(TileGrid grid, int x, int y, float defaultValue = 0)
    {
        if (grid[new TileCoord(PixelRect.FloorDiv(x, 256), PixelRect.FloorDiv(y, 256))] is not { } tile) return new Vector4(defaultValue);
        return tile.PixelAt(((x % 256) + 256) % 256, ((y % 256) + 256) % 256);
    }
}

public class StrokeSamplerTests
{
    [Fact]
    public void EvenSpacingAcrossEvents()
    {
        var settings = new BrushSettings { Size = 10, Spacing = 0.5 };  // 5 px
        var sampler = new StrokeSampler(settings);
        var dabs = sampler.Begin(new(new Vec2(0, 0)));
        dabs.AddRange(sampler.Move(new(new Vec2(7, 0))));
        dabs.AddRange(sampler.Move(new(new Vec2(21, 0))));
        Assert.Equal([0f, 5f, 10f, 15f, 20f], dabs.Select(d => d.Center.X).ToArray());
    }

    [Fact]
    public void PressureScalesSize()
    {
        var sampler = new StrokeSampler(new BrushSettings { Size = 100 });
        var light = sampler.Begin(new(new Vec2(0, 0), Pressure: 0))[0];
        var heavy = sampler.Begin(new(new Vec2(0, 0), Pressure: 1))[0];
        Assert.True(MathF.Abs(light.Radius - 7.5f) < 0.01f && MathF.Abs(heavy.Radius - 50) < 0.01f);
    }

    /// <summary>Jitter and scatter come from the generator the sampler is given, so a stroke can be replayed.</summary>
    [Fact]
    public void JitterAndScatterAreReproducibleWithASeededGenerator()
    {
        var settings = new BrushSettings { Size = 40, SizeJitter = 0.5, Scatter = 0.5 };
        List<DabInstance> Stroke() => new StrokeSampler(settings, new Random(42)).Move(new(new Vec2(200, 0)));
        Assert.Equal(Stroke(), Stroke());
        var dabs = Stroke();
        Assert.Contains(dabs, d => d.Radius < 19.9f);                // some jitter
        Assert.Contains(dabs, d => MathF.Abs(d.Center.Y) > 0.5f);    // some scatter
    }
}

public class StrokeEngineTests
{
    private static List<DabInstance> Dabs(Vec2 from, Vec2 to, double size = 20, double hardness = 1, double flow = 1)
    {
        var sampler = new StrokeSampler(new BrushSettings { Size = size, Hardness = hardness, Flow = flow, PressureSize = false });
        var dabs = sampler.Begin(new(from));
        dabs.AddRange(sampler.Move(new(to)));
        return dabs;
    }

    [Fact]
    public void PaintsWithOpacityCap()
    {
        var engine = new StrokeEngine(TileGrid.Empty, new StrokeEngine.Target.Pixels(), erase: false, new Vector4(1, 0, 0, 1), opacity: 0.5f);
        // Overlapping dabs build up coverage, but opacity caps the result at 50 %.
        engine.Add(Dabs(new Vec2(100, 100), new Vec2(400, 100)));
        engine.Add(Dabs(new Vec2(100, 100), new Vec2(400, 100)));
        engine.Finish();
        var p = Painting.Value(engine.Result, 300, 100);
        Assert.True(MathF.Abs(p.X - 0.5f) < 0.01f && MathF.Abs(p.W - 0.5f) < 0.01f, $"{p}");
        Assert.Equal(0, Painting.Value(engine.Result, 300, 130).W);
        Assert.NotNull(engine.Result[new TileCoord(1, 0)]);  // crossed into the next tile
    }

    [Fact]
    public void FlowBuildsUp()
    {
        var engine = new StrokeEngine(TileGrid.Empty, new StrokeEngine.Target.Pixels(), erase: false, new Vector4(0, 0, 1, 1), opacity: 1);
        engine.Add(Dabs(new Vec2(50, 50), new Vec2(50, 50), flow: 0.2));
        engine.Finish();
        float once = Painting.Value(engine.Result, 50, 50).W;
        engine.Add(Dabs(new Vec2(50, 50), new Vec2(50, 50), flow: 0.2));
        engine.Finish();
        float twice = Painting.Value(engine.Result, 50, 50).W;
        Assert.True(MathF.Abs(once - 0.2f) < 0.01f && MathF.Abs(twice - 0.36f) < 0.01f, $"{once} {twice}");
    }

    [Fact]
    public void EraseAndSelectionLimit()
    {
        var white = TileGrid.Solid(new Vector4(1, 1, 1, 1), new PixelRect(0, 0, 512, 256));
        var selection = Selection.Rectangle(new CGRectLike(0, 0, 200, 256));
        var engine = new StrokeEngine(white, new StrokeEngine.Target.Pixels(), erase: true, Vector4.Zero, opacity: 1, selection: selection);
        engine.Add(Dabs(new Vec2(100, 50), new Vec2(300, 50)));
        engine.Finish();
        Assert.True(Painting.Value(engine.Result, 150, 50).W < 0.01f);  // erased inside the selection
        Assert.True(Painting.Value(engine.Result, 250, 50).W > 0.99f);  // protected outside
    }

    [Fact]
    public void PaintsMasks()
    {
        var engine = new StrokeEngine(TileGrid.Empty, new StrokeEngine.Target.Mask(1), erase: false, Vector4.Zero, opacity: 1, maskTarget: 0);
        engine.Add(Dabs(new Vec2(20, 20), new Vec2(80, 20)));
        engine.Finish();
        Assert.True(Painting.Value(engine.Result, 50, 20, 1).W < 0.01f);  // painted black (hidden)
        Assert.True(Painting.Value(engine.Result, 50, 60, 1).W > 0.99f);  // untouched mask stays white
    }

    /// <summary>A mask tile painted fully black is kept: a missing tile would read as the white default.</summary>
    [Fact]
    public void AMaskTilePaintedBlackIsKept()
    {
        var engine = new StrokeEngine(TileGrid.Empty, new StrokeEngine.Target.Mask(1), erase: false, Vector4.Zero, opacity: 1, maskTarget: 0);
        var cover = Enumerable.Range(0, 64).Select(i => new DabInstance(new Vector2(16 + (i % 8) * 32, 16 + (i / 8) * 32), 40, 1)).ToList();
        engine.Add(cover);
        var tile = engine.Result[new TileCoord(0, 0)];
        Assert.NotNull(tile);
        Assert.Equal(0, Painting.Value(engine.Result, 128, 128, 1).W);
    }

    /// <summary>A retouch stroke blends toward the effect (here a shifted copy, as the clone stamp does).</summary>
    [Fact]
    public void RetouchBlendsTowardTheEffect()
    {
        var grid = TileGrid.Solid(new Vector4(0, 0, 0, 1), new PixelRect(0, 0, 512, 256)).ToBuilder();
        foreach (var (c, t) in TileGrid.Solid(new Vector4(1, 1, 1, 1), new PixelRect(0, 0, 256, 256))) grid[c] = t;
        var split = grid.ToImmutable();
        var effect = new FilterEngine().Shifted(split, (256, 0), new PixelRect(0, 0, 512, 256));
        var engine = new StrokeEngine(split, PixelOps.Mode.TowardEffect, effect, Vector4.Zero, opacity: 1, selection: null);
        engine.Add(Dabs(new Vec2(350, 100), new Vec2(350, 100), size: 30));
        Assert.True(Painting.Value(engine.Result, 350, 100).X > 0.99f);   // cloned white
        Assert.True(Painting.Value(engine.Result, 450, 100).X < 0.01f);   // untouched black
    }
}

public class SelectionToolTests
{
    [Fact]
    public void PolygonLasso()
    {
        var tri = Selection.Polygon([new Vec2(0, 0), new Vec2(100, 0), new Vec2(0, 100)]);
        var v = tri.Values(new PixelRect(0, 0, 100, 100));
        Assert.Equal(1, v[10 * 100 + 10]);
        Assert.Equal(0, v[90 * 100 + 90]);
    }

    [Fact]
    public void FloodFillContiguousAndGlobal()
    {
        // 4×3: two separate red regions split by a blue column.
        float[] r = [255, 0, 0, 255], b = [0, 0, 255, 255];
        float[] row = [.. r, .. r, .. b, .. r];
        float[] px = [.. row, .. row, .. row];
        var contiguous = FloodFill.Mask(px, 4, 3, (0, 0), tolerance: 10, contiguous: true);
        Assert.Equal([1f, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0], contiguous);
        var global = FloodFill.Mask(px, 4, 3, (0, 0), tolerance: 10, contiguous: false);
        Assert.Equal([1f, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1], global);
    }

    [Fact]
    public void GrowShrinkFeather()
    {
        var sel = Selection.Rectangle(new CGRectLike(100, 100, 200, 200));
        var limit = new PixelRect(0, 0, 512, 512);
        var grown = SelectionOps.Apply(new SelectionOps.Operation.Grow(20), sel, limit);
        Assert.True(grown.Values(new PixelRect(85, 150, 1, 1))[0] > 0.99f);
        Assert.True(grown.Values(new PixelRect(75, 150, 1, 1))[0] < 0.01f);
        var shrunk = SelectionOps.Apply(new SelectionOps.Operation.Shrink(20), sel, limit);
        Assert.True(shrunk.Values(new PixelRect(110, 150, 1, 1))[0] < 0.01f);
        Assert.True(shrunk.Values(new PixelRect(130, 150, 1, 1))[0] > 0.99f);
        var feathered = SelectionOps.Apply(new SelectionOps.Operation.Feather(20), sel, limit);
        float edge = feathered.Values(new PixelRect(100, 150, 1, 1))[0];
        Assert.True(edge > 0.3f && edge < 0.7f, $"edge {edge}");
    }

    /// <summary>Shrinking a select-all keeps its default: what is outside the processed area stays selected.</summary>
    [Fact]
    public void ShrinkingSelectAllPullsInFromTheDocumentEdge()
    {
        var limit = new PixelRect(0, 0, 512, 512);
        var shrunk = SelectionOps.Apply(new SelectionOps.Operation.Shrink(10), Selection.All(), limit);
        Assert.Equal(1, shrunk.DefaultValue);
        Assert.True(shrunk.Values(new PixelRect(256, 256, 1, 1))[0] > 0.99f);
    }

    [Fact]
    public void KeepSelectedAndClear()
    {
        var ops = new PixelOps();
        var white = TileGrid.Solid(new Vector4(1, 1, 1, 1), new PixelRect(0, 0, 256, 256));
        var sel = Selection.Rectangle(new CGRectLike(0, 0, 128, 256));
        var kept = ops.Run(PixelOps.Mode.KeepSelected, white, sel, [new TileCoord(0, 0)]);
        Assert.True(Painting.Value(kept, 50, 50).W > 0.99f && Painting.Value(kept, 200, 50).W < 0.01f);
        var cleared = ops.Run(PixelOps.Mode.Erase, white, sel, [new TileCoord(0, 0)]);
        Assert.True(Painting.Value(cleared, 50, 50).W < 0.01f && Painting.Value(cleared, 200, 50).W > 0.99f);
    }

    /// <summary>Paint bucket: a flood-fill mask turned into coverage tiles paints only the filled pixels.</summary>
    [Fact]
    public void FillThroughCoverageFromASelection()
    {
        var ops = new PixelOps();
        var fill = Selection.Rectangle(new CGRectLike(10, 10, 60, 60));
        TileCoord[] coords = [new(0, 0)];
        var painted = ops.Run(PixelOps.Mode.Paint, TileGrid.Empty, null, coords, coverage: ops.Coverage(fill, coords), fullCoverage: false,
                              color: new Vector4(0, 1, 0, 1));
        Assert.True(Painting.Value(painted, 30, 30).Y > 0.99f);
        Assert.Equal(0, Painting.Value(painted, 100, 100).W);
    }
}

public class PenDynamicsTests
{
    [Fact]
    public void TiltAndRotationShapeTheDab()
    {
        var settings = new BrushSettings { Angle = 10, Roundness = 1 };
        var flat = new StrokeSampler.Sample(new Vec2(0, 0), Tilt: new Vec2(0, 0.8), Rotation: 30);
        // Off by default: the brush's own angle and roundness.
        Assert.Equal((10.0, 1.0), StrokeSampler.Shape(settings, flat));
        settings = settings with { TiltAngle = true, TiltRoundness = true };
        var (angle, roundness) = StrokeSampler.Shape(settings, flat);
        Assert.True(Math.Abs(angle - 90) < 1e-9);
        Assert.True(Math.Abs(roundness - 0.36) < 1e-9);
        settings = settings with { RotationAngle = true };
        Assert.True(Math.Abs(StrokeSampler.Shape(settings, flat).Angle - 120) < 1e-9);
        // An upright pen keeps the brush angle.
        Assert.Equal(10, StrokeSampler.Shape(settings, new StrokeSampler.Sample(new Vec2(0, 0), Rotation: 0)).Angle);
    }

    [Fact]
    public void TiltIsInterpolatedAlongTheStroke()
    {
        var sampler = new StrokeSampler(new BrushSettings { Size = 10, Spacing = 0.5, TiltRoundness = true });
        sampler.Begin(new(new Vec2(0, 0), Tilt: new Vec2(0, 0)));
        var dabs = sampler.Move(new(new Vec2(100, 0), Tilt: new Vec2(1, 0)));
        Assert.True(dabs.Count > 10);
        Assert.True(dabs[0].Roundness > dabs[^1].Roundness);
    }
}
