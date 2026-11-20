// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/CanvasStepsTests.swift
// (theOperationsListHasTheNewOnes is in AgentSupportTests.cs, with the other tests of the real operation list.)
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

public class CanvasStepsTests
{
    static readonly PixelRect Canvas = new(0, 0, 1600, 1000);
    internal static AgentStep Step(string op, params (string, AgentValue)[] p) => new(op, p);

    static string? Reason(Action body)
    {
        try { body(); }
        catch (CanvasSteps.Refusal r) { return r.Reason; }
        catch { return "other"; }
        return null;
    }

    [Fact]
    public void CropFormsExcludeEachOther()
    {
        Assert.Null(CanvasSteps.Malformed(Step("crop", ("width", 10), ("height", 10))));
        Assert.Null(CanvasSteps.Malformed(Step("crop", ("aspect", "1:1"))));
        Assert.Null(CanvasSteps.Malformed(Step("crop", ("toSelection", true))));
        Assert.NotNull(CanvasSteps.Malformed(Step("crop", ("aspect", "1:1"), ("width", 10), ("height", 10))));
        Assert.NotNull(CanvasSteps.Malformed(Step("crop", ("aspect", "1:1"), ("toSelection", true))));
        Assert.NotNull(CanvasSteps.Malformed(Step("crop")));
        Assert.NotNull(CanvasSteps.Malformed(Step("crop", ("width", 10))));   // a rectangle needs both sides
        Assert.Null(CanvasSteps.Malformed(Step("crop", ("toSelection", false), ("aspect", "1:1"))));
    }

    [Fact]
    public void ARectangleIsClippedToTheCanvas()
    {
        var box = CanvasSteps.CropBox(Step("crop", ("x", -100), ("y", 900), ("width", 500), ("height", 500)), Canvas, null);
        Assert.Equal(new PixelRect(0, 900, 400, 100), box);
        Assert.Equal("bad_rect", Reason(() => CanvasSteps.CropBox(Step("crop", ("x", 2000), ("width", 50), ("height", 50)), Canvas, null)));
        Assert.Equal("bad_rect", Reason(() => CanvasSteps.Rect(Step("select_rect", ("width", 0), ("height", 50)), Canvas)));
    }

    [Fact]
    public void AspectGivesTheLargestCentredBox()
    {
        var wide = CanvasSteps.CropBox(Step("crop", ("aspect", "16:9")), Canvas, null);
        Assert.Equal(new PixelRect(0, 50, 1600, 900), wide);
        var square = CanvasSteps.CropBox(Step("crop", ("aspect", "1:1")), Canvas, null);
        Assert.Equal(new PixelRect(300, 0, 1000, 1000), square);
        foreach (var bad in new[] { "16", "16:0", "-4:3", "a:b", "16:9:1", "" })
            Assert.Equal("bad_aspect", Reason(() => CanvasSteps.CropBox(Step("crop", ("aspect", bad)), Canvas, null)));
        Assert.Equal(4.0 / 3, CanvasSteps.Aspect(" 4 : 3 "));
    }

    [Fact]
    public void CropToSelectionNeedsOne()
    {
        var sel = new PixelRect(100, 100, 300, 200);
        Assert.Equal(sel, CanvasSteps.CropBox(Step("crop", ("toSelection", true)), Canvas, sel));
        Assert.Equal("no_selection", Reason(() => CanvasSteps.CropBox(Step("crop", ("toSelection", true)), Canvas, null)));
    }

    [Fact]
    public void ResizeKeepsProportionsUnlessBothSidesAreGiven()
    {
        Assert.NotNull(CanvasSteps.Malformed(Step("resize_image", ("scale", 0.5), ("width", 10))));
        Assert.NotNull(CanvasSteps.Malformed(Step("resize_image")));
        Assert.Equal((800, 500), CanvasSteps.NewSize(Step("resize_image", ("width", 800)), 1600, 1000));
        Assert.Equal((400, 250), CanvasSteps.NewSize(Step("resize_image", ("height", 250)), 1600, 1000));
        Assert.Equal((300, 300), CanvasSteps.NewSize(Step("resize_image", ("width", 300), ("height", 300)), 1600, 1000));
        Assert.Equal((3200, 2000), CanvasSteps.NewSize(Step("resize_image", ("scale", 2)), 1600, 1000));
        Assert.Equal("bad_size", Reason(() => CanvasSteps.NewSize(Step("resize_image", ("scale", 0)), 1600, 1000)));
        Assert.Equal("bad_size", Reason(() => CanvasSteps.NewSize(Step("resize_image", ("width", 40_000)), 1600, 1000)));
    }

    [Fact]
    public void SetLayerChecksItsValues()
    {
        var c = CanvasSteps.LayerChangeOf(Step("set_layer", ("opacity", 0.5), ("blendMode", "soft-light"), ("visible", false), ("name", " Sky ")), isGroup: false);
        Assert.Equal(new CanvasSteps.LayerChange(Opacity: 0.5, BlendMode: BlendMode.SoftLight, Visible: false, Name: "Sky"), c);
        Assert.Equal("bad_opacity", Reason(() => CanvasSteps.LayerChangeOf(Step("set_layer", ("opacity", 50)), false)));
        Assert.Equal("unknown_blend_mode", Reason(() => CanvasSteps.LayerChangeOf(Step("set_layer", ("blendMode", "glow")), false)));
        Assert.Equal("unknown_blend_mode", Reason(() => CanvasSteps.LayerChangeOf(Step("set_layer", ("blendMode", "pass-through")), false)));
        Assert.Equal(BlendMode.PassThrough, CanvasSteps.LayerChangeOf(Step("set_layer", ("blendMode", "pass-through")), true).BlendMode);
        Assert.Equal("nothing_to_set", Reason(() => CanvasSteps.LayerChangeOf(Step("set_layer"), false)));
    }

    [Fact]
    public void ATargetIsAnIdOrAnUnambiguousName()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var layers = new List<(Guid, string)> { (a, "Sky"), (b, "Title"), (c, "title") };
        Assert.Equal(a, CanvasSteps.Target("sky", layers));
        Assert.Equal(b, CanvasSteps.Target(b.ToString("D").ToLowerInvariant(), layers));
        var r = Assert.Throws<CanvasSteps.Refusal>(() => CanvasSteps.Target("Title", layers));
        Assert.True(r.Reason == "ambiguous_target" && r.Message.Contains(b.ToString("D").ToUpperInvariant()) && r.Message.Contains(c.ToString("D").ToUpperInvariant()));
        Assert.Equal("unknown_layer", Reason(() => CanvasSteps.Target("Nope", layers)));
    }

    [Fact]
    public void AnAddedImageFitsTheCanvas()
    {
        var big = CanvasSteps.Placement((4000, 2000), (1000, 1000), CanvasSteps.Fit.Contain, null, null);
        Assert.True(big.Width == 1000 && big.Height == 500 && big.X == 0 && big.Y == 250);
        var small = CanvasSteps.Placement((200, 100), (1000, 1000), CanvasSteps.Fit.Contain, null, null);
        Assert.True(small.Width == 200 && small.X == 400 && small.Y == 450, "contain never scales up");
        var cover = CanvasSteps.Placement((200, 100), (1000, 1000), CanvasSteps.Fit.Cover, null, null);
        Assert.True(cover.Width == 2000 && cover.Height == 1000 && cover.X == -500 && cover.Y == 0);
        var placed = CanvasSteps.Placement((200, 100), (1000, 1000), CanvasSteps.Fit.None, 10, 20);
        Assert.True(placed.X == 10 && placed.Y == 20 && placed.Width == 200);
    }
}

public class CanvasSizeTests
{
    static AgentStep Step(params (string, AgentValue)[] p) => new("resize_canvas", p);

    [Fact]
    public void ResizeCanvasTakesASizeOrAShape()
    {
        Assert.NotNull(CanvasSteps.Malformed(Step()));
        Assert.NotNull(CanvasSteps.Malformed(Step(("aspect", "16:9"), ("width", 10))));
        Assert.Null(CanvasSteps.Malformed(Step(("height", 10))));
        // One side alone keeps the other.
        Assert.Equal(new PixelRect(-100, 0, 1200, 600), CanvasSteps.CanvasBox(Step(("width", 1200)), 1000, 600));
        // The smallest 16:9 canvas that holds a square image, the image on the right.
        var wide = CanvasSteps.CanvasBox(Step(("aspect", "16:9"), ("anchor", "right")), 900, 900);
        Assert.Equal(new PixelRect(-700, 0, 1600, 900), wide);
        // A wide image made square grows in height.
        Assert.Equal(new PixelRect(0, 0, 1000, 1000), CanvasSteps.CanvasBox(Step(("aspect", "1:1"), ("anchor", "top")), 1000, 600));
        static string? Reason(params (string, AgentValue)[] p)
        {
            try { CanvasSteps.CanvasBox(Step(p), 1000, 600); } catch (CanvasSteps.Refusal r) { return r.Reason; } catch { return "other"; }
            return null;
        }
        Assert.True(Reason(("width", 0)) == "bad_size" && Reason(("width", 99_999)) == "bad_size");
        Assert.Equal("bad_anchor", Reason(("width", 10), ("anchor", "middle")));
        Assert.Equal("bad_aspect", Reason(("aspect", "wide")));
    }

    [Fact]
    public void TheAnchorDecidesWhereTheNewRoomGoes()
    {
        var old = (Width: 1000, Height: 600);
        // Wider, anchored in the middle: the room is shared between left and right.
        Assert.Equal(new PixelRect(-100, 0, 1200, 600), CanvasSteps.CanvasBox(old, (1200, 600), (1, 1)));
        // Anchored top-left: everything is added to the right and below.
        Assert.Equal(new PixelRect(0, 0, 1200, 800), CanvasSteps.CanvasBox(old, (1200, 800), (0, 0)));
        // Anchored bottom-right, smaller: the top-left is cut away.
        Assert.Equal(new PixelRect(500, 300, 500, 300), CanvasSteps.CanvasBox(old, (500, 300), (2, 2)));
        Assert.Equal(-1, CanvasSteps.CanvasBox(old, (1001, 600), (1, 1)).X);
    }
}

public class ScaledEffectsTests
{
    [Fact]
    public void LayerStylesFollowTheImageSize()
    {
        var e = new LayerEffects { DropShadow = new ShadowEffect(), Stroke = new StrokeEffect() };
        var half = e.Scaled(0.5);
        Assert.True(half.DropShadow?.Distance == 5 && half.DropShadow?.Size == 5 && half.Stroke?.Width == 1.5);
        Assert.True(half.DropShadow?.Opacity == e.DropShadow?.Opacity && half.OuterGlow is null);
    }
}
