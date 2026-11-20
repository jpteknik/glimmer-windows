// New tests for the ported value types and the planner's pure functions (the Swift originals are covered indirectly by AgentTests.swift, which needs the text model).
using System.Text.Json;
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

public class AgentTypesTests
{
    [Fact]
    public void AgentValueRoundTripsAndConvertsLikeTheSwiftEnum()
    {
        var v = JsonSerializer.Deserialize<AgentValue>("""{"a":1,"b":[true,null,"x"],"c":{"d":2.5}}""")!;
        Assert.Equal(JsonKind.Object, v.Kind);
        Assert.Equal(1, v.AsObject!["a"].AsNumber);
        Assert.Equal(JsonKind.Null, v.AsObject["b"].AsArray!.Value[1].Kind);
        Assert.Equal(JsonSerializer.Deserialize<AgentValue>(JsonSerializer.Serialize(v)), v);
        // number accessor: numbers, numeric strings and bools; not other text
        Assert.Equal(7, AgentValue.String("7").AsNumber);
        Assert.Equal(1, AgentValue.Bool(true).AsNumber);
        Assert.Null(AgentValue.String("7 ").AsNumber);
        Assert.Null(AgentValue.String("abc").AsNumber);
        Assert.Null(AgentValue.Number(1).AsString);
        Assert.Equal("3", AgentValue.Number(3).PlainString);
        Assert.Equal("true", AgentValue.Bool(true).PlainString);
        Assert.Equal("{\"a\":1}", JsonSerializer.Serialize(AgentValue.Object(("a", 1))));
    }

    [Fact]
    public void AgentStepDecodesLeniently()
    {
        var s = JsonSerializer.Deserialize<AgentStep>("""{"params":{"x":1},"rationale":5}""")!;
        Assert.Equal("", s.Op);
        Assert.Equal(1, s.Number("x"));
        Assert.Null(s.Rationale);   // not a string: nil, not an error
        var t = JsonSerializer.Deserialize<AgentStep>("""{"op":"crop","rationale":"why"}""")!;
        Assert.Equal("crop", t.Op);
        Assert.Empty(t.Params);
        Assert.Equal("""{"op":"crop","params":{},"rationale":"why"}""", JsonSerializer.Serialize(t));
        Assert.Equal("""{"op":"crop","params":{}}""", JsonSerializer.Serialize(new AgentStep("crop")));
    }

    [Fact]
    public void ParsePlanToleratesFencesProseAndInventedOps()
    {
        var reply = """
            Sure! ```json
            {"steps": [{"op": "auto_enhance", "rationale": "first"}, {"op": "teleport", "params": {}}, 5, {"op": "crop", "params": {"aspect": "1:1"}}],
             "message": "Two steps {with braces}"}
            ``` hope that helps
            """;
        var plan = AgentPlanner.ParsePlan(reply);
        Assert.Equal(["auto_enhance", "crop"], plan.Steps.Select(s => s.Op));
        Assert.Equal(["teleport"], plan.DroppedOps.ToArray());
        Assert.Equal("Two steps {with braces}", plan.Message);
        // Plain prose is a chat answer; nothing at all is an error.
        Assert.Equal("Hello there", AgentPlanner.ParsePlan("  Hello there \n").Message);
        Assert.Throws<AgentError>(() => AgentPlanner.ParsePlan("   "));
        Assert.True(plan.UsesAI == false && new AgentPlan([new AgentStep("generate_layer")]).UsesAI);
    }

    [Theory]
    [InlineData(0.25, "0.25")]
    [InlineData(0.5, "0.5")]
    [InlineData(0.05, "0.05")]
    [InlineData(0.1, "0.1")]
    [InlineData(0.15, "0.15")]
    [InlineData(1.5, "1.5")]
    [InlineData(12.5, "12")]        // an exact tie rounds to even, as printf does
    [InlineData(13.5, "14")]
    [InlineData(0.125, "0.12")]
    [InlineData(0.375, "0.38")]
    [InlineData(99.5, "1e+02")]     // rounds up into exponent form
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(-0.35, "-0.35")]
    [InlineData(2.675, "2.7")]
    [InlineData(1234.5, "1.2e+03")]
    public void FormatG2IsPrintfPercentDotTwoG(double v, string expected) => Assert.Equal(expected, AgentPlanner.FormatG2(v));

    /// <summary>Values and what Python's <c>'%.2g' % v</c> (C's printf) printed for them.</summary>
    public static readonly (double, string)[] Printf =
    [
        (0.0003, "0.0003"),
        (6.51e-05, "6.5e-05"),
        (0.53588, "0.54"),
        (0.375, "0.38"),
        (1e-05, "1e-05"),
        (4245.2, "4.2e+03"),
        (9474.5, "9.5e+03"),
        (5.8, "5.8"),
        (1e-05, "1e-05"),
        (0.0858, "0.086"),
        (1e-05, "1e-05"),
        (0.006, "0.006"),
        (1e-05, "1e-05"),
        (0.0006, "0.0006"),
        (6.8039997, "6.8"),
        (3141.4717, "3.1e+03"),
        (0.0036158, "0.0036"),
        (574.4237, "5.7e+02"),
        (87.514, "88"),
        (0.0005119, "0.00051"),
        (34.2056, "34"),
        (3.9e-06, "3.9e-06"),
        (0.558, "0.56"),
        (6952.9537, "7e+03"),
        (0.084, "0.084"),
        (0.073116, "0.073"),
        (57.795, "58"),
        (0.7, "0.7"),
        (0.94, "0.94"),
        (61.1, "61"),
        (0.02, "0.02"),
        (7.3836, "7.4"),
        (91.7, "92"),
        (1.66366, "1.7"),
        (0.0002778, "0.00028"),
        (430.522, "4.3e+02"),
        (0.706397, "0.71"),
        (0.01, "0.01"),
        (0.001, "0.001"),
        (0.0003, "0.0003")
    ];

    [Fact]
    public void FormatG2AgreesWithPythonPrintfOnASweep()
    {
        foreach (var (v, expected) in Printf) Assert.Equal(expected, AgentPlanner.FormatG2(v));
    }

    [Fact]
    public void PromptsAreAssembledAsTheOriginalDoes()
    {
        var ctx = new AgentContext(2400, 1600, 3, true, "pixel", "Photo");
        var history = Enumerable.Range(1, 8).Select(i => ($"q{i}", $"a{i}")).ToList();
        var p = AgentPlanner.UserPrompt("make it warm", ctx, history);
        Assert.StartsWith("CURRENT EDITOR CONTEXT:\n- canvas: 2400×1600 px\n- layers: 3\n- active layer: Photo (pixel)\n- hasSelection: true\nEARLIER IN THIS CONVERSATION:\n- user: q3\n  you: a3\n", p);
        Assert.EndsWith("- user: q8\n  you: a8\n\nUSER GOAL:\nmake it warm\n\nReturn the JSON plan now.", p);
        Assert.DoesNotContain("q2", p);
        var none = AgentPlanner.UserPrompt("x", ctx with { HasSelection = false }, []);
        Assert.Equal("CURRENT EDITOR CONTEXT:\n- canvas: 2400×1600 px\n- layers: 3\n- active layer: Photo (pixel)\n- hasSelection: false\nUSER GOAL:\nx\n\nReturn the JSON plan now.", none);
        var system = AgentPlanner.SystemPrompt();
        Assert.Contains("\n\nAVAILABLE OPS:\n- add_adjustment: ", system);
        Assert.Contains("- generate_layer [AI job]: ", system);
        Assert.Contains("- auto_enhance: Automatic tone and color correction", system);
        Assert.Contains("    (no params)", system);
        Assert.Contains("    - type (string): Which adjustment. — one of: brightness_contrast, levels, curves, exposure", system);
    }
}
