// Not a port: the presets (Presets.swift) and the web parameter bags they are written in (AipsImporter.adjustment).

using System.Text.Json.Nodes;

namespace Glimmer.Core.Tests;

public class PresetTests
{
    [Fact]
    public void EveryPresetBecomesAGroupOfItsAdjustments()
    {
        Assert.Equal(10, Preset.All.Length);
        Assert.Equal(Preset.All.Length, Preset.All.Select(p => p.Id).Distinct().Count());
        foreach (var preset in Preset.All)
        {
            var group = preset.MakeGroup();
            Assert.True(group.IsGroup && group.BlendMode == BlendMode.PassThrough && group.Name == preset.Name);
            var g = ((GroupContent)group.Content).Group;
            Assert.False(g.IsExpanded);
            Assert.Equal(preset.Steps.OfType<PresetStep.AdjustmentStep>().Count(), g.Children.Length);
            Assert.All(g.Children, c => Assert.True(c.IsAdjustment));
            Assert.Equal(preset.Steps.Length, g.Children.Length + preset.FilterSteps.Count);
        }
        // A new group each time, with fresh ids.
        Assert.NotEqual(Preset.All[0].MakeGroup().Id, Preset.All[0].MakeGroup().Id);
    }

    [Fact]
    public void WebParametersFillTheBagsAsOnTheMac()
    {
        var teal = Preset.All.First(p => p.Id == "teal_orange");
        var balance = ((PresetStep.AdjustmentStep)teal.Steps[1]).Adjustment;
        Assert.Equal(-0.18, balance.Number("shadowsR"));
        Assert.Equal(-0.22, balance.Number("highlightsB"));
        Assert.True(balance.Flag("preserveLuminosity"));
        var curves = ((PresetStep.AdjustmentStep)teal.Steps[0]).Adjustment;
        Assert.Equal(4, curves.Curve(CurveChannel.Rgb).Length);
        Assert.Equal(("clarity", 0.25), (teal.FilterSteps[0].Name, teal.FilterSteps[0].Parameters["amount"]));

        // Levels: the numbers stay in the bag too, as the Mac's importer puts every number there.
        var levels = Adjustment.FromWebParameters(AdjustmentKind.Levels, new JsonObject { ["gamma"] = 1.05, ["outBlack"] = 0.08 });
        Assert.Equal(1.05, levels.LevelsChannelFor(CurveChannel.Rgb).Gamma);
        Assert.Equal(1, levels.LevelsChannelFor(CurveChannel.Rgb).InWhite);
        Assert.Equal(0.08, levels.Numbers["outBlack"]);

        // Gradient map: stops with their colours and alpha; fewer than two stops leave the default.
        var map = Adjustment.FromWebParameters(AdjustmentKind.GradientMap, new JsonObject
        {
            ["stops"] = new JsonArray(
                new JsonObject { ["pos"] = 0, ["color"] = new JsonObject { ["r"] = 1, ["g"] = 0, ["b"] = 0, ["a"] = 0.5 } },
                new JsonObject { ["pos"] = 1, ["color"] = new JsonObject { ["r"] = 0, ["g"] = 0, ["b"] = 1 } }),
        });
        Assert.Equal(new GradientStop(0, new SRGBColor(1, 0, 0), 0.5), map.Gradient[0]);
        Assert.Equal(1, map.Gradient[1].Opacity);
        var single = Adjustment.FromWebParameters(AdjustmentKind.GradientMap, new JsonObject { ["stops"] = new JsonArray(new JsonObject { ["pos"] = 0 }) });
        Assert.True(GradientStop.BlackToWhite.SequenceEqual(single.Gradient));
    }
}
