// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/StyleTextStepTests.swift
// (OfflineMessageTests are in OfflineMessageTests.cs.) Core Text's font families are Skia's here: "HelveticaNeue" resolves to
// whatever the machine has (Arial, Segoe UI, DejaVu …), so the bold test checks the relation (a bold member of the same family
// is chosen, and unbolding returns a regular one) and not the Mac's exact PostScript names.
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

public class StyleTextStepTests
{
    static TextLayer Text(string s) => new(s, Vec2.Zero, "HelveticaNeue", 40, SRGBColor.Black);
    static AgentStep Step(params (string, AgentValue)[] p) => new("style_text", p);
    static int[][] Ranges(StyleTextStep.Info info) => info.Ranges.Select(r => new[] { r.From, r.Length }).ToArray();

    [Fact]
    public void MatchStylesEveryOccurrenceAndReportsThem()
    {
        var t = Text("sale, sale, SALE");
        var (styled, info) = StyleTextStep.Apply(Step(("match", "sale"), ("color", "#E0303A")), t);
        Assert.Equal(2, info.Matches);   // exact matches first; SALE only if nothing matched exactly
        Assert.Equal([[0, 4], [6, 4]], Ranges(info));
        Assert.True(styled.StyleAt(7).Color.HexString() == "#E0303A" && styled.StyleAt(12).Color == SRGBColor.Black);
        var first = Text("sale, sale");
        var (f2, one) = StyleTextStep.Apply(Step(("match", "sale"), ("occurrence", "first"), ("size", 80)), first);
        Assert.Equal([[0, 4]], Ranges(one));
        Assert.Equal(40, f2.StyleAt(7).Size);
    }

    [Fact]
    public void FromAndLengthCountGraphemeClusters()
    {
        // 👍🏽 is 4 UTF-16 units and 2 code points; "å" here is a + combining ring (2 units).
        var s = "👍🏽 låt bra";
        Assert.Equal(9, new System.Globalization.StringInfo(s).LengthInTextElements);
        var (t, info) = StyleTextStep.Apply(Step(("from", 2), ("length", 3), ("size", 60)), Text(s));
        Assert.Equal([[2, 3]], Ranges(info));
        var big = t.ResolvedRuns().Where(r => r.Style.Size == 60).Select(r => t.String.Substring(r.Range.Location, r.Range.Length)).ToList();
        Assert.Equal(["låt"], big);   // the combining ring stays with its a
        // The emoji is one character: from 0, length 1 takes all of it.
        var (e, _) = StyleTextStep.Apply(Step(("from", 0), ("length", 1), ("color", "#00FF00")), Text(s));
        var green = e.ResolvedRuns().Where(r => r.Style.Color.HexString() == "#00FF00").Select(r => e.String.Substring(r.Range.Location, r.Range.Length)).ToList();
        Assert.Equal(["👍🏽"], green);
        // The styles list reports the same units.
        var run = t.Styles!.Value.First();
        Assert.Equal((2, 3), t.CharacterRange(run.Range));
    }

    [Fact]
    public void MatchNeverSplitsACharacter()
    {
        var (t, info) = StyleTextStep.Apply(Step(("match", "la"), ("bold", true)), Text("låt and lat"));
        Assert.Equal([[8, 2]], Ranges(info));   // “la” isn't inside “låt”, where the a carries a ring
        Assert.True(FontResolver.IsBold(t.StyleAt(10).FontName), t.StyleAt(10).FontName);
    }

    [Fact]
    public void RefusalsHaveReasons()
    {
        static string? Reason(string s, params (string, AgentValue)[] p)
        {
            try { StyleTextStep.Apply(Step(p), Text(s)); return null; }
            catch (StyleTextStep.Refusal r) { return r.Reason; }
            catch { return "other"; }
        }
        Assert.Equal("no_match", Reason("Hello", ("match", "bye"), ("bold", true)));
        Assert.Equal("bad_range", Reason("Hello", ("from", 3), ("length", 5), ("bold", true)));
        Assert.Equal("bad_range", Reason("👍🏽", ("from", 1), ("length", 1), ("size", 9)));   // one character only
        Assert.Equal("unknown_font", Reason("Hello", ("match", "Hello"), ("fontName", "NoSuchFont-Bold")));
        Assert.Equal("nothing_to_style", Reason("Hello", ("match", "Hello")));
        Assert.Equal("bad_color", Reason("Hello", ("color", "red")));
        Assert.Equal("bad_size", Reason("Hello", ("size", 0)));
    }

    [Fact]
    public void BoldUsesTheFamilysBoldMember()
    {
        var t = Text("plain bold");
        var (b, _) = StyleTextStep.Apply(Step(("match", "bold"), ("bold", true)), t);
        Assert.True(FontResolver.IsBold(b.StyleAt(7).FontName) && !FontResolver.IsBold(b.StyleAt(0).FontName));
        Assert.Equal("HelveticaNeue", b.StyleAt(0).FontName);
        var (back, _) = StyleTextStep.Apply(Step(("match", "bold"), ("bold", false)), b);
        Assert.False(FontResolver.IsBold(back.StyleAt(7).FontName));
    }

    [Fact]
    public void InfoHasTheFoundationShape()
    {
        var (_, info) = StyleTextStep.Apply(Step(("match", "a"), ("size", 50)), Text("aXa"));
        var d = info.ToDictionary();
        Assert.Equal(2, d["matches"]);
        Assert.Equal(2, ((List<object?>)d["ranges"]!).Count);
    }

    [Fact]
    public void HexStringReversesParse()
    {
        Assert.Equal("#E0303A", HexColors.Parse("#e0303a")!.Value.HexString());
        Assert.Equal("#00FF00", HexColors.Parse(" 00ff00ab ")!.Value.HexString());   // alpha ignored
        Assert.Null(HexColors.Parse("red"));
        Assert.Null(HexColors.Parse("#12345"));
        Assert.Null(HexColors.Parse(null));
    }

    [Fact]
    public void TheOperationIsListed()
    {
        var op = AgentAPI.Operations(AgentOperationCatalog.Instance).First(o => (string?)o["name"] == "style_text");
        Assert.True(op["paid"]!.GetValue<bool>() == false && op["startsJob"]!.GetValue<bool>() == false);
        var props = op["params"]!["properties"]!;
        Assert.Equal("boolean", (string?)props["bold"]!["type"]);
        Assert.Contains("grapheme clusters", (string?)props["from"]!["description"]);
        var guide = AgentAPI.Guide(9001, "1", AgentOperationCatalog.Instance);
        Assert.True(guide.Contains("👍🏽") && guide.Contains("\"op\": \"style_text\""));
    }
}
