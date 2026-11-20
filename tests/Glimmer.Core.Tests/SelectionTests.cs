// Ported from Packages/GlimmerKit/Tests/GlimmerCoreTests/SelectionTests.swift

namespace Glimmer.Core.Tests;

public class SelectionTests
{
    [Fact]
    public void RectangleIsExactWithAntialiasedEdges()
    {
        var s = Selection.Rectangle(new CGRectLike(10, 10, 20.5, 20));
        var v = s.Values(new PixelRect(0, 0, 30, 30));
        Assert.Equal(1, v[15 * 30 + 15]);
        Assert.Equal(0.5, v[15 * 30 + 20]);
        Assert.Equal(0, v[5 * 30 + 5]);
        Assert.Equal(new PixelRect(10, 10, 11, 10), s.Bounds(new PixelRect(0, 0, 100, 100)));
    }

    [Fact]
    public void Ellipse()
    {
        var s = Selection.Ellipse(new CGRectLike(0, 0, 100, 50));
        var v = s.Values(new PixelRect(0, 0, 100, 50));
        Assert.Equal(1, v[25 * 100 + 50]);
        Assert.Equal(0, v[0]);
    }

    [Fact]
    public void CombineAndInvert()
    {
        var a = Selection.Rectangle(new CGRectLike(0, 0, 300, 10));
        var b = Selection.Rectangle(new CGRectLike(100, 0, 400, 10));
        var r = new PixelRect(0, 0, 400, 10);
        float At(Selection s, int x) => s.Values(r)[5 * 400 + x];
        var add = a.Combined(b, Selection.Operation.Add);
        var sub = a.Combined(b, Selection.Operation.Subtract);
        var both = a.Combined(b, Selection.Operation.Intersect);
        Assert.True(At(add, 50) == 1 && At(add, 350) == 1);
        Assert.True(At(sub, 50) == 1 && At(sub, 150) == 0);
        Assert.True(At(both, 50) == 0 && At(both, 150) == 1);
        var inv = a.Inverted();
        Assert.True(At(inv, 50) == 0 && At(inv, 350) == 1);
        Assert.Equal(r, inv.Bounds(r));
        Assert.Null(new Selection().Bounds(r));
    }

    /// <summary>
    /// Lasso polygons: even-odd inside one polygon, overlapping polygons add up, edges anti-aliased. Not in the Mac's
    /// tests (the Mac rasterizes with Core Graphics, this port with Skia).
    /// </summary>
    [Fact]
    public void PolygonsAreEvenOddInsideAndAddUpAcross()
    {
        // A square with a square hole drawn as one self-overlapping polygon path: the hole stays out (even-odd).
        Vec2[] ring = [new(0, 0), new(100, 0), new(100, 100), new(0, 100), new(0, 0), new(30, 30), new(30, 70), new(70, 70), new(70, 30), new(30, 30)];
        var s = Selection.Polygon(ring);
        Assert.True(s.Values(new PixelRect(10, 50, 1, 1))[0] > 0.99);
        Assert.True(s.Values(new PixelRect(50, 50, 1, 1))[0] < 0.01);
        // Two overlapping triangles: both areas are selected, the overlap too.
        var two = Selection.Polygons([[new(0, 0), new(60, 0), new(0, 60)], [new(20, 20), new(80, 20), new(20, 80)]]);
        Assert.True(two.Values(new PixelRect(5, 5, 1, 1))[0] > 0.99);
        Assert.True(two.Values(new PixelRect(25, 25, 1, 1))[0] > 0.99);
        Assert.True(two.Values(new PixelRect(30, 40, 1, 1))[0] > 0.99);
        // A half-pixel edge is about half covered.
        var half = Selection.Polygon([new(0, 0), new(10.5, 0), new(10.5, 10), new(0, 10)]);
        Assert.InRange(half.Values(new PixelRect(10, 5, 1, 1))[0], 0.4f, 0.6f);
        Assert.Equal(new PixelRect(0, 0, 11, 10), half.Bounds(new PixelRect(-50, -50, 200, 200)));
    }

    [Fact]
    public void AnEmptyOrDegeneratePolygonSelectsNothing()
    {
        Assert.True(Selection.Polygons([]).Tiles.IsEmpty);
        Assert.True(Selection.Polygon([new(0, 0), new(5, 5)]).Tiles.IsEmpty);
    }
}
