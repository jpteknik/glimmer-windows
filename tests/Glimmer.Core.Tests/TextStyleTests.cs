// Not a port (the Mac's tests of TextStyles.swift live in the app's test target): the styled-run model, the character
// ranges an agent uses, and the font lookup that stands in for Core Text's.

using System.Text.Json;

namespace Glimmer.Core.Tests;

public class TextStyleTests
{
    private static readonly SRGBColor Red = new(1, 0, 0);

    [Fact]
    public void StylingAStretchKeepsOnlyWhatDiffers()
    {
        var t = new TextLayer("Hello world", Vec2.Zero, size: 40);
        t = t.SetStyle(new TextRange(6, 5), size: 60, color: Red);
        Assert.Equal([new TextStyleRun(6, 5, null, 60, Red)], t.Styles!.Value.ToArray());
        Assert.Equal(60, t.StyleAt(8).Size);
        Assert.Equal(40, t.StyleAt(0).Size);
        Assert.Equal(60, t.LargestSize);
        var runs = t.ResolvedRuns();
        Assert.Equal(2, runs.Count);
        Assert.Equal(new TextRange(0, 6), runs[0].Range);
        // Styling it back to the layer's own values leaves no runs.
        t = t.SetStyle(new TextRange(6, 5), size: 40, color: SRGBColor.Black);
        Assert.Null(t.Styles);
        Assert.False(t.HasStyledRuns);
    }

    [Fact]
    public void AChangeToTheWholeLayerDropsThePerWordValuesOfThatProperty()
    {
        var t = new TextLayer("abc def", Vec2.Zero).SetStyle(new TextRange(0, 3), size: 10, color: Red);
        var bigger = t with { Size = 90 };
        var dropped = bigger.DropStyles(t);
        Assert.Equal([new TextStyleRun(0, 3, null, null, Red)], dropped.Styles!.Value.ToArray());
    }

    [Fact]
    public void StylesFollowTheirCharactersWhenTheTextChanges()
    {
        var t = new TextLayer("Hello world", Vec2.Zero).SetStyle(new TextRange(6, 5), color: Red);
        // Text inserted before the styled word: the style moves with the word.
        var moved = (t with { String = "Hello big world" }).CarryStyles("Hello world");
        Assert.Equal([new TextStyleRun(10, 5, null, null, Red)], moved.Styles!.Value.ToArray());
        // Text inserted inside the styled word takes the style of the character before it.
        var inside = (t with { String = "Hello wo-rld" }).CarryStyles("Hello world");
        Assert.Equal([new TextStyleRun(6, 6, null, null, Red)], inside.Styles!.Value.ToArray());
        // A deleted styled word takes its style with it.
        var gone = (t with { String = "Hello " }).CarryStyles("Hello world");
        Assert.Null(gone.Styles);
    }

    [Fact]
    public void CharacterRangesCountWhatAPersonSeesAsOneCharacter()
    {
        var t = new TextLayer("Hej 👍🏽 på dig", Vec2.Zero);
        Assert.Equal(new TextRange(4, 4), t.Utf16Range(4, 1));   // the thumb and its skin tone: four UTF-16 units
        Assert.Equal((4, 1), t.CharacterRange(new TextRange(4, 4)));
        Assert.Null(t.Utf16Range(10, 5));
        // Whole characters only: "a" doesn't match inside a decomposed "å" ("a" + ring).
        var decomposed = new TextLayer("pa" + (char)0x030A + " a", Vec2.Zero);
        Assert.Equal([new TextRange(4, 1)], decomposed.Ranges("a", all: true));
        // Exact first, then without case.
        var t2 = new TextLayer("Glimmer glimmer", Vec2.Zero);
        Assert.Equal([new TextRange(0, 7)], t2.Ranges("Glimmer", all: true));
        Assert.Equal(2, t2.Ranges("GLIMMER", all: true).Count);
        Assert.Single(t2.Ranges("GLIMMER", all: false));
    }

    [Fact]
    public void ThePostScriptNamesTheMacStoresAreParsed()
    {
        var (families, weight, _, slant) = FontResolver.Parse("HelveticaNeue-BoldItalic");
        Assert.Equal("Helvetica Neue", families[0]);
        Assert.Contains("Arial", families);
        Assert.True(weight == 700 && slant != SkiaSharp.SKFontStyleSlant.Upright);
        Assert.Equal("Times New Roman", FontResolver.Parse("TimesNewRomanPS-BoldMT").Families[0]);
        Assert.Equal(700, FontResolver.Parse("TimesNewRomanPS-BoldMT").Weight);
        Assert.Equal(400, FontResolver.Parse("TimesNewRomanPSMT").Weight);
        Assert.Equal(700, FontResolver.Parse("ArialBold").Weight);
        Assert.Equal(3, FontResolver.Parse("AvenirNextCondensed-Medium").Width);
        Assert.Equal(500, FontResolver.Parse("AvenirNextCondensed-Medium").Weight);
        Assert.Contains("Segoe UI", FontResolver.Parse(".SFNS-Regular").Families);
        Assert.Equal("SF Pro Display", FontResolver.CamelSplit("SFProDisplay"));
    }

    [Fact]
    public void AStoredFontNameIsKeptAndResolvesTheSameEveryTime()
    {
        var a = FontResolver.Resolve("HelveticaNeue-Bold");
        var b = FontResolver.Resolve("HelveticaNeue-Bold");
        Assert.Equal(a, b);
        Assert.Equal("HelveticaNeue-Bold", a.RequestedName);
        Assert.True(a.Weight >= 600, $"{a}");
        // The model and its JSON keep the Mac's name whatever it resolved to.
        var t = new TextLayer("x", Vec2.Zero, fontName: "HelveticaNeue-Bold");
        Assert.Contains("\"fontName\":\"HelveticaNeue-Bold\"", SwiftJson.Serialize(t));
        // A name that exists is found exactly; one that doesn't, isn't.
        var installed = FontResolver.InstalledPostScriptNames;
        Assert.NotEmpty(installed);
        Assert.True(FontResolver.Exists(installed[0]) && FontResolver.Resolve(installed[0]).IsExact);
        Assert.False(TextLayer.FontExists("NoSuchFont-Regular"));
    }

    [Fact]
    public void BoldGoesWithinTheFamily()
    {
        var regular = FontResolver.InstalledPostScriptNames.FirstOrDefault(n => n == "DejaVuSans")
                      ?? FontResolver.InstalledPostScriptNames.FirstOrDefault(n => !FontResolver.IsBold(n) && FontResolver.WithBold(n, true) is not null);
        if (regular is null) return;   // no family with a bold member installed
        var t = new TextLayer("Hello world", Vec2.Zero, fontName: regular);
        var (bold, changed) = t.SetBold(true, new TextRange(0, 5));
        Assert.True(changed);
        Assert.True(FontResolver.IsBold(bold.StyleAt(0).FontName));
        Assert.False(FontResolver.IsBold(bold.StyleAt(7).FontName));
        Assert.Equal(regular, bold.StyleAt(7).FontName);
    }

    [Fact]
    public void LayoutGrowsWithTextAndWrapsInABox()
    {
        var one = new TextLayer("Hello", Vec2.Zero, size: 40);
        var two = new TextLayer("Hello\nHello", Vec2.Zero, size: 40);
        // Every line is exactly size × lineHeight tall, as on the Mac (+2 for the frame).
        Assert.Equal(Math.Ceiling(40 * 1.2) + 2, one.LayoutSize.Height);
        Assert.Equal(Math.Ceiling(2 * 40 * 1.2) + 2, two.LayoutSize.Height);
        Assert.Equal(one.LayoutSize.Width, two.LayoutSize.Width);
        // A box wraps at its width, and the box's width is the layout's width.
        var boxed = new TextLayer("one two three four five six", Vec2.Zero, size: 40, boxWidth: 200);
        Assert.Equal(202, boxed.LayoutSize.Width);
        Assert.True(boxed.LayoutSize.Height > 3 * 48, $"{boxed.LayoutSize}");
        // An empty text still has a line, as the Mac lays out " ".
        Assert.Equal(Math.Ceiling(72 * 1.2) + 2, new TextLayer("", Vec2.Zero).LayoutSize.Height);
        // A bigger word makes its paragraph's lines taller.
        var mixed = new TextLayer("Hi there", Vec2.Zero, size: 20).SetStyle(new TextRange(3, 5), size: 50);
        Assert.Equal(Math.Ceiling(50 * 1.2) + 2, mixed.LayoutSize.Height);
    }

    [Fact]
    public void GlyphOutlinesAreInTheTextBoxAndConvertToAPenPath()
    {
        var t = new TextLayer("Ab", Vec2.Zero, size: 50).SetStyle(new TextRange(1, 1), color: Red);
        var groups = t.GlyphOutlinesByColor();
        Assert.Equal(2, groups.Count);
        Assert.Equal(SRGBColor.Black, groups[0].Color);
        Assert.Equal(Red, groups[1].Color);
        var bounds = t.GlyphOutlines().ControlBounds!.Value;
        var size = t.LayoutSize;
        Assert.True(bounds.MinX >= -1 && bounds.MaxX <= size.Width + 1 && bounds.MinY >= -5 && bounds.MaxY <= size.Height + 5, $"{bounds} in {size}");
        var pen = t.GlyphOutlines().ToVectorPath("Ab");
        Assert.NotEmpty(pen.Subpaths);
        Assert.All(pen.Subpaths, s => Assert.True(s.IsClosed));
    }

    [Fact]
    public void ATransformedLayerRendersInsideItsTransformedBounds()
    {
        var t = new TextLayer("Turn", new Vec2(100, 100), size: 40);
        var turned = t with { Transform = AffineTransform.Make(0, 0, 1, 1, 0.5, new Vec2(1, 1)).Translated(new Vec2(0, 0)) };
        var raster = turned.Render();
        Assert.NotNull(raster);
        Assert.Equal(turned.Bounds, raster!.Rect);
        Assert.Contains(raster.Pixels.Where((_, i) => i % 4 == 3), a => a > 128);
    }

    [Fact]
    public void StyleRunsReadAsTheMacWritesThem()
    {
        var run = SwiftJson.Deserialize<TextStyleRun>("""{"length":2,"location":1,"fontName":"Menlo-Bold"}""");
        Assert.Equal(new TextStyleRun(1, 2, "Menlo-Bold"), run);
        Assert.Throws<JsonException>(() => SwiftJson.Deserialize<TextStyleRun>("""{"length":2}"""));
    }
}
