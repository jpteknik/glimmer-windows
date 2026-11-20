// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/MaskTransformTests.swift
using Glimmer.Core;

namespace Glimmer.Render.Tests;

/// <summary>A layer mask follows its layer through Free Transform.</summary>
public class MaskTransformTests
{
    private static float Value(TileGrid grid, int x, int y, float defaultValue)
    {
        var coord = new TileCoord(PixelRect.FloorDiv(x, TileGeometry.Size), PixelRect.FloorDiv(y, TileGeometry.Size));
        if (grid[coord] is not { } tile) return defaultValue;
        return tile.PixelAt(x - coord.X * TileGeometry.Size, y - coord.Y * TileGeometry.Size).W;
    }

    private static readonly PixelRect Limit = new(-512, -512, 2048, 2048);

    /// <summary>Hide-all mask revealing a rectangle 100…200 × 100…160.</summary>
    private static LayerMask HideAll => Selection.Rectangle(new CGRectLike(100, 100, 200, 160)).AsMask;

    /// <summary>Reveal-all mask hiding the same rectangle.</summary>
    private static LayerMask RevealAll => Selection.Rectangle(new CGRectLike(100, 100, 200, 160)).Inverted().AsMask;

    [Fact]
    public void AHideAllMaskScalesAndMovesWithTheLayer()
    {
        var mask = HideAll;
        Assert.Equal(0, mask.DefaultValue);
        // ×2 about the origin, then 300 right: the rectangle becomes 500…700 × 200…320.
        var t = new AffineTransform(a: 2, d: 2, tx: 300);
        var output = MaskTransform.Apply(mask, t, Limit);
        Assert.True(Value(output, 600, 260, 0) > 0.99f);
        Assert.True(Value(output, 510, 210, 0) > 0.99f && Value(output, 690, 310, 0) > 0.99f);
        Assert.True(Value(output, 490, 260, 0) < 0.01f && Value(output, 600, 330, 0) < 0.01f);
        Assert.True(Value(output, 150, 130, 0) < 0.01f, "nothing is left where the rectangle was");
    }

    [Fact]
    public void ARevealAllMaskStaysWhiteOutsideWhatMoved()
    {
        var mask = RevealAll;
        Assert.Equal(1, mask.DefaultValue);
        // Half size about the origin: the hidden rectangle becomes 50…100 × 50…80, well inside one tile, so the rest of
        // that tile must be white, not left black by the resampling.
        var t = new AffineTransform(a: 0.5, d: 0.5);
        var output = MaskTransform.Apply(mask, t, Limit);
        Assert.True(Value(output, 75, 65, 1) < 0.01f, "the hidden part moved");
        Assert.True(Value(output, 150, 130, 1) > 0.99f, "where it was is revealed again");
        Assert.True(Value(output, 200, 200, 1) > 0.99f && Value(output, 10, 10, 1) > 0.99f);
        Assert.True(Value(output, 400, 400, 1) > 0.99f && Value(output, -100, 50, 1) > 0.99f);
    }

    [Fact]
    public void AQuarterTurnTurnsTheMask()
    {
        // (x, y) → (400 − y, x): the rectangle 100…200 × 100…160 becomes 240…300 × 100…200.
        var t = new AffineTransform(a: 0, b: 1, c: -1, d: 0, tx: 400);
        foreach (var mask in new[] { HideAll, RevealAll })
        {
            float inside = mask.DefaultValue == 0 ? 1 : 0, d = mask.DefaultValue;
            var output = MaskTransform.Apply(mask, t, Limit);
            Assert.True(MathF.Abs(Value(output, 270, 150, d) - inside) < 0.01f);
            Assert.True(MathF.Abs(Value(output, 250, 110, d) - inside) < 0.01f && MathF.Abs(Value(output, 290, 190, d) - inside) < 0.01f);
            Assert.True(MathF.Abs(Value(output, 230, 150, d) - d) < 0.01f && MathF.Abs(Value(output, 270, 210, d) - d) < 0.01f);
            Assert.True(MathF.Abs(Value(output, 150, 130, d) - d) < 0.01f);
        }
    }

    [Fact]
    public void ThePreviewAgreesWhereTheLayerHasContent()
    {
        var mask = RevealAll;
        var t = new AffineTransform(a: 0.5, d: 0.5);
        var cover = new PixelRect(0, 0, 600, 600);  // the layer's pixels
        var quick = MaskTransform.Apply(mask, t, Limit, exact: false, cover: cover);
        var exact = MaskTransform.Apply(mask, t, Limit);
        foreach (var (x, y) in new[] { (75, 65), (150, 130), (10, 10), (250, 250), (290, 20) })  // inside the moved content, 0…300
            Assert.True(MathF.Abs(Value(quick, x, y, 1) - Value(exact, x, y, 1)) < 0.01f, $"{x},{y}");
    }

    [Fact]
    public void AMaskWithoutTilesIsLeftAlone()
    {
        var output = MaskTransform.Apply(LayerMask.RevealAll, new AffineTransform(a: 2, d: 2), Limit);
        Assert.True(output.IsEmpty);
    }
}
