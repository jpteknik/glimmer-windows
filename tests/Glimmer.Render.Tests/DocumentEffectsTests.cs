// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/DocumentEffectsTests.swift
using System.Numerics;
using Glimmer.Core;

namespace Glimmer.Render.Tests;

public class DocumentEffectsTests
{
    /// <summary>Left half white, right half black.</summary>
    private static DocumentState Split(int w, int h)
    {
        var grid = TileGrid.Solid(new Vector4(0, 0, 0, 1), new PixelRect(0, 0, w, h)).ToBuilder();
        foreach (var (c, t) in TileGrid.Solid(new Vector4(1, 1, 1, 1), new PixelRect(0, 0, w / 2, h))) grid[c] = t;
        return new DocumentState(w, h, layers: [new Layer("s", new RasterContent(grid.ToImmutable()))]);
    }

    private static Vector4 Pixel(TileGrid grid, int x, int y) => grid[new TileCoord(x / 256, y / 256)]!.PixelAt(x % 256, y % 256);

    [Fact]
    public void InFocusStaysSharpOutOfFocusBlurs()
    {
        var state = Split(512, 256);
        var sharp = DocumentEffects.LensBlur(state, Enumerable.Repeat(0.8f, 512 * 256).ToArray(),
                                             new DocumentEffects.LensBlurParameters(Focus: 0.8, Amount: 0.03));
        Assert.True(Pixel(sharp, 254, 100).X > 0.99f && Pixel(sharp, 258, 100).X < 0.01f);
        var blurred = DocumentEffects.LensBlur(state, new float[512 * 256],
                                               new DocumentEffects.LensBlurParameters(Focus: 1, Amount: 0.03, Bokeh: 0));
        float edge = Pixel(blurred, 256, 100).X;
        Assert.True(edge > 0.2f && edge < 0.8f, $"edge {edge}");
        Assert.True(Pixel(blurred, 50, 100).X > 0.99f);
    }

    [Fact]
    public void UpscaleDoublesTheDocument()
    {
        var grid = DocumentEffects.Upscale(Split(512, 100), scale: 2);
        Assert.Equal(new PixelRect(0, 0, 1024, 200).TileCoords.ToHashSet(), grid.Coords.ToHashSet());
        Assert.True(Pixel(grid, 100, 50).X > 0.99f);
        Assert.True(Pixel(grid, 900, 50).X < 0.01f);
        Assert.True(Pixel(grid, 1023, 199).W > 0.99f);
        Assert.Equal(0, Pixel(grid, 100, 210).W);  // below the new size: cleared
    }
}
