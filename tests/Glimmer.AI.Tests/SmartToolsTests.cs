// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/SmartToolsTests.swift
// The text tests ran Vision on the Mac; text recognition on Windows is the app's (Windows.Media.Ocr behind
// IImageTextRecognizer), so here a fake recognizer reports where the drawn text is, and the tests check what this library
// does with it: the reading order, the colour sampled from the picture and the text layer that covers the line.
// noPeopleInALandscape needs the downloaded models: it is in OnDeviceModelTests. The tests after the ported ones are new.
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

public class SmartToolsTests
{
    [Fact]
    public void NeutralSceneHasNoCast()
    {
        var cast = SmartAnalysis.ColorCast(TestPictures.GreyScene());
        Assert.False(SmartAnalysis.IsNoticeableCast(cast));
    }

    [Fact]
    public void WarmCastIsFoundAndCorrected()
    {
        var tint = new Vec3(0.06, 0.0, -0.06);
        var cast = SmartAnalysis.ColorCast(TestPictures.GreyScene(tint))!.Value;
        Assert.True(SmartAnalysis.IsNoticeableCast(cast));
        Assert.True(cast.X > 0.03 && cast.Z < -0.03);
        // The Color Balance shader adds value × 0.5 per tone weight (weights sum to 1).
        var a = SmartAnalysis.CastCorrection(cast, strength: 1);
        var shift = new Vec3(a.Numbers["midtonesR"], a.Numbers["midtonesG"], a.Numbers["midtonesB"]) * 0.5;
        var corrected = cast + shift;
        Assert.True(Math.Max(Math.Abs(corrected.X), Math.Max(Math.Abs(corrected.Y), Math.Abs(corrected.Z))) < 0.005);
        Assert.True(a.Flags["preserveLuminosity"]);
    }

    [Fact]
    public void AutoEnhanceIncludesAClearCast()
    {
        var recipe = SmartAnalysis.EnhanceRecipeFor(TestPictures.GreyScene(new Vec3(0.0, 0.0, 0.08)));
        Assert.True(recipe.CorrectsCast);
        Assert.Contains(recipe.Adjustments, a => a.Kind == AdjustmentKind.ColorBalance);
    }

    [Fact]
    public async Task ReadsTextWithItsPositionAndColour()
    {
        var (image, box) = TestPictures.TextImage("Grand Opening", light: true);
        var recognizer = new FakeTextRecognizer(new ImageText.Line("Grand Opening", box, 0.95));
        var lines = await ImageText.LinesAsync(image, recognizer);
        var line = lines[0];
        Assert.Contains("grand opening", line.String, StringComparison.OrdinalIgnoreCase);
        // The text was drawn with its baseline 80 px above the bottom: the box sits around 220 − 80 − ~50 (y down).
        Assert.True(line.Box.MinX > 40 && line.Box.MinX < 90, $"{line.Box}");
        Assert.True(line.Box.MidY() > 90 && line.Box.MidY() < 140, $"{line.Box}");
        var color = ImageText.TextColor(image, line.Box);
        Assert.True(color.R > 0.7 && color.G > 0.6 && color.B < 0.5, $"{color}");  // yellow, not the dark background
        Assert.Contains("Opening", ImageText.PlainText(lines));
    }

    [Fact]
    public void TextLayerCoversTheOriginalLine()
    {
        var (image, box) = TestPictures.TextImage("Grand Opening", light: true);
        var line = new ImageText.Line("Grand Opening", box, 0.95);
        var layer = ImageText.TextLayer(line, image);
        var text = Assert.IsType<TextContent>(layer.Content).Text;
        var size = text.LayoutSize;
        Assert.True(Math.Abs(size.Width - line.Box.Width) < line.Box.Width * 0.15, $"layout {size.Width} vs box {line.Box.Width}");
        Assert.True(Math.Abs(text.Origin.Y + size.Height / 2 - line.Box.MidY()) < 3);
        Assert.True(text.Size > 40 && text.Size < 110, $"size {text.Size}");  // drawn at 72 pt
        Assert.True(text.Color.R > 0.7 && text.Color.B < 0.5);
    }

    [Fact]
    public void DarkTextOnLightReadsDark()
    {
        var (image, box) = TestPictures.TextImage("Hello", light: false);
        var color = ImageText.TextColor(image, box);
        Assert.True(color.R < 0.3, $"{color}");
    }

    [Fact]
    public void GenericFileNamesAreRecognised()
    {
        foreach (var name in new[] { "IMG_1234", "DSC01234", "Screenshot 2026-11-15 at 10.00.00", "Pasted Image", "20261115_101010", "PXL_2026" })
            Assert.True(ImageLabel.IsGeneric(name), name);
        foreach (var name in new[] { "Logo", "Golden Gate", "mum's birthday" })
            Assert.False(ImageLabel.IsGeneric(name), name);
    }

    // MARK: New in the port

    /// <summary>Lines come top to bottom, a row left to right, and the uncertain ones (0.3 or less) are dropped.</summary>
    [Fact]
    public async Task LinesAreReadInOrderAndUncertainOnesDropped()
    {
        var recognizer = new FakeTextRecognizer(
            new ImageText.Line("world", Rects.Make(300, 102, 120, 40), 0.9),
            new ImageText.Line("second line", Rects.Make(50, 200, 300, 40), 0.9),
            new ImageText.Line("Hello", Rects.Make(50, 100, 200, 40), 0.9),
            new ImageText.Line("smudge", Rects.Make(50, 300, 100, 40), 0.3));
        var lines = await ImageText.LinesAsync(new RGBAImage(500, 400, System.Numerics.Vector4.One), recognizer);
        Assert.Equal(new[] { "Hello", "world", "second line" }, lines.Select(l => l.String));
        Assert.Equal("Hello\nworld\nsecond line", ImageText.PlainText(lines));
    }

    /// <summary>Without a recognizer (Linux, or Windows without an OCR language) reading text says why it can't.</summary>
    [Fact]
    public async Task ReadingTextWithoutARecognizerSaysWhy()
    {
        var error = await Assert.ThrowsAsync<TextRecognitionUnavailableError>(() => ImageText.LinesAsync(new RGBAImage(10, 10, System.Numerics.Vector4.One)));
        Assert.StartsWith("Text recognition isn't available on this computer.", error.Message);
    }

    /// <summary>
    /// Names from the classifier's labels: ImageNet's synonyms shortened to the clearest, a second label when it is
    /// nearly as likely, nothing when nothing is likely enough.
    /// </summary>
    [Fact]
    public void LayerNamesFromImageNetLabels()
    {
        Assert.Equal("tabby cat", ImageLabel.Pretty("tabby, tabby cat"));
        Assert.Equal("pickup truck", ImageLabel.Pretty("pickup, pickup truck"));
        Assert.Equal("valley", ImageLabel.Pretty("valley, vale"));
        Assert.Equal("monarch butterfly", ImageLabel.Pretty("monarch, monarch butterfly, milkweed butterfly, Danaus plexippus"));
        Assert.Equal("Coffee mug", ImageLabel.Name([("coffee mug", 0.8), ("cup", 0.1)]));
        Assert.Equal("Alp & valley", ImageLabel.Name([("alp", 0.33), ("valley, vale", 0.31)]));
        Assert.Null(ImageLabel.Name([]));
    }

    /// <summary>Without the classifier downloaded, a layer keeps its name: naming never asks for a download.</summary>
    [Fact]
    public async Task NoClassifierNoName()
    {
        using var root = new TempFolder();
        var store = new ModelStore(root.Sub("models")) { TestModels = null };
        Assert.Null(await ImageLabel.NameAsync(TestPictures.Mug(), store));
    }
}
