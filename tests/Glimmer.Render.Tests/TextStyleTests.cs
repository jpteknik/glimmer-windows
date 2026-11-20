// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/TextStyleTests.swift
using System.Text.Json;
using System.Text.Json.Nodes;
using Glimmer.Core;

namespace Glimmer.Render.Tests;

/// <summary>Single words styled differently within one text layer.</summary>
public class TextStyleTests
{
    private static readonly SRGBColor Red = new(1, 0, 0);

    private static TextLayer Text(string s = "Hello brave world") =>
        new(s, new Vec2(10, 10), fontName: "HelveticaNeue", size: 40, color: SRGBColor.Black);

    private static void AssertRuns(IEnumerable<TextStyleRun>? expected, TextLayer t) =>
        Assert.Equal(expected?.ToArray(), t.Styles?.ToArray());

    [Fact]
    public void StyleARangeAndReadItBack()
    {
        var t = Text().SetStyle(new TextRange(6, 5), fontName: "HelveticaNeue-Bold", color: Red);
        AssertRuns([new TextStyleRun(6, 5, FontName: "HelveticaNeue-Bold", Color: Red)], t);
        Assert.Equal(t.BaseStyle, t.StyleAt(0));
        Assert.Equal(new TextStyle("HelveticaNeue-Bold", 40, Red), t.StyleAt(8));
        Assert.Equal([new TextRange(0, 6), new TextRange(6, 5), new TextRange(11, 6)], t.ResolvedRuns().Select(r => r.Range).ToArray());
    }

    [Fact]
    public void OverlappingChangesSplitAndMergeRuns()
    {
        var t = Text().SetStyle(new TextRange(0, 11), color: Red).SetStyle(new TextRange(6, 5), size: 60);
        AssertRuns([new TextStyleRun(0, 6, Color: Red), new TextStyleRun(6, 5, Size: 60, Color: Red)], t);
        // Setting the layer's own values again removes the runs.
        t = t.SetStyle(new TextRange(0, 17), size: 40, color: SRGBColor.Black);
        Assert.True(t.Styles is null && !t.HasStyledRuns);
    }

    [Fact]
    public void AWholeLayerChangeWinsOverWords()
    {
        var t = Text().SetStyle(new TextRange(6, 5), size: 60, color: Red);
        var old = t;
        t = (t with { Color = new SRGBColor(0, 0, 1) }).DropStyles(old);
        AssertRuns([new TextStyleRun(6, 5, Size: 60)], t);  // the colour applies everywhere, the size stays
    }

    [Fact]
    public void EditingTheTextKeepsStylesOnTheirWords()
    {
        var t = Text().SetStyle(new TextRange(6, 5), color: Red);  // "brave"
        string old = t.String;
        t = (t with { String = "Oh, hello brave world!" }).CarryStyles(old);
        var red = t.ResolvedRuns().Where(r => r.Style.Color == Red).Select(r => t.String.Substring(r.Range.Location, r.Range.Length)).ToArray();
        Assert.Equal(["brave"], red);
    }

    [Fact]
    public void TheEditorsRunsAreStoredAsDifferences()
    {
        var t = Text();
        var baseStyle = t.BaseStyle;
        var bold = baseStyle with { FontName = "HelveticaNeue-Bold" };
        t = t.SetStyledText("One two", [(new TextRange(0, 4), baseStyle), (new TextRange(4, 3), bold)]);
        Assert.Equal("One two", t.String);
        AssertRuns([new TextStyleRun(4, 3, FontName: "HelveticaNeue-Bold")], t);
    }

    [Fact]
    public void OlderFilesWithoutStylesStillOpen()
    {
        var t = Text();
        var json = JsonNode.Parse(JsonSerializer.Serialize(t))!.AsObject();
        json.Remove("styles");
        var decoded = JsonSerializer.Deserialize<TextLayer>(json.ToJsonString())!;
        Assert.True(decoded == t && decoded.Styles is null);
        var styled = t.SetStyle(new TextRange(0, 5), size: 80);
        Assert.Equal(styled, JsonSerializer.Deserialize<TextLayer>(JsonSerializer.Serialize(styled)));
    }

    [Fact]
    public void ABiggerWordMakesItsLineTallerAndTheTextWider()
    {
        var t = Text();
        var plain = t.LayoutSize;
        var big = t.SetStyle(new TextRange(6, 5), size: 80).LayoutSize;
        Assert.True(big.Height > plain.Height + 30, $"{plain} → {big}");
        Assert.True(big.Width > plain.Width + 40, $"{plain} → {big}");
    }

    [Fact]
    public void EachWordIsDrawnInItsColour()
    {
        var t = new TextLayer("AAAA BBBB", new Vec2(0, 0), fontName: "HelveticaNeue-Bold", size: 60, color: SRGBColor.Black)
            .SetStyle(new TextRange(5, 4), color: Red);
        static (int Left, int Right) Colours(TextLayer t)
        {
            var image = t.Render();
            Assert.NotNull(image);
            int w = image.Width, h = image.Height, redLeft = 0, redRight = 0;
            var px = image.Pixels;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (px[i + 3] <= 200 || px[i] <= 200 || px[i + 1] >= 60) continue;
                    if (x < w / 2) redLeft++; else redRight++;
                }
            }
            return (redLeft, redRight);
        }
        var flat = Colours(t);
        Assert.True(flat.Left == 0 && flat.Right > 200, $"only the second word is red: {flat}");
        // The same through the warp, which fills outlines itself.
        var warped = Colours(t with { Warp = new TextWarp(TextWarpStyle.Arc, Bend: 0.3) });
        Assert.True(warped.Left == 0 && warped.Right > 200, $"{warped}");
    }

    /// <summary>
    /// The Mac checks the fonts of the attributed string Core Text lays out; here the layout's glyph runs carry the
    /// per-word sizes.
    /// </summary>
    [Fact]
    public void LayoutRunsCarryTheSizes()
    {
        var t = Text().SetStyle(new TextRange(6, 5), size: 60);
        var layout = TextLayout.Build(t, null);
        var sizes = layout.Lines.SelectMany(l => l.Runs).Select(r => r.Size).ToArray();
        Assert.Equal(40f, sizes[0]);
        Assert.Contains(60f, sizes);
    }

    /// <summary>The compositor caches a text layer's tiles by its value: an equal layer reuses them, a changed one re-renders.</summary>
    [Fact]
    public void CompositorRendersAndCachesTextLayers()
    {
        var text = Text("Glimmer") with { Color = new SRGBColor(1, 0, 0) };
        var layer = new Layer("T", new TextContent(text));
        var state = new DocumentState(256, 128, layers: [layer]);
        var compositor = new DocumentCompositor();
        compositor.Update(state, null, [new TileCoord(0, 0)]);
        var first = compositor.Texture(new TileCoord(0, 0));
        Assert.NotNull(first);
        Assert.True(Enumerable.Range(0, 256 * 128).Any(i => first.Read(i % 256, i / 256).X > 0.9f), "red text was drawn");
        // An equal copy of the layer value: nothing is stale.
        compositor.Update(state with { Layers = [layer with { Content = new TextContent(text with { }) }] }, null, [new TileCoord(0, 0)]);
        Assert.Same(first, compositor.Texture(new TileCoord(0, 0)));
        // A changed colour re-renders.
        compositor.Update(state with { Layers = [layer with { Content = new TextContent(text with { Color = new SRGBColor(0, 0, 1) }) }] }, null, [new TileCoord(0, 0)]);
        Assert.NotSame(first, compositor.Texture(new TileCoord(0, 0)));
    }
}
