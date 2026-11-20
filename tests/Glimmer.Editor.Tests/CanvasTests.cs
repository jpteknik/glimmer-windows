// Tests for the port of App/CanvasController.swift (fit, zoom around a point, pan, rotation with snapping, visible
// tiles) and the frame drawing that replaces CanvasNSView.draw(): a frame captured on the UI thread and drawn on
// another into a BGRA buffer. The original has no tests at this level.
using System.Numerics;
using Glimmer.Core;

namespace Glimmer.Editor.Tests;

public class CanvasTests
{
    [Fact]
    public void ANewWindowFitsTheDocument()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.White(2000, 1000));
        Assert.Equal(new Vec2(2000, 1000), s.Canvas.DocumentSize);
        s.Canvas.ViewDidResize(new Vec2(1000, 800));
        // 92 % of the view's width, centred.
        Assert.Equal(1000 * 0.92 / 2000, s.Canvas.Transform.Scale, 6);
        var centre = s.Canvas.DocumentPoint(new Vec2(500, 400));
        Assert.Equal(1000, centre.X, 6);
        Assert.Equal(500, centre.Y, 6);
        // Resizing the window keeps the centre where it was.
        s.Canvas.ViewDidResize(new Vec2(1200, 800));
        Assert.Equal(1000, s.Canvas.DocumentPoint(new Vec2(600, 400)).X, 6);
    }

    [Fact]
    public void ZoomKeepsThePointUnderThePointer()
    {
        using var h = new Harness();
        var s = h.NewSession();
        s.Canvas.ViewDidResize(new Vec2(1000, 800));
        var anchor = new Vec2(300, 200);
        var under = s.Canvas.DocumentPoint(anchor);
        s.Canvas.ZoomBy(2.5, anchor);
        var after = s.Canvas.DocumentPoint(anchor);
        Assert.Equal(under.X, after.X, 6);
        Assert.Equal(under.Y, after.Y, 6);
        s.Canvas.Pan(new Vec2(10, -20));
        Assert.Equal(under.X - 10 / s.Canvas.Transform.Scale, s.Canvas.DocumentPoint(anchor).X, 6);
    }

    [Fact]
    public void ZoomStepsFollowTheLadderAndStopAtTheEnds()
    {
        using var h = new Harness();
        var s = h.NewSession();
        s.Canvas.ViewDidResize(new Vec2(1000, 800));
        s.Perform(CommandID.ActualSize);
        s.Perform(CommandID.ZoomIn);
        Assert.Equal(ZoomLadder.Next(1), s.Canvas.Zoom, 6);
        for (int i = 0; i < 100; i++) s.Perform(CommandID.ZoomIn);
        Assert.False(s.CanPerform(CommandID.ZoomIn));
        for (int i = 0; i < 200; i++) s.Perform(CommandID.ZoomOut);
        Assert.False(s.CanPerform(CommandID.ZoomOut));
    }

    [Fact]
    public void BackingScaleCountsInTheZoom()
    {
        using var h = new Harness();
        var s = h.NewSession();
        s.Canvas.ViewDidResize(new Vec2(1000, 800));
        s.Canvas.BackingScale = 1.5;
        s.Perform(CommandID.ActualSize);
        // 100 % is one document pixel per device pixel.
        Assert.Equal(1, s.Canvas.Zoom, 6);
        Assert.Equal(1 / 1.5, s.Canvas.Transform.Scale, 6);
        s.Canvas.ToggleFitAndActualSize(new Vec2(500, 400));
        Assert.NotEqual(1, s.Canvas.Zoom, 3);
        s.Canvas.ToggleFitAndActualSize(new Vec2(500, 400));
        Assert.Equal(1, s.Canvas.Zoom, 6);
    }

    [Fact]
    public void RotationSnapsToRightAngles()
    {
        using var h = new Harness();
        var s = h.NewSession();
        s.Canvas.ViewDidResize(new Vec2(1000, 800));
        s.Canvas.BeginRotationGesture();
        s.Canvas.RotateGesture(88 * Math.PI / 180, new Vec2(500, 400));
        Assert.Equal(90, s.Canvas.RotationDegrees, 6);
        s.Canvas.RotateGesture(10 * Math.PI / 180, new Vec2(500, 400));
        Assert.Equal(98, s.Canvas.RotationDegrees, 6);
        s.Canvas.RotationDegrees = 30;
        Assert.Equal(30, s.Canvas.RotationDegrees, 6);
        // Fit keeps the rotation.
        s.Perform(CommandID.FitToWindow);
        Assert.Equal(30, s.Canvas.RotationDegrees, 6);
    }

    [Fact]
    public void VisibleTilesAreTheOnesOnScreenNearestTheCentreFirst()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.White(4096, 4096));
        Assert.Empty(s.Canvas.VisibleTiles(s.State.Bounds));
        s.Canvas.ViewDidResize(new Vec2(1000, 800));
        s.Perform(CommandID.ActualSize);
        var tiles = s.Canvas.VisibleTiles(s.State.Bounds);
        // 1000 × 800 at 100 % around the middle of the document: 5 × 5 tiles at most.
        Assert.InRange(tiles.Count, 4, 25);
        Assert.Contains(tiles[0], new[] { new TileCoord(7, 7), new TileCoord(7, 8), new TileCoord(8, 7), new TileCoord(8, 8) });
        Assert.All(tiles, t => Assert.True(t.X is >= 5 and <= 10 && t.Y is >= 5 and <= 10, $"{t}"));
    }

    [Fact]
    public void AFrameShowsTheDocumentOnThePasteboard()
    {
        using var h = new Harness(settings => settings.Set(Preferences.CanvasBackgroundKey, "black"));
        var s = h.NewSession(Docs.TwoTone());
        int renders = 0;
        s.Canvas.NeedsRender += (_, _) => renders++;
        s.Canvas.ViewDidResize(new Vec2(800, 600));
        Assert.True(renders > 0);
        var frame = s.Canvas.Renderer.CaptureFrame(800, 600);
        Assert.Equal((800, 600), (frame.PixelWidth, frame.PixelHeight));
        var buffer = new byte[800 * 600 * 4];
        // Composited a few tiles at a time; draw until nothing is left.
        int passes = 0;
        while (frame.Render(buffer, 800 * 4, CancellationToken.None) && passes++ < 100) frame = s.Canvas.Renderer.CaptureFrame(800, 600);
        Bgra Pixel(Vec2 document)
        {
            var v = s.Canvas.Transform.ViewPoint(document);
            int i = ((int)v.Y * 800 + (int)v.X) * 4;
            return new Bgra(buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]);
        }
        Assert.Equal(new Bgra(0, 0, 0, 255), Pixel(new Vec2(100, 150)));
        Assert.Equal(new Bgra(255, 255, 255, 255), Pixel(new Vec2(300, 150)));
        // Outside the document: the pasteboard (black in Settings here).
        Assert.Equal(new Bgra(0, 0, 0, 255), new Bgra(buffer[0], buffer[1], buffer[2], buffer[3]));
        // "Match System" is Glimmer's own near-black, #0c0d11.
        h.Settings.Set(Preferences.CanvasBackgroundKey, "system");
        s.Canvas.Renderer.CaptureFrame(800, 600).Render(buffer, 800 * 4, CancellationToken.None);
        Assert.Equal(new Bgra(0x11, 0x0d, 0x0c, 255), new Bgra(buffer[0], buffer[1], buffer[2], buffer[3]));
    }

    private readonly record struct Bgra(byte B, byte G, byte R, byte A);

    [Fact]
    public void AFrameIsASnapshot()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.Canvas.ViewDidResize(new Vec2(400, 300));
        var frame = s.Canvas.Renderer.CaptureFrame(400, 300);
        // The document changes after the capture: the frame still draws what was captured.
        s.Perform(CommandID.SelectAll);
        s.Perform(CommandID.FillForeground);
        var buffer = new byte[400 * 300 * 4];
        int passes = 0;
        while (frame.Render(buffer, 400 * 4, CancellationToken.None) && passes++ < 100) { }
        var v = s.Canvas.Transform.ViewPoint(new Vec2(300, 150));
        int i = ((int)v.Y * 400 + (int)v.X) * 4;
        Assert.Equal(255, buffer[i]);
    }

    [Fact]
    public void ACancelledFrameStops()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.Canvas.ViewDidResize(new Vec2(400, 300));
        var frame = s.Canvas.Renderer.CaptureFrame(400, 300);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => frame.Render(new byte[400 * 300 * 4], 400 * 4, cancelled.Token));
    }

    [Fact]
    public void ChannelViewsShowOneChannelAsGrey()
    {
        using var h = new Harness();
        var red = Docs.Gray(0);
        red = red with { Layers = [red.Layers[0] with { Content = new RasterContent(TileGrid.Solid(new Vector4(1, 0, 0, 1), red.Bounds)) }] };
        var s = h.NewSession(red);
        s.Canvas.ViewDidResize(new Vec2(400, 300));
        s.ChannelMask = 2;   // green only
        var buffer = new byte[400 * 300 * 4];
        var frame = s.Canvas.Renderer.CaptureFrame(400, 300);
        int passes = 0;
        while (frame.Render(buffer, 400 * 4, CancellationToken.None) && passes++ < 100) frame = s.Canvas.Renderer.CaptureFrame(400, 300);
        int i = (150 * 400 + 200) * 4;
        Assert.Equal((0, 0, 0), (buffer[i], buffer[i + 1], buffer[i + 2]));
    }
}
