// Ported from Packages/GlimmerKit/Tests/GlimmerCoreTests/CommandSearchTests.swift

namespace Glimmer.Core.Tests;

public class CommandSearchTests
{
    private readonly PaletteItem[] items =
    [
        new("a", "Gaussian Blur…", "Filter", new PaletteAction.Command(CommandID.Filter(FilterKind.GaussianBlur)), ["soften"]),
        new("b", "Group Layer", "Layer", new PaletteAction.Command(CommandID.GroupLayers)),
        new("c", "Hue/Saturation", "Adjustment", new PaletteAction.Command(CommandID.NewAdjustment(AdjustmentKind.HueSaturation)), ["color"]),
        new("d", "Brush", "Tool", new PaletteAction.Tool("brush")),
    ];

    [Fact]
    public void SubsequenceMatching()
    {
        Assert.NotNull(FuzzyMatch.Score("gb", "Gaussian Blur"));
        Assert.Null(FuzzyMatch.Score("xyz", "Gaussian Blur"));
        Assert.True(FuzzyMatch.Score("blur", "Gaussian Blur") > FuzzyMatch.Score("blur", "Bring Layer Up Right"));
    }

    [Fact]
    public void RankingAndKeywords()
    {
        Assert.Equal("a", CommandSearch.Search("gaus", items).First().Item.Id);
        Assert.Equal("c", CommandSearch.Search("hue", items).First().Item.Id);
        Assert.Equal("a", CommandSearch.Search("soften", items).First().Item.Id);
        Assert.Equal("d", CommandSearch.Search("br", items).First().Item.Id);
        Assert.Equal(4, CommandSearch.Search("", items).Count);
    }

    [Fact]
    public void AssistantRouting()
    {
        Assert.True(CommandSearch.LooksLikeAssistantRequest("make the sky more dramatic", 5));
        Assert.False(CommandSearch.LooksLikeAssistantRequest("blur", 20));
        Assert.True(CommandSearch.LooksLikeAssistantRequest("remove person", null));
    }

    [Fact]
    public void CandidatesComeFromWordsToo()
    {
        // The whole sentence matches nothing; its words do.
        Assert.Empty(CommandSearch.Search("please soften the photo a bit", items));
        var c = CommandSearch.Candidates("please soften the photo a bit", items);
        Assert.Equal("a", c.First().Id);
        Assert.Contains(CommandSearch.Candidates("pick the brush", items), i => i.Id == "d");
    }
}

public class CommandUsageTests
{
    private readonly PaletteItem[] items =
    [
        new("sharpen", "Sharpen", "Filter", new PaletteAction.Command(CommandID.Filter(FilterKind.Sharpen))),
        new("shadow", "Shadows", "Adjustment", new PaletteAction.Command(CommandID.GroupLayers)),
    ];

    [Fact]
    public void FrequentlyUsedItemWinsAnEvenMatch()
    {
        var now = DateTimeOffset.UtcNow;
        var plain = CommandSearch.Search("sha", items, now: now);
        Assert.Equal("sharpen", plain.First().Item.Id);
        var usage = new Dictionary<string, CommandUsage>();
        for (int i = 0; i < 5; i++) CommandUsage.Record("shadow", usage, now);
        var ranked = CommandSearch.Search("sha", items, usage: usage, now: now);
        Assert.Equal("shadow", ranked.First().Item.Id);
    }

    [Fact]
    public void BoostFadesWithoutUse()
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = new CommandUsage(8, now);
        var stale = new CommandUsage(8, now.AddSeconds(-60 * 86_400));
        Assert.True(fresh.Boost(now) > 2);
        Assert.True(stale.Boost(now) < 0.3);
        Assert.Equal(0, new CommandUsage().Boost(now));
    }

    [Fact]
    public void BoostNeverBeatsAMuchBetterMatch()
    {
        var usage = new Dictionary<string, CommandUsage>();
        for (int i = 0; i < 100; i++) CommandUsage.Record("shadow", usage);
        Assert.Equal("sharpen", CommandSearch.Search("sharpen", items, usage: usage).First().Item.Id);
    }

    [Fact]
    public void TableStaysBounded()
    {
        var usage = new Dictionary<string, CommandUsage>();
        for (int i = 0; i < 30; i++) CommandUsage.Record($"id{i}", usage, limit: 10);
        Assert.Equal(10, usage.Count);
        Assert.True(usage.ContainsKey("id29"));
    }

    /// <summary>The usage table as Swift's default JSONEncoder writes it: the date in seconds since 2001. Not in the Mac's tests.</summary>
    [Fact]
    public void UsageReadsAsTheMacWritesIt()
    {
        var u = SwiftJson.Deserialize<CommandUsage>("""{"count":3,"last":752112000}""");
        Assert.Equal(3, u.Count);
        Assert.Equal(new DateTimeOffset(2024, 11, 1, 0, 0, 0, TimeSpan.Zero), u.Last);
        Assert.Equal("""{"count":3,"last":752112000}""", SwiftJson.Serialize(u));
        var never = SwiftJson.Deserialize<CommandUsage>("""{"count":0,"last":-63114076800}""");
        Assert.Equal(DateTimeOffset.MinValue, never.Last);
    }
}
