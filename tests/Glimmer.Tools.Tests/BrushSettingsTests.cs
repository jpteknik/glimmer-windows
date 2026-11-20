// Ported from Packages/GlimmerKit/Sources/GlimmerTools/BrushSettings.swift and ToolID.swift: tests of the port (the JSON
// shape of the Mac's synthesized Codable, the presets and the tool ids), which the Mac's suite has no file for.
using System.Text.Json;
using Glimmer.Core;

namespace Glimmer.Tools.Tests;

public class BrushSettingsTests
{
    /// <summary>What the Mac's <c>JSONEncoder</c> writes for <c>BrushSettings()</c>.</summary>
    private const string MacDefaults =
        "{\"size\":30,\"hardness\":0.8,\"opacity\":1,\"flow\":1,\"spacing\":0.1,\"roundness\":1,\"angle\":0,\"scatter\":0,\"sizeJitter\":0," +
        "\"textured\":false,\"pressureSize\":true,\"pressureOpacity\":false,\"tiltAngle\":false,\"tiltRoundness\":false,\"rotationAngle\":false}";

    [Fact]
    public void WritesTheMacsShape() => Assert.Equal(MacDefaults, JsonSerializer.Serialize(new BrushSettings()));

    [Fact]
    public void ReadsTheMacsShapeInAnyKeyOrderAndIgnoresUnknownKeys()
    {
        const string json = "{\"rotationAngle\":true,\"size\":12.5,\"hardness\":0,\"opacity\":0.4,\"flow\":0.3,\"spacing\":0.25,\"roundness\":0.5," +
                            "\"angle\":-30,\"scatter\":0.1,\"sizeJitter\":0.2,\"textured\":true,\"pressureSize\":false,\"pressureOpacity\":true," +
                            "\"tiltAngle\":true,\"tiltRoundness\":true,\"futureKey\":[1,2]}";
        var s = JsonSerializer.Deserialize<BrushSettings>(json)!;
        Assert.Equal(new BrushSettings
        {
            Size = 12.5, Hardness = 0, Opacity = 0.4, Flow = 0.3, Spacing = 0.25, Roundness = 0.5, Angle = -30, Scatter = 0.1,
            SizeJitter = 0.2, Textured = true, PressureSize = false, PressureOpacity = true, TiltAngle = true, TiltRoundness = true,
            RotationAngle = true,
        }, s);
        Assert.Equal(s, JsonSerializer.Deserialize<BrushSettings>(JsonSerializer.Serialize(s)));
    }

    /// <summary>As with Swift's synthesized decoder, a missing key or a wrong type is an error, not a default.</summary>
    [Theory]
    [InlineData("""{"size":30}""")]
    [InlineData("""[]""")]
    public void RejectsIncompleteOrMistypedJson(string json) =>
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<BrushSettings>(json));

    [Fact]
    public void RejectsABooleanGivenAsANumber()
    {
        string json = MacDefaults.Replace("\"textured\":false", "\"textured\":0");
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<BrushSettings>(json));
    }

    [Fact]
    public void PresetsMatchTheMac()
    {
        Assert.Equal(["Soft Round", "Hard Round", "Chalk", "Calligraphic", "Airbrush", "Pencil"], BrushSettings.Presets.Select(p => p.Name).ToArray());
        var chalk = BrushSettings.Presets[2].Settings;
        Assert.True(chalk.Textured && chalk.Hardness == 0.6 && chalk.Size == 40 && chalk.Flow == 0.8);
        var calligraphy = BrushSettings.Presets[3].Settings;
        Assert.True(calligraphy.Roundness == 0.25 && calligraphy.Angle == 45);
    }
}

public class ToolIDTests
{
    [Fact]
    public void RawValuesAndShortcutsMatchTheMac()
    {
        Assert.Equal(26, ToolID.AllCases.Count);
        Assert.Equal("rectangleMarquee", ToolID.RectangleMarquee.RawValue());
        Assert.Equal(ToolID.HealingBrush, ToolID.FromRawValue("healingBrush"));
        Assert.Null(ToolID.FromRawValue("HealingBrush"));
        Assert.Equal('m', ToolID.EllipseMarquee.ShortcutKey());
        Assert.Equal('o', ToolID.Burn.ShortcutKey());
        Assert.Null(ToolID.Smudge.ShortcutKey());
        foreach (var tool in ToolID.AllCases) Assert.Equal(tool, ToolID.FromRawValue(tool.RawValue()));
    }
}
