// Tests for the port of the selection commands of App/EditorSession.swift and EditorSession+Tools.swift (Select menu,
// Feather/Expand/Contract, copy, cut, paste, clear), App/EditorSession+Channels.swift, and the Windows-only selection
// outline (SelectionOutline.cs) the canvas draws the marching ants from. The original has no tests at this level.
using Glimmer.Core;

namespace Glimmer.Editor.Tests;

public class SelectionTests
{
    private static Selection Rect(double x0, double y0, double x1, double y1) => Selection.Rectangle(new CGRectLike(x0, y0, x1, y1));

    private static void Select(EditorSession s, Selection selection) => s.Edit("Select", st => st with { Selection = selection });

    [Fact]
    public void SelectAllDeselectAndInverse()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        Assert.False(s.CanPerform(CommandID.Deselect));
        Assert.False(s.CanPerform(CommandID.InvertSelection));
        s.Perform(CommandID.SelectAll);
        Assert.Equal(new PixelRect(0, 0, 400, 300), s.State.Selection!.Bounds(s.State.Bounds));
        Assert.True(s.CanPerform(CommandID.Deselect));
        s.Perform(CommandID.Deselect);
        Assert.Null(s.State.Selection);
        Select(s, Rect(0, 0, 100, 300));
        s.Perform(CommandID.InvertSelection);
        Assert.Equal([0f, 1f], s.State.Selection!.Values(new PixelRect(99, 150, 2, 1)));
        Assert.Equal(["Select All", "Deselect", "Select", "Select Inverse"], s.Document.History.Select(t => t.ToString()));
    }

    [Fact]
    public void ModifyOpensItsSheetThenChangesTheSelection()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        Select(s, Rect(100, 100, 200, 200));
        s.Perform(CommandID.GrowSelection);
        Assert.Equal(CommandID.GrowSelection, s.SelectionDialog);
        s.ModifySelection(CommandID.GrowSelection, 10);
        var grown = s.State.Selection!.Bounds(s.State.Bounds)!.Value;
        Assert.True(grown.X <= 91 && grown.MaxX >= 209, $"{grown}");
        s.ModifySelection(CommandID.ShrinkSelection, 20);
        var shrunk = s.State.Selection!.Bounds(s.State.Bounds)!.Value;
        Assert.True(shrunk.X >= 105 && shrunk.MaxX <= 195, $"{shrunk}");
        s.ModifySelection(CommandID.FeatherSelection, 5);
        Assert.Equal(["Select", "Expand Selection", "Contract Selection", "Feather"], s.Document.History.Select(t => t.ToString()));
        // Nothing to modify, or no radius: nothing happens.
        s.ModifySelection(CommandID.FeatherSelection, 0);
        s.Perform(CommandID.Deselect);
        s.ModifySelection(CommandID.GrowSelection, 5);
        Assert.Equal(5, s.Document.History.Count);
    }

    [Fact]
    public void CopyAndPasteInPlace()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        Assert.False(s.CanPerform(WindowsCommands.Paste));
        Select(s, Rect(150, 100, 250, 200));
        s.Perform(CommandID.Copy);
        // Other apps get a picture of the copied area.
        Assert.Equal((100, 100), (h.Clipboard.LastImage!.Width, h.Clipboard.LastImage.Height));
        Assert.True(s.CanPerform(WindowsCommands.Paste));
        s.Perform(WindowsCommands.Paste);
        var pasted = s.ActiveLayer!;
        Assert.Equal("Pasted Layer", pasted.Name);
        Assert.Equal(Docs.BlackPixel, Docs.Pixel(pasted, 160, 150));
        Assert.Equal(Docs.WhitePixel, Docs.Pixel(pasted, 240, 150));
        Assert.Equal(0, Docs.Pixel(pasted, 100, 150).W);
    }

    [Fact]
    public void PasteTakesAnotherAppsImageOnceTheClipboardChanged()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.Perform(CommandID.SelectAll);
        s.Perform(CommandID.Copy);
        // Something else was copied: a 4 × 2 red PNG.
        using var bitmap = new SkiaSharp.SKBitmap(4, 2);
        bitmap.Erase(new SkiaSharp.SKColor(255, 0, 0));
        using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        h.Clipboard.ChangedElsewhere(png.ToArray());
        Assert.True(s.Paste());
        var layer = s.ActiveLayer!;
        Assert.Equal("Pasted Layer", layer.Name);
        // Placed in the middle of the document.
        var p = Docs.Pixel(layer, 199, 150);
        Assert.True(p.X > 0.99 && p.Y < 0.01, $"{p}");
        h.Clipboard.ChangedElsewhere();
        Assert.False(s.Paste());
    }

    [Fact]
    public void CutAndClearRemovePixels()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        Assert.False(s.CanPerform(CommandID.ClearPixels));
        Select(s, Rect(0, 0, 100, 100));
        s.Perform(CommandID.ClearPixels);
        Assert.Equal(0, Docs.Pixel(s.State.Layers[0], 50, 50).W);
        Select(s, Rect(300, 200, 400, 300));
        s.Perform(CommandID.Cut);
        Assert.Equal(0, Docs.Pixel(s.State.Layers[0], 350, 250).W);
        Assert.Equal(["Select", "Clear", "Select", "Cut"], s.Document.History.Select(t => t.ToString()));
        // Without a selection, cut takes the whole layer.
        s.Perform(CommandID.Deselect);
        s.Perform(CommandID.Cut);
        Assert.True(s.State.Layers[0].Tiles.IsEmpty);
    }

    [Fact]
    public void LayerViaCopyCopiesTheSelectedPixels()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        Select(s, Rect(150, 0, 250, 300));
        s.Perform(CommandID.LayerViaCopy);
        var layer = s.ActiveLayer!;
        Assert.Equal("Layer 1", layer.Name);
        Assert.Equal(Docs.BlackPixel, Docs.Pixel(layer, 160, 10));
        Assert.Equal(0, Docs.Pixel(layer, 100, 10).W);
        // Without a selection it duplicates the layer.
        s.Perform(CommandID.Deselect);
        s.Perform(CommandID.LayerViaCopy);
        Assert.Equal(3, s.State.AllLayers.Length);
        Assert.Equal("Duplicate Layer", s.Document.History[^1].ToString());
    }

    [Fact]
    public void FillCommandsFillTheSelection()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        Select(s, Rect(250, 50, 300, 100));
        s.Foreground = new SRGBColor(1, 0, 0);
        s.Perform(CommandID.FillForeground);
        Assert.True(Docs.Pixel(s.State.Layers[0], 260, 60).Y < 0.01);
        Assert.Equal(Docs.WhitePixel, Docs.Pixel(s.State.Layers[0], 320, 60));
        s.Perform(CommandID.FillBackground);
        Assert.Equal(Docs.WhitePixel, Docs.Pixel(s.State.Layers[0], 260, 60));
        s.Perform(CommandID.FillPattern);
        Assert.Equal(["Select", "Fill", "Fill", "Pattern Fill"], s.Document.History.Select(t => t.ToString()));
    }

    [Fact]
    public void ChannelsLoadAsSelections()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        // Luminosity: the white half is selected, the black half not.
        s.LoadChannelAsSelection(0);
        var sel = s.State.Selection!;
        Assert.True(sel.Values(new PixelRect(300, 100, 1, 1))[0] > 0.99);
        Assert.True(sel.Values(new PixelRect(100, 100, 1, 1))[0] < 0.01);
        // Values are 0…1, never the 0…255 of the picture they come from (the Mac's selection went up to 65025).
        Assert.True(sel.Values(s.State.Bounds).Max() <= 1);
        // Alpha: everything is opaque.
        s.LoadChannelAsSelection(4);
        Assert.True(s.State.Selection!.Values(new PixelRect(100, 100, 1, 1))[0] > 0.99);
        Assert.Equal(["Load Channel", "Load Channel"], s.Document.History.Select(t => t.ToString()));
    }

    [Fact]
    public void ChannelMaskIsAViewSettingOnly()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.ChannelMask = 1;
        Assert.Equal(1, s.ChannelMask);
        Assert.Empty(s.Document.History);
    }

    [Fact]
    public void OutlineFollowsTheSelection()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        Assert.Empty(s.SelectionOutline);
        Select(s, Rect(100, 50, 300, 150));
        h.Scheduler.RunPosted();
        var ring = Assert.Single(s.SelectionOutline);
        Assert.InRange(ring.Min(p => p.X), 99, 101);
        Assert.InRange(ring.Max(p => p.X), 299, 301);
        Assert.InRange(ring.Min(p => p.Y), 49, 51);
        Assert.InRange(ring.Max(p => p.Y), 149, 151);
        s.Perform(CommandID.Deselect);
        h.Scheduler.RunPosted();
        Assert.Empty(s.SelectionOutline);
    }

    [Fact]
    public void SelectAllHasAntsAroundTheCanvas()
    {
        var outline = SelectionOutlineTracer.Trace(Selection.All(), new PixelRect(0, 0, 400, 300), 1);
        var ring = Assert.Single(outline);
        Assert.InRange(ring.Min(p => p.X), -1, 1);
        Assert.InRange(ring.Max(p => p.X), 399, 401);
    }

    [Fact]
    public void TwoSeparateAreasAreTwoRingsAndAHoleIsARing()
    {
        var two = Rect(10, 10, 50, 50).Combined(Rect(100, 10, 150, 50), Selection.Operation.Add);
        Assert.Equal(2, SelectionOutlineTracer.Trace(two, new PixelRect(0, 0, 200, 100), 1).Count);
        var frame = Rect(10, 10, 90, 90).Combined(Rect(30, 30, 70, 70), Selection.Operation.Subtract);
        Assert.Equal(2, SelectionOutlineTracer.Trace(frame, new PixelRect(0, 0, 100, 100), 1).Count);
        Assert.Empty(SelectionOutlineTracer.Trace(new Selection(), new PixelRect(0, 0, 100, 100), 1));
    }

    [Fact]
    public void OutlineIsCoarserZoomedOut()
    {
        Assert.Equal(1, SelectionOutlineTracer.StepFor(1, new PixelRect(0, 0, 2000, 1500)));
        Assert.Equal(1, SelectionOutlineTracer.StepFor(4, new PixelRect(0, 0, 2000, 1500)));
        Assert.Equal(4, SelectionOutlineTracer.StepFor(0.25, new PixelRect(0, 0, 2000, 1500)));
        // Never more than the node budget (4 million), whatever the zoom.
        Assert.Equal(2, SelectionOutlineTracer.StepFor(1, new PixelRect(0, 0, 4000, 3000)));
        Assert.True(SelectionOutlineTracer.StepFor(8, new PixelRect(0, 0, 30000, 30000)) >= 8);
        var fine = SelectionOutlineTracer.Trace(Selection.Ellipse(new CGRectLike(0, 0, 1000, 800)), new PixelRect(0, 0, 1000, 800), 1);
        var coarse = SelectionOutlineTracer.Trace(Selection.Ellipse(new CGRectLike(0, 0, 1000, 800)), new PixelRect(0, 0, 1000, 800), 8);
        Assert.True(coarse[0].Count * 4 < fine[0].Count, $"{coarse[0].Count} vs {fine[0].Count}");
    }

    [Fact]
    public void ZoomingOutRetracesTheOutline()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.White(4000, 3000));
        s.Canvas.ViewDidResize(new Vec2(4000, 3000));
        h.Scheduler.RunPosted();
        s.Perform(CommandID.SelectAll);
        Select(s, Selection.Ellipse(new CGRectLike(0, 0, 4000, 3000)));
        h.Scheduler.RunPosted();
        int before = s.SelectionOutline[0].Count;
        s.Canvas.ZoomBy(0.1, new Vec2(0, 0));
        h.Scheduler.RunPosted();
        Assert.True(s.SelectionOutline[0].Count < before, $"{s.SelectionOutline[0].Count} vs {before}");
    }
}
