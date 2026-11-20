// Tests for the port of App/EditorSession+Tools.swift, +Retouch, +Paths, +Shapes, +Text, +Liquify and the Move,
// marquee and canvas tools of EditorSession.swift: every tool pressed, dragged and released as the canvas sends it
// (App/CanvasView.swift), and what each leaves in the document and its history. The original has no tests at this level.
using System.Numerics;
using Glimmer.Core;
using Glimmer.Tools;

namespace Glimmer.Editor.Tests;

[Collection(SharedModelStoreCollection.Name)]
public class ToolTests
{
    private static (Harness H, EditorSession S, CanvasInput Input) Start(DocumentState state, ToolID tool)
    {
        var h = new Harness();
        var s = h.NewSession(state);
        s.Tool = tool;
        h.Scheduler.RunPosted();
        return (h, s, new CanvasInput(s, h.Scheduler));
    }

    private static string[] Steps(EditorSession s) => [.. s.Document.History.Select(t => t.ToString())];

    /// <summary>Every tool can be pressed, dragged and released on a document with pixels without an error, and leaves
    /// no preview or half-finished drag behind.</summary>
    [Theory]
    [MemberData(nameof(AllTools))]
    public void EveryToolBeginsUpdatesAndEnds(ToolID tool)
    {
        var (h, s, input) = Start(Docs.TwoTone(), tool);
        using var _ = h;
        if (tool is ToolID.CloneStamp or ToolID.HealingBrush) input.Click(new Vec2(60, 60), InputModifiers.Option);
        input.Drag(new Vec2(150, 100), new Vec2(260, 180));
        // Modal tools end when another tool is chosen (the commit), as on the Mac; an open polygon lasso survives that
        // there too, and ends with Return (or Esc).
        if (tool == ToolID.PolygonLasso) s.FinishLasso();
        s.Tool = ToolID.Move;
        // And the canvas closes an open text editor when the tool changes (CanvasNSView.updateTextEditor).
        s.EditingText = null;
        h.Scheduler.Settle();
        Assert.Null(s.ErrorMessage);
        Assert.Null(s.Marquee);
        Assert.Null(s.Lasso);
        Assert.Null(s.GradientDrag);
        Assert.Null(s.ShapeDrag);
        Assert.Null(s.TransformSession);
        Assert.Null(s.CropSession);
        Assert.True(s.Overrides.Equals(Glimmer.Render.LayerOverrides.None), $"{tool} left a preview behind");
    }

    public static TheoryData<ToolID> AllTools()
    {
        var data = new TheoryData<ToolID>();
        foreach (var t in Enum.GetValues<ToolID>()) data.Add(t);
        return data;
    }

    [Fact]
    public void BrushPaintsTheForeground()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Brush);
        using var _ = h;
        s.Foreground = new SRGBColor(1, 0, 0);
        input.Drag(new Vec2(250, 100), new Vec2(350, 100));
        Assert.Equal(["Brush Stroke"], Steps(s));
        var p = Docs.Pixel(s.State.Layers[0], 300, 100);
        Assert.True(p.X > 0.99 && p.Y < 0.01 && p.Z < 0.01, $"{p}");
        // Away from the stroke nothing changed.
        Assert.Equal(Docs.WhitePixel, Docs.Pixel(s.State.Layers[0], 300, 250));
        // The live preview is gone; the stroke is in the document.
        Assert.True(s.Overrides.Pixels.IsEmpty);
    }

    [Fact]
    public void BrushInsideASelectionPaintsOnlyThere()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Brush);
        using var _ = h;
        s.Edit("Select", st => st with { Selection = Selection.Rectangle(new CGRectLike(250, 0, 300, 300)) });
        h.Scheduler.RunPosted();
        s.Foreground = new SRGBColor(1, 0, 0);
        input.Drag(new Vec2(220, 100), new Vec2(330, 100));
        Assert.True(Docs.Pixel(s.State.Layers[0], 270, 100).Y < 0.01);
        Assert.Equal(Docs.WhitePixel, Docs.Pixel(s.State.Layers[0], 320, 100));
    }

    [Fact]
    public void BrushOnAGroupSaysWhy()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Brush);
        using var _ = h;
        s.Perform(CommandID.NewGroup);
        input.Drag(new Vec2(250, 100), new Vec2(300, 100));
        Assert.Equal("Select a pixel layer, or click a layer mask to paint it.", s.ErrorMessage?.ToString());
        Assert.Equal(["New Group"], Steps(s));
    }

    [Fact]
    public void BrushPaintsTheMaskWhileEditingIt()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Brush);
        using var _ = h;
        s.Perform(CommandID.AddMaskRevealAll);
        s.Perform(CommandID.EditMask);
        Assert.True(s.PaintsMask);
        h.Scheduler.RunPosted();
        input.Drag(new Vec2(250, 100), new Vec2(300, 100));
        Assert.Equal("Paint Mask", Steps(s)[^1]);
        var mask = s.State.Layers[0].Mask!;
        Assert.True(mask.Tiles.ReadChannel(new PixelRect(270, 100, 1, 1), 0, mask.DefaultValue)[0] < 0.01);
    }

    [Fact]
    public void EraserMakesPixelsTransparent()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Eraser);
        using var _ = h;
        input.Drag(new Vec2(50, 100), new Vec2(150, 100));
        Assert.Equal(["Eraser"], Steps(s));
        Assert.True(Docs.Pixel(s.State.Layers[0], 100, 100).W < 0.01);
    }

    [Fact]
    public void ShiftClickDrawsAStraightLineFromTheLastStroke()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Brush);
        using var _ = h;
        input.Click(new Vec2(250, 50));
        input.Click(new Vec2(350, 250), InputModifiers.Shift);
        // The point halfway along the line was painted.
        Assert.True(Docs.Pixel(s.State.Layers[0], 300, 150).X < 0.05);
        Assert.Equal(2, s.Document.History.Count);
    }

    [Fact]
    public void PaintBucketFillsTheContiguousArea()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.PaintBucket);
        using var _ = h;
        s.Foreground = new SRGBColor(0, 0, 1);
        input.Click(new Vec2(300, 150));
        Assert.Equal(["Paint Bucket"], Steps(s));
        var right = Docs.Pixel(s.State.Layers[0], 390, 290);
        Assert.True(right.Z > 0.99 && right.X < 0.01, $"{right}");
        Assert.Equal(Docs.BlackPixel, Docs.Pixel(s.State.Layers[0], 100, 150));
    }

    [Fact]
    public void GradientAddsAnEditableFillLayer()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Gradient);
        using var _ = h;
        input.Drag(new Vec2(0, 150), new Vec2(400, 150));
        Assert.Equal(["Gradient"], Steps(s));
        var layer = s.ActiveLayer!;
        Assert.Equal("Gradient", layer.Name);
        var fill = Assert.IsType<Fill.Gradient>(Assert.IsType<FillContent>(layer.Content).Fill);
        Assert.Equal(new Vec2(0, 150), fill.Value.Start);
        Assert.Equal(new Vec2(400, 150), fill.Value.End);
        // A drag of two pixels or less is a click: nothing.
        input.Drag(new Vec2(10, 10), new Vec2(11, 11));
        Assert.Single(s.Document.History);
    }

    [Fact]
    public void EyedropperSamplesIntoForegroundOrBackground()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Eyedropper);
        using var _ = h;
        s.Foreground = new SRGBColor(1, 0, 0);
        s.Background = new SRGBColor(1, 0, 0);
        input.Click(new Vec2(100, 100));
        AssertColor(SRGBColor.Black, s.Foreground);
        input.Click(new Vec2(300, 100), InputModifiers.Option);
        AssertColor(SRGBColor.White, s.Background);
        AssertColor(SRGBColor.Black, s.Foreground);
        // Dragging keeps sampling.
        input.Drag(new Vec2(100, 100), new Vec2(300, 100));
        AssertColor(SRGBColor.White, s.Foreground);
        Assert.Empty(s.Document.History);
    }

    private static void AssertColor(SRGBColor expected, SRGBColor actual)
    {
        Assert.True(Math.Abs(expected.R - actual.R) < 1e-6 && Math.Abs(expected.G - actual.G) < 1e-6 && Math.Abs(expected.B - actual.B) < 1e-6,
                    $"expected {expected}, got {actual}");
    }

    [Fact]
    public void MoveTranslatesTheActiveLayer()
    {
        var (h, s, input) = Start(Docs.WithSquare(), ToolID.Move);
        using var _ = h;
        input.Drag(new Vec2(300, 250), new Vec2(340, 270));
        Assert.Equal(["Move"], Steps(s));
        var square = s.State.Named("Square");
        Assert.Equal(Docs.BlackPixel, Docs.Pixel(square, 430, 360));
        Assert.Equal(0, Docs.Pixel(square, 210, 160).W);
        // The background didn't move.
        Assert.Equal(Docs.WhitePixel, Docs.Pixel(s.State.Named("Background"), 5, 5));
        // A press without movement changes nothing.
        input.Click(new Vec2(300, 250));
        Assert.Single(s.Document.History);
    }

    [Fact]
    public void NudgeMovesByWholePixels()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.WithSquare());
        s.NudgeActiveLayer(-10, 0);
        Assert.Equal(Docs.BlackPixel, Docs.Pixel(s.State.Named("Square"), 190, 200));
        Assert.Equal("Nudge", s.Document.History[^1].ToString());
    }

    [Fact]
    public void RectangleMarqueeSelects()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.RectangleMarquee);
        using var _ = h;
        input.Drag(new Vec2(50, 40), new Vec2(150, 120));
        Assert.Equal(["Rectangular Marquee"], Steps(s));
        Assert.Equal(new PixelRect(50, 40, 100, 80), s.State.Selection?.Bounds(s.State.Bounds));
        // A click deselects.
        input.Click(new Vec2(10, 10));
        Assert.Null(s.State.Selection);
        Assert.Equal("Deselect", Steps(s)[^1]);
    }

    [Fact]
    public void MarqueeModifiersAddSubtractAndIntersect()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.RectangleMarquee);
        using var _ = h;
        input.Drag(new Vec2(0, 0), new Vec2(100, 100));
        input.Drag(new Vec2(200, 0), new Vec2(300, 100), InputModifiers.Shift);
        var both = s.State.Selection!;
        Assert.Equal(new PixelRect(0, 0, 300, 100), both.Bounds(s.State.Bounds));
        Assert.Equal(0, both.Values(new PixelRect(150, 50, 1, 1))[0]);
        input.Drag(new Vec2(0, 0), new Vec2(50, 100), InputModifiers.Option);
        Assert.Equal(new PixelRect(50, 0, 250, 100), s.State.Selection!.Bounds(s.State.Bounds));
        input.Drag(new Vec2(250, 0), new Vec2(400, 300), InputModifiers.Shift | InputModifiers.Option);
        Assert.Equal(new PixelRect(250, 0, 50, 100), s.State.Selection!.Bounds(s.State.Bounds));
    }

    [Fact]
    public void EllipticalMarqueeSelectsAnEllipse()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.EllipseMarquee);
        using var _ = h;
        input.Drag(new Vec2(100, 100), new Vec2(300, 200));
        Assert.Equal(["Elliptical Marquee"], Steps(s));
        var sel = s.State.Selection!;
        Assert.True(sel.Values(new PixelRect(200, 150, 1, 1))[0] > 0.99);
        Assert.True(sel.Values(new PixelRect(102, 102, 1, 1))[0] < 0.01);
    }

    [Fact]
    public void SelectionFeatherSoftensWhatTheToolsMake()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.RectangleMarquee);
        using var _ = h;
        s.SelectionFeather = 8;
        input.Drag(new Vec2(100, 100), new Vec2(300, 200));
        float edge = s.State.Selection!.Values(new PixelRect(100, 150, 1, 1))[0];
        Assert.True(edge > 0.05 && edge < 0.95, $"{edge}");
    }

    [Fact]
    public void LassoSelectsThePolygonDrawn()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Lasso);
        using var _ = h;
        input.Down(new Vec2(100, 100));
        input.Dragged(new Vec2(300, 100));
        input.Dragged(new Vec2(300, 250));
        input.Dragged(new Vec2(100, 250));
        Assert.Equal(4, s.Lasso!.Count);
        input.Up(new Vec2(100, 250));
        h.Scheduler.RunPosted();
        Assert.Null(s.Lasso);
        Assert.Equal(["Lasso"], Steps(s));
        var bounds = s.State.Selection!.Bounds(s.State.Bounds)!.Value;
        Assert.InRange(bounds.X, 99, 101);
        Assert.InRange(bounds.MaxX, 299, 301);
    }

    [Fact]
    public void PolygonLassoClosesOnDoubleClickOrAtTheStart()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.PolygonLasso);
        using var _ = h;
        input.Click(new Vec2(100, 100));
        input.Click(new Vec2(300, 100));
        input.Click(new Vec2(300, 250));
        Assert.Equal(3, s.Lasso!.Count);
        Assert.Null(s.State.Selection);
        input.Click(new Vec2(102, 101));   // near the start: closes
        Assert.Null(s.Lasso);
        Assert.Equal(["Lasso"], Steps(s));
        Assert.True(s.State.Selection!.Values(new PixelRect(250, 130, 1, 1))[0] > 0.99);

        // A double-click is a click and then a second press with a click count of 2: it closes the polygon there.
        input.Click(new Vec2(10, 10));
        input.Click(new Vec2(60, 10));
        input.Click(new Vec2(60, 60));
        input.Click(new Vec2(60, 60), clickCount: 2);
        Assert.Null(s.Lasso);
        Assert.Equal(["Lasso", "Lasso"], Steps(s));
        var bounds = s.State.Selection!.Bounds(s.State.Bounds)!.Value;
        Assert.Equal((10, 10), (bounds.X, bounds.Y));
    }

    [Fact]
    public void MagicWandSelectsTheRegionUnderThePointer()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.MagicWand);
        using var _ = h;
        input.Click(new Vec2(100, 150));
        Assert.Equal(["Magic Wand"], Steps(s));
        Assert.Equal(new PixelRect(0, 0, 200, 300), s.State.Selection!.Bounds(s.State.Bounds));
    }

    [Fact]
    public void MagicSelectAsksForItsModelFirst()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.MagicSelect);
        using var _ = h;
        using var models = new SharedModelStore(h);
        input.Click(new Vec2(100, 150));
        // Segment Anything isn't on this computer: the person is asked, nothing is downloaded or changed.
        Assert.Equal([Glimmer.AI.OnDeviceModel.SegmentAnything], s.AI.ModelRequest?.Models);
        Assert.Empty(s.AI.MagicPoints);
        Assert.Empty(s.Document.History);
    }

    [Fact]
    public void CloneStampCopiesFromTheSource()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.CloneStamp);
        using var _ = h;
        // Without a source it says how to set one.
        input.Drag(new Vec2(300, 150), new Vec2(310, 150));
        Assert.Equal("Alt-click to set the clone source first.", s.ErrorMessage?.ToString());
        s.ErrorMessage = null;
        input.Click(new Vec2(100, 150), InputModifiers.Option);
        Assert.Equal(new Vec2(100, 150), s.CloneSource);
        Assert.Equal(new Vec2(100, 150), s.CloneMarker(new Vec2(300, 150)));
        input.Drag(new Vec2(300, 150), new Vec2(320, 150));
        Assert.Equal(["Clone Stamp"], Steps(s));
        Assert.True(Docs.Pixel(s.State.Layers[0], 310, 150).X < 0.05);
        // Aligned: the marker follows the brush at the same offset.
        Assert.Equal(new Vec2(150, 200), s.CloneMarker(new Vec2(350, 200)));
    }

    [Fact]
    public void HealingBrushNeedsASourceAndRecordsAStep()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.HealingBrush);
        using var _ = h;
        input.Click(new Vec2(300, 150));
        Assert.Equal("Alt-click to set the healing source first.", s.ErrorMessage?.ToString());
        s.ErrorMessage = null;
        input.Click(new Vec2(320, 60), InputModifiers.Option);
        input.Drag(new Vec2(300, 150), new Vec2(320, 150));
        Assert.Equal(["Healing Brush"], Steps(s));
    }

    [Theory]
    [InlineData(ToolID.Dodge, true)]
    [InlineData(ToolID.Burn, false)]
    public void DodgeLightensAndBurnDarkens(ToolID tool, bool lighter)
    {
        var (h, s, input) = Start(Docs.Gray(0.3f), tool);
        using var _ = h;
        input.Drag(new Vec2(150, 150), new Vec2(250, 150));
        Assert.Equal([tool == ToolID.Dodge ? "Dodge" : "Burn"], Steps(s));
        float v = Docs.Pixel(s.State.Layers[0], 200, 150).X;
        Assert.True(lighter ? v > 0.31 : v < 0.29, $"{v}");
    }

    [Fact]
    public void BlurSoftensAnEdge()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Blur);
        using var _ = h;
        s.RetouchStrength = 1;
        input.Drag(new Vec2(200, 100), new Vec2(200, 200));
        Assert.Equal(["Blur Tool"], Steps(s));
        Assert.True(Docs.Pixel(s.State.Layers[0], 198, 150).X > 0.05);
        Assert.True(Docs.Pixel(s.State.Layers[0], 201, 150).X < 0.95);
    }

    [Theory]
    [InlineData(ToolID.Sharpen, "Sharpen Tool")]
    [InlineData(ToolID.PatternStamp, "Pattern Stamp")]
    public void RetouchStrokesAreOneStepEach(ToolID tool, string step)
    {
        var (h, s, input) = Start(Docs.TwoTone(), tool);
        using var _ = h;
        input.Drag(new Vec2(150, 100), new Vec2(250, 100));
        input.Drag(new Vec2(150, 200), new Vec2(250, 200));
        Assert.Equal([step, step], Steps(s));
    }

    [Fact]
    public void SmudgeDragsColourAlong()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Smudge);
        using var _ = h;
        s.RetouchStrength = 1;
        input.Drag(new Vec2(170, 150), new Vec2(240, 150), steps: 20);
        Assert.Equal(["Smudge"], Steps(s));
        Assert.True(Docs.Pixel(s.State.Layers[0], 215, 150).X < 0.95);
    }

    [Fact]
    public void RetouchToolsRefuseNonPixelLayers()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Blur);
        using var _ = h;
        s.Perform(CommandID.NewSolidFill);
        input.Drag(new Vec2(150, 100), new Vec2(250, 100));
        Assert.Equal("Retouch tools work on pixel layers.", s.ErrorMessage?.ToString());
    }

    [Fact]
    public void PenDrawsAPathAndClosesItAtTheFirstAnchor()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Pen);
        using var _ = h;
        input.Click(new Vec2(100, 100));
        input.Drag(new Vec2(300, 100), new Vec2(330, 130));   // a smooth anchor
        input.Click(new Vec2(300, 250));
        var path = Assert.Single(s.State.Paths);
        Assert.Equal(path.Id, s.ActivePathID);
        var anchors = Assert.Single(path.Subpaths).Anchors;
        Assert.Equal(3, anchors.Length);
        Assert.Equal(new Vec2(330, 130), anchors[1].HandleOut);
        Assert.Equal(new Vec2(270, 70), anchors[1].HandleIn);
        input.Click(new Vec2(100, 100));
        Assert.True(s.State.Paths[0].Subpaths[0].IsClosed);
        // The path becomes a selection.
        s.MakeSelectionFromPath(path.Id);
        Assert.True(s.State.Selection!.Values(new PixelRect(250, 150, 1, 1))[0] > 0.99);
        // Undo takes the steps back one at a time, and the first one removes the path.
        while (s.Document.CanUndo) s.Document.Undo();
        Assert.Empty(s.State.Paths);
    }

    [Fact]
    public void ShapeToolAddsAShapeLayer()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Shape);
        using var _ = h;
        s.ShapeKind = ShapeKind.Ellipse;
        s.Foreground = new SRGBColor(0, 1, 0);
        input.Drag(new Vec2(50, 50), new Vec2(150, 120));
        Assert.Equal(["New Ellipse"], Steps(s));
        var layer = s.ActiveLayer!;
        Assert.NotNull(layer.VectorMask);
        Assert.Equal(new SRGBColor(0, 1, 0), Assert.IsType<Fill.Solid>(Assert.IsType<FillContent>(layer.Content).Fill).Color);
        foreach (var kind in Enum.GetValues<ShapeKind>())
        {
            s.ShapeKind = kind;
            input.Drag(new Vec2(200, 200), new Vec2(260, 240));
        }
        Assert.Equal(1 + Enum.GetValues<ShapeKind>().Length, s.Document.History.Count);
        // A click without a drag makes nothing.
        s.ShapeKind = ShapeKind.Rectangle;
        input.Click(new Vec2(20, 20));
        Assert.Equal(1 + Enum.GetValues<ShapeKind>().Length, s.Document.History.Count);
    }

    [Fact]
    public void HandPansTheView()
    {
        var (h, s, input) = Start(Docs.TwoTone(), ToolID.Hand);
        using var _ = h;
        s.Canvas.ViewDidResize(new Vec2(1000, 800));
        var before = s.Canvas.Transform;
        input.Drag(new Vec2(100, 100), new Vec2(140, 130));
        Assert.Equal(before.Translation + new Vec2(40, 30), s.Canvas.Transform.Translation);
        Assert.Empty(s.Document.History);
        // Space held over another tool does the same.
        s.Tool = ToolID.Brush;
        s.TemporaryHand = true;
        Assert.Equal(ToolID.Hand, s.EffectiveTool);
    }

    [Fact]
    public void LiquifyPreviewsThenCommitsOneStep()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        var input = new CanvasInput(s, h.Scheduler);
        Assert.True(s.CanPerform(CommandID.Liquify));
        s.Perform(CommandID.Liquify);
        Assert.NotNull(s.Liquify);
        input.Drag(new Vec2(180, 150), new Vec2(240, 150), steps: 12);
        Assert.False(s.Overrides.Pixels.IsEmpty);
        Assert.Empty(s.Document.History);
        s.CommitLiquify();
        Assert.Null(s.Liquify);
        Assert.Equal(["Liquify"], Steps(s));
        Assert.True(Docs.Pixel(s.State.Layers[0], 215, 150).X < 0.95);

        s.Perform(CommandID.Liquify);
        input.Drag(new Vec2(180, 150), new Vec2(240, 150));
        s.CancelLiquify();
        Assert.Single(s.Document.History);
        Assert.True(s.Overrides.Pixels.IsEmpty);
    }

    [Fact]
    public void BrushSizeKeys()
    {
        using var h = new Harness();
        var s = h.NewSession();
        s.Brush = s.Brush with { Size = 30 };
        s.AdjustBrushSize(1.5);
        Assert.Equal(45, s.Brush.Size);
        s.Brush = s.Brush with { Size = 1 };
        s.AdjustBrushSize(0.5);
        Assert.Equal(1, s.Brush.Size);
        s.Brush = s.Brush with { Size = 1900 };
        s.AdjustBrushSize(2);
        Assert.Equal(2000, s.Brush.Size);
    }

    [Fact]
    public void ColoursSwapAndReset()
    {
        using var h = new Harness();
        var s = h.NewSession();
        s.Foreground = new SRGBColor(1, 0, 0);
        s.ForegroundOpacity = 0.5;
        s.SwapColors();
        Assert.Equal(new SRGBColor(1, 1, 1), s.Foreground);
        Assert.Equal(new SRGBColor(1, 0, 0), s.Background);
        Assert.Equal(0.5, s.BackgroundOpacity);
        s.ResetColors();
        Assert.Equal((new SRGBColor(0, 0, 0), new SRGBColor(1, 1, 1), 1.0, 1.0),
                     (s.Foreground, s.Background, s.ForegroundOpacity, s.BackgroundOpacity));
    }

    [Fact]
    public void GuidesAreAddedMovedAndSnappedTo()
    {
        using var h = new Harness();
        var s = h.NewSession();
        s.AddGuide(GuideAxis.Vertical, 100);
        var guide = Assert.Single(s.State.Aids.Guides);
        // The canvas moves a guide once, when the drag ends.
        s.MoveGuide(guide.Id, 120, live: false);
        // A live move (a field being edited) is one step once it goes quiet.
        s.MoveGuide(guide.Id, 150, live: true);
        s.MoveGuide(guide.Id, 170, live: true);
        Assert.Equal(2, s.Document.History.Count);
        h.Scheduler.Settle();
        Assert.Equal(170, s.State.Aids.Guides[0].Position);
        Assert.Equal(["New Guide", "Move Guide", "Move Guide"], Steps(s));
        s.Document.Undo();
        Assert.Equal(120, s.State.Aids.Guides[0].Position);
        s.Document.Redo();
        // Snap is on by default in new documents? Either way: on, a point near the guide lands on it.
        if (!s.State.Aids.Snap) s.Perform(CommandID.ToggleSnap);
        Assert.Equal(170, s.Snapped(new Vec2(172, 300)).X);
        s.RemoveGuide(guide.Id);
        Assert.Empty(s.State.Aids.Guides);
    }
}
