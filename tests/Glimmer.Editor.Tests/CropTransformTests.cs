// Tests for the port of App/EditorSession+Crop.swift (crop, straighten, flip and rotate, Image Size, Canvas Size) and
// App/EditorSession+Transform.swift (Free Transform): commit and cancel, through the canvas as a person uses them.
// The original has no tests at this level.
using Glimmer.Core;
using Glimmer.Tools;

namespace Glimmer.Editor.Tests;

public class CropTransformTests
{
    private static string[] Steps(EditorSession s) => [.. s.Document.History.Select(t => t.ToString())];

    [Fact]
    public void CropCommitsToTheBoxDrawn()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.AddGuide(GuideAxis.Vertical, 150);
        h.Scheduler.RunPosted();
        s.Tool = ToolID.Crop;
        // Starts on the whole image.
        Assert.Equal(new CGRectLike(0, 0, 400, 300), s.CropSession!.Rect);
        new CanvasInput(s, h.Scheduler).Drag(new Vec2(150, 20), new Vec2(350, 170));
        Assert.Equal(new CGRectLike(150, 20, 350, 170), s.CropSession!.Rect);
        Assert.Equal(4, s.CropSession.Corners.Count);
        s.ApplyCrop();
        Assert.Null(s.CropSession);
        Assert.Equal((200, 150), (s.State.Width, s.State.Height));
        Assert.Equal(["New Guide", "Crop"], Steps(s));
        // What was at (150, 20) is at the origin; the guide moved along.
        Assert.Equal(Docs.BlackPixel, Docs.Pixel(s.State.Layers[0], 0, 0));
        Assert.Equal(Docs.WhitePixel, Docs.Pixel(s.State.Layers[0], 60, 0));
        Assert.Equal(0, s.State.Aids.Guides[0].Position);
        // Pixels outside the crop are gone (crop.deletePixels is on by default).
        Assert.Equal(0, Docs.Pixel(s.State.Layers[0], -10, 10).W);
        Assert.Equal(new Vec2(200, 150), s.Canvas.DocumentSize);
    }

    [Fact]
    public void CropKeepsPixelsOutsideWhenAskedTo()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.CropDeletesPixels = false;
        Assert.False(h.Settings.Get(Preferences.CropDeletesKey, true));
        s.Tool = ToolID.Crop;
        new CanvasInput(s, h.Scheduler).Drag(new Vec2(100, 0), new Vec2(300, 300));
        s.ApplyCrop();
        Assert.Equal(200, s.State.Width);
        Assert.Equal(Docs.BlackPixel, Docs.Pixel(s.State.Layers[0], -50, 10));
    }

    [Fact]
    public void CropCancelsWithEscOrAnotherTool()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        var input = new CanvasInput(s, h.Scheduler);
        s.Tool = ToolID.Crop;
        input.Drag(new Vec2(10, 10), new Vec2(100, 100));
        s.CancelCrop();
        Assert.Null(s.CropSession);
        s.Tool = ToolID.Move;
        s.Tool = ToolID.Crop;
        input.Drag(new Vec2(10, 10), new Vec2(100, 100));
        s.Tool = ToolID.Brush;
        Assert.Null(s.CropSession);
        Assert.Equal((400, 300), (s.State.Width, s.State.Height));
        Assert.Empty(s.Document.History);
    }

    [Fact]
    public void CropBoxMovesResizesAndKeepsItsAspect()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        var input = new CanvasInput(s, h.Scheduler);
        s.Tool = ToolID.Crop;
        input.Drag(new Vec2(100, 100), new Vec2(200, 200));
        // Inside the box: moves it.
        input.Drag(new Vec2(150, 150), new Vec2(170, 160));
        Assert.Equal(new CGRectLike(120, 110, 220, 210), s.CropSession!.Rect);
        // The bottom-right handle: resizes.
        input.Drag(new Vec2(220, 210), new Vec2(260, 230));
        Assert.Equal(new CGRectLike(120, 110, 260, 230), s.CropSession!.Rect);
        s.SetCropAspect(1);
        Assert.Equal(s.CropSession!.Rect.Width, s.CropSession.Rect.Height, 6);
        // A click (a tiny new box) puts the crop back on the whole image.
        s.SetCropAspect(null);
        input.Click(new Vec2(20, 20));
        Assert.Equal(new CGRectLike(0, 0, 400, 300), s.CropSession!.Rect);
        Assert.Equal(8, s.CropHandles(s.CropSession.Rect).Count);
    }

    [Fact]
    public void StraightenedCropRotatesTheImage()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.Tool = ToolID.Crop;
        s.SetCropAngle(10);
        var c = s.CropSession!;
        Assert.Equal(10 * Math.PI / 180, c.Angle, 9);
        // The box shrank to stay inside the image.
        Assert.True(c.Rect.Width < 400 && c.Rect.Height < 300);
        int w = (int)Math.Round(c.Rect.Width), hgt = (int)Math.Round(c.Rect.Height);
        s.ApplyCrop();
        Assert.Equal((w, hgt), (s.State.Width, s.State.Height));
        Assert.Equal(["Crop"], Steps(s));
        // Still opaque everywhere: the box stayed inside the image.
        Assert.Equal(1, Docs.Pixel(s.State.Layers[0], 2, 2).W, 2);
    }

    [Theory]
    [InlineData("image.flipHorizontal")]
    [InlineData("image.flipVertical")]
    [InlineData("image.rotateRight")]
    [InlineData("image.rotateLeft")]
    [InlineData("image.rotate180")]
    public void FlipsAndRotationsAreUndoable(string raw)
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        var id = new CommandID(raw);
        Assert.NotNull(WindowsCommands.Registry.Descriptor(id));
        var before = s.State;
        s.Perform(id);
        Assert.Single(s.Document.History);
        bool quarter = raw.EndsWith("Right") || raw.EndsWith("Left");
        Assert.Equal(quarter ? (300, 400) : (400, 300), (s.State.Width, s.State.Height));
        s.Document.Undo();
        Assert.Equal((400, 300), (s.State.Width, s.State.Height));
        Assert.Equal(Docs.Pixel(before.Layers[0], 10, 10), Docs.Pixel(s.State.Layers[0], 10, 10));
    }

    [Fact]
    public void FlipHorizontalMirrorsThePixels()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.FlipCanvas(CanvasFlip.Horizontal);
        Assert.Equal(Docs.WhitePixel, Docs.Pixel(s.State.Layers[0], 10, 10));
        Assert.Equal(Docs.BlackPixel, Docs.Pixel(s.State.Layers[0], 390, 10));
        Assert.Equal(["Flip Horizontal"], Steps(s));
        s.FlipCanvas(CanvasFlip.RotateRight);
        // (x, y) → (h − y, x): the white half (left after the flip) is now at the top.
        Assert.Equal(Docs.WhitePixel, Docs.Pixel(s.State.Layers[0], 150, 10));
        Assert.Equal(Docs.BlackPixel, Docs.Pixel(s.State.Layers[0], 150, 390));
    }

    [Fact]
    public void ImageSizeScalesAndRefusesImpossibleSizes()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.AddGuide(GuideAxis.Vertical, 100);
        s.ResizeImage(200, 150);
        Assert.Equal((200, 150), (s.State.Width, s.State.Height));
        Assert.Equal(50, s.State.Aids.Guides[0].Position);
        Assert.Equal(Docs.BlackPixel, Docs.Pixel(s.State.Layers[0], 50, 75));
        Assert.Equal("Image Size", Steps(s)[^1]);
        // The same size: nothing to do.
        s.ResizeImage(200, 150);
        Assert.Equal(2, s.Document.History.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.ResizeImage(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.ResizeCanvas(10, -1, 1, 1));
    }

    [Fact]
    public void CanvasSizeKeepsTheImageAtTheAnchor()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.ResizeCanvas(600, 300, 1, 1);
        Assert.Equal((600, 300), (s.State.Width, s.State.Height));
        // Centred: the image moved 100 to the right; the new room is transparent.
        Assert.Equal(Docs.BlackPixel, Docs.Pixel(s.State.Layers[0], 110, 10));
        Assert.Equal(0, Docs.Pixel(s.State.Layers[0], 50, 10).W);
        Assert.Equal(["Canvas Size"], Steps(s));
        // Smaller, anchored top left: what is cut off is kept outside the canvas.
        s.ResizeCanvas(100, 100, 0, 0);
        Assert.Equal(Docs.WhitePixel, Docs.Pixel(s.State.Layers[0], 450, 10));
    }

    [Fact]
    public void FreeTransformMovesAndCommitsOneStep()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.WithSquare());
        var input = new CanvasInput(s, h.Scheduler);
        Assert.True(s.CanPerform(CommandID.FreeTransform));
        s.Perform(CommandID.FreeTransform);
        var t = s.TransformSession!;
        Assert.Equal([new Vec2(200, 150), new Vec2(400, 150), new Vec2(400, 350), new Vec2(200, 350)], t.Corners);
        Assert.Equal(new Vec2(300, 250), t.Centre);
        input.Drag(new Vec2(300, 250), new Vec2(350, 250));
        Assert.Equal(new Vec2(350, 250), s.TransformSession!.Centre);
        // A preview only, until committed.
        Assert.Empty(s.Document.History);
        Assert.Equal(Docs.BlackPixel, Docs.Pixel(s.State.Named("Square"), 210, 160));
        s.CommitTransform();
        Assert.Null(s.TransformSession);
        Assert.Equal(["Free Transform"], Steps(s));
        var square = s.State.Named("Square");
        Assert.Equal(0, Docs.Pixel(square, 210, 160).W, 3);
        Assert.Equal(1, Docs.Pixel(square, 440, 200).W, 3);
        Assert.True(s.Overrides.Pixels.IsEmpty);
    }

    [Fact]
    public void FreeTransformScalesAndRotatesFromTheOptionsBar()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.WithSquare());
        s.Tool = ToolID.FreeTransform;
        s.SetTransformScaleXPercent(50);
        s.SetTransformDegrees(90);
        var t = s.TransformSession!;
        Assert.Equal(0.5, t.ScaleX, 6);
        Assert.Equal(Math.PI / 2, t.Rotation, 6);
        Assert.False(t.IsQuad);
        // Choosing another tool commits.
        s.Tool = ToolID.Move;
        Assert.Equal(["Free Transform"], Steps(s));
        var bounds = new Selection(s.State.Named("Square").Tiles).Bounds(s.State.Bounds)!.Value;
        // 100 wide and 200 tall, turned a quarter: 200 wide and 100 tall about the same centre.
        Assert.InRange(bounds.Width, 198, 202);
        Assert.InRange(bounds.Height, 98, 102);
    }

    [Fact]
    public void FreeTransformCancels()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.WithSquare());
        var before = s.State;
        s.Tool = ToolID.FreeTransform;
        new CanvasInput(s, h.Scheduler).Drag(new Vec2(300, 250), new Vec2(350, 300));
        s.CancelTransform();
        Assert.Null(s.TransformSession);
        Assert.Same(before, s.State);
        Assert.Empty(s.Document.History);
        Assert.True(s.Overrides.Pixels.IsEmpty);
    }

    [Fact]
    public void DistortingTextAsksBeforeMakingItPixels()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.TypeClick(new Vec2(50, 100));
        var id = s.EditingText!.Value;
        s.CommitText("Glimmer", [], id);
        s.EditingText = null;
        h.Scheduler.RunPosted();
        s.Tool = ToolID.FreeTransform;
        Assert.NotNull(s.TransformSession);
        s.SetTransformMode(TransformMode.Distort);
        var corner = s.TransformSession!.Corners[2];
        new CanvasInput(s, h.Scheduler).Drag(corner, corner + new Vec2(30, 20));
        Assert.True(s.TransformSession!.IsQuad);
        s.CommitTransform();
        Assert.Equal(1, s.RasterizeTextConfirmation);
        Assert.NotNull(s.TransformSession);
        s.RasterizeTextConfirmation = null;
        s.CommitTransform(confirmed: true);
        Assert.Null(s.TransformSession);
        Assert.Equal("Free Transform", Steps(s)[^1]);
        Assert.IsType<RasterContent>(s.State.LayerById(id)!.Content);
    }
}
