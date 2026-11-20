// Ported from Packages/GlimmerKit/Tests/GlimmerCoreTests/ViewTransformTests.swift

namespace Glimmer.Core.Tests;

public class ViewTransformTests
{
    private static bool Close(Vec2 a, Vec2 b, double tolerance = 1e-9) => (a - b).Length <= tolerance;

    private static readonly Vec2[] SamplePoints = [Vec2.Zero, new(100, 50), new(-30, 2000), new(1919.5, 1079.5)];

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(-2.1)]
    [InlineData(Math.PI)]
    public void ViewDocumentRoundTrip(double rotation)
    {
        var t = new ViewTransform(0.37, rotation, new Vec2(12, -40));
        foreach (var p in SamplePoints) Assert.True(Close(t.DocumentPoint(t.ViewPoint(p)), p, 1e-7));
    }

    [Fact]
    public void MatrixMatchesDocumentPoint()
    {
        var t = new ViewTransform(2.5, 0.7, new Vec2(300, 120));
        foreach (var p in SamplePoints)
        {
            var h = t.ViewToDocumentMatrix * new Vec3(p, 1);
            Assert.True(Close(new Vec2(h.X, h.Y), t.DocumentPoint(p), 1e-9));
        }
    }

    [Fact]
    public void ZoomKeepsAnchorFixed()
    {
        var t = new ViewTransform(1, 0.4, new Vec2(50, 60));
        var anchor = new Vec2(400.0, 300.0);
        var before = t.DocumentPoint(anchor);
        t = t.Zoom(3, anchor);
        Assert.True(Close(t.DocumentPoint(anchor), before));
        Assert.True(Math.Abs(t.Scale - 3) < 1e-12);
    }

    [Fact]
    public void RotateKeepsAnchorFixed()
    {
        var t = new ViewTransform(0.5, translation: new Vec2(10, 10));
        var anchor = new Vec2(200.0, 150.0);
        var before = t.DocumentPoint(anchor);
        t = t.Rotate(Math.PI / 12, anchor);
        Assert.True(Close(t.DocumentPoint(anchor), before));
    }

    [Fact]
    public void ScaleIsClamped()
    {
        var t = new ViewTransform();
        t = t.Zoom(1e6, Vec2.Zero);
        Assert.Equal(ViewTransform.MaxScale, t.Scale);
        t = t.Zoom(1e-9, Vec2.Zero);
        Assert.Equal(ViewTransform.MinScale, t.Scale);
        Assert.Equal(1, new ViewTransform(double.NaN).Scale);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(Math.PI / 4)]
    [InlineData(Math.PI / 2)]
    public void FittingCentersAndContains(double rotation)
    {
        var doc = new Vec2(1920.0, 1080.0);
        var view = new Vec2(800.0, 600.0);
        var t = ViewTransform.Fitting(doc, view, rotation);
        Assert.True(Close(t.ViewPoint(doc / 2), view / 2, 1e-9));
        foreach (var corner in new[] { new Vec2(0.0, 0.0), new Vec2(doc.X, 0), new Vec2(0, doc.Y), doc })
        {
            var v = t.ViewPoint(corner);
            Assert.True(v.X >= -1e-9 && v.X <= view.X + 1e-9);
            Assert.True(v.Y >= -1e-9 && v.Y <= view.Y + 1e-9);
        }
    }

    [Fact]
    public void RotationIsNormalized()
    {
        var t = new ViewTransform(rotation: 3 * Math.PI);
        Assert.True(Math.Abs(t.Rotation - Math.PI) < 1e-12);
        Assert.True(Math.Abs(new ViewTransform(rotation: -3 * Math.PI / 2).Rotation - Math.PI / 2) < 1e-12);
    }

    [Fact]
    public void Snapping()
    {
        double deg = Math.PI / 180;
        Assert.Equal(0, ViewTransform.SnappedRotation(2 * deg));
        Assert.Equal(Math.PI / 2, ViewTransform.SnappedRotation(88 * deg));
        Assert.Equal(10 * deg, ViewTransform.SnappedRotation(10 * deg));
    }
}

public class ZoomLadderTests
{
    [Fact]
    public void StepsUpAndDown()
    {
        Assert.Equal(1.5, ZoomLadder.Next(1));
        Assert.Equal(1, ZoomLadder.Next(0.9));
        Assert.Equal(0.6667, ZoomLadder.Previous(1));
        Assert.Equal(1, ZoomLadder.Previous(1.2));
    }

    [Fact]
    public void ClampsAtEnds()
    {
        Assert.Equal(64, ZoomLadder.Next(64));
        Assert.Equal(0.02, ZoomLadder.Previous(0.02));
    }
}
