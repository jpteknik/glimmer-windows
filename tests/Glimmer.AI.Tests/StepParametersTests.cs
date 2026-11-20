// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/StepParametersTests.swift
// (the sibling tests of that file are in SiblingTests.cs, the worked-figures and plan-claims suites in WorkedFiguresTests.cs and PlanClaimsTests.cs)
using System.Text.Json.Nodes;
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

/// <summary>A parameter an operation doesn't have is refused by name, for every operation in the register: the walk, so that an operation added later can't forget it.</summary>
public class StepParametersTests
{
    [Fact]
    public void EveryOperationRefusesAParameterItDoesNotHave()
    {
        Assert.True(AgentPlanner.Ops.Count >= 30);
        foreach (var op in AgentPlanner.Ops)
        {
            // Its own parameters, whatever their values: nothing stray.
            var all = new Dictionary<string, AgentValue>();
            foreach (var p in op.Params.Where(p => p.Name != "params")) all[p.Name] = AgentValue.String("x");
            Assert.True(StepParameters.StrayIn(new AgentStep(op.Name, all)) is null, $"{op.Name} refuses one of its own parameters");
            // One more, under a name it doesn't have.
            all["zzNotAParameter"] = AgentValue.Number(1);
            var stray = StepParameters.StrayIn(new AgentStep(op.Name, all));
            Assert.True(stray is { Name: "zzNotAParameter", Inside: null } && stray.Allowed.SequenceEqual(op.Params.Select(p => p.Name)), $"{op.Name} lets an unknown parameter through");
            Assert.StartsWith($"{op.Name} has no parameter named zzNotAParameter. ", stray!.Message);
        }
        // The model's own "rationale" is not a parameter of any operation and is always allowed.
        Assert.Null(StepParameters.StrayIn(new AgentStep("auto_enhance", ("rationale", "why"))));
        Assert.Equal("auto_enhance has no parameter named strength. It takes none.", StepParameters.StrayIn(new AgentStep("auto_enhance", ("strength", 1)))?.Message);
        // An operation Glimmer doesn't have (a sibling's tool) is not this check's to refuse.
        Assert.Null(StepParameters.StrayIn(new AgentStep("flimmer_add_clip", ("anything", 1))));
    }

    [Fact]
    public void TheNestedSettingsAreCheckedAgainstTheirKind()
    {
        int checkedCount = 0;
        void Walk(string op, IEnumerable<string> types)
        {
            foreach (var type in types)
            {
                var keys = StepParameters.NestedKeys(op, type);
                if (keys is null) { Assert.Fail($"{op} {type} has no list of settings"); continue; }
                var nested = keys.ToDictionary(k => k, _ => AgentValue.Number(0));
                Assert.True(StepParameters.StrayIn(new AgentStep(op, ("type", type), ("params", AgentValue.Object(nested)))) is null, $"{op} {type} refuses one of its own settings");
                nested["zzNotASetting"] = AgentValue.Number(1);
                var stray = StepParameters.StrayIn(new AgentStep(op, ("type", type), ("params", AgentValue.Object(nested))));
                Assert.True(stray is { Name: "zzNotASetting", Inside: "params" }, $"{op} {type} lets an unknown setting through");
                checkedCount++;
            }
        }
        Walk("add_adjustment", AdjustmentKind.AllCases.Select(k => k.RawValue()));
        Walk("apply_filter", FilterKind.AllCases.Select(k => k.RawValue()));
        Walk("add_layer_effect", ["dropShadow", "innerShadow", "outerGlow", "stroke", "colorOverlay"]);
        Assert.True(checkedCount > 30, $"{checkedCount}");
        // What the guide documents for each kind is what is accepted: the same lists.
        Assert.Equal(["brightness", "contrast"], StepParameters.NestedKeys("add_adjustment", "brightness_contrast"));
        Assert.Contains("enabled", StepParameters.NestedKeys("add_layer_effect", "dropShadow")!);
        Assert.Equal(
            $"add_adjustment has no parameter named brightness in params. It has: {string.Join(", ", StepParameters.NestedKeys("add_adjustment", "exposure")!)}.",
            StepParameters.StrayIn(new AgentStep("add_adjustment", ("type", "exposure"), ("params", AgentValue.Object(("brightness", 0.2)))))?.Message);
        // A type that doesn't exist is refused elsewhere, with its own message.
        Assert.Null(StepParameters.StrayIn(new AgentStep("add_adjustment", ("type", "nope"), ("params", AgentValue.Object(("x", 1))))));
    }

    [Fact]
    public void APlanWithAStrayParameterIsShownNotRun()
    {
        var named = new AgentPlan([new AgentStep("add_adjustment", ("type", "black_white"))]);
        Assert.True(named.RunsAtOnce("Make it black and white"));
        var stray = new AgentPlan([new AgentStep("add_adjustment", ("type", "black_white"), ("strength", 2))]);
        Assert.False(stray.RunsAtOnce("Make it black and white"));
    }

    [Fact]
    public void EndpointsHaveTheirParametersAndNoOthers()
    {
        Assert.Equal(["name"], StepParameters.QueryParameters(["documents"], "POST"));
        Assert.Empty(StepParameters.QueryParameters(["documents"], "GET"));
        Assert.Equal(["format", "maxSide"], StepParameters.QueryParameters(["documents", "abc", "image"], "GET"));
        Assert.Equal(["fit", "x", "y", "smartObject", "name"], StepParameters.QueryParameters(["documents", "abc", "layers"], "POST"));
        Assert.Empty(StepParameters.QueryParameters(["documents", "abc", "steps"], "POST"));
        Assert.Equal("POST /x has no parameter named scale. It has: fit, x.", StepParameters.StrayMessage(["fit", "scale"], ["fit", "x"], "POST /x"));
        Assert.Null(StepParameters.StrayMessage(["fit"], ["fit", "x"], "POST /x"));
        Assert.Equal("Undo has no parameter named a. It takes none.", StepParameters.StrayMessage(["a"], [], "Undo"));
    }
}

/// <summary>What is published is what is enforced (SIBLINGS.md §3): closed schemas, the nested settings per type, <c>rationale</c> everywhere.</summary>
public class PublishedSchemaTests
{
    static IReadOnlyList<JsonObject> Operations => AgentAPI.Operations(AgentOperationCatalog.Instance);

    [Fact]
    public void EveryPublishedSchemaIsClosedAndCarriesRationale()
    {
        var ops = Operations;
        Assert.True(ops.Count >= 35);
        foreach (var op in ops)
        {
            var name = (string?)op["name"] ?? "?";
            var parameters = Assert.IsType<JsonObject>(op["params"]);
            Assert.True(parameters["additionalProperties"]?.GetValue<bool>() == false, $"{name}'s schema is open");
            var properties = Assert.IsType<JsonObject>(parameters["properties"]);
            Assert.True(properties["rationale"] is not null, $"{name} does not publish rationale");
            // What the schema lists is exactly what the check accepts.
            if (op["call"] is null && AgentPlanner.Op(name) is { } registered)
                Assert.True(properties.Select(p => p.Key).ToHashSet().SetEquals(registered.Params.Select(p => p.Name).Append("rationale")), $"{name}: schema and register differ");
        }
    }

    [Fact]
    public void TheNestedSettingsArePublishedPerTypeAndClosed()
    {
        foreach (var (name, types) in new[]
        {
            ("add_adjustment", AdjustmentKind.AllCases.Select(k => k.RawValue()).ToArray()),
            ("apply_filter", FilterKind.AllCases.Select(k => k.RawValue()).ToArray()),
            ("add_layer_effect", new[] { "dropShadow", "innerShadow", "outerGlow", "stroke", "colorOverlay" }),
        })
        {
            var op = Operations.First(o => (string?)o["name"] == name);
            var nested = Assert.IsType<JsonObject>(op["params"]!["properties"]!["params"]);
            var byType = Assert.IsType<JsonObject>(nested["x-byType"]);   // "{name}'s params is a bare object"
            Assert.True(byType.Select(p => p.Key).ToHashSet().SetEquals(types));
            foreach (var type in types)
            {
                var schema = Assert.IsType<JsonObject>(byType[type]);
                Assert.False(schema["additionalProperties"]!.GetValue<bool>());
                // The published names are the enforced names.
                Assert.True(((JsonObject)schema["properties"]!).Select(p => p.Key).ToHashSet().SetEquals(StepParameters.NestedKeys(name, type) ?? ["?"]), $"{name} {type}");
            }
        }
        // A slider's range and default are there to check against.
        var exposure = (JsonObject)StepParameters.NestedSchemas("add_adjustment")!["exposure"]!["properties"]!;
        Assert.True(exposure["exposure"]!["minimum"] is not null && exposure["exposure"]!["maximum"] is not null);
        // A model's tool list has the closed top level but not the per-type tables.
        var tool = AgentPlanner.ToolSchemas().First(t => (string?)t["function"]!["name"] == "add_adjustment");
        var parameters = (JsonObject)tool["function"]!["parameters"]!;
        Assert.False(parameters["additionalProperties"]!.GetValue<bool>());
        Assert.Null(parameters["properties"]!["params"]!["x-byType"]);
    }

    [Fact]
    public void RationaleIsAcceptedOnEveryEndpoint()
    {
        Assert.Null(StepParameters.StrayMessage(["fit", "rationale"], ["fit"], "x"));
        Assert.Null(StepParameters.StrayMessage(["rationale"], [], "x"));
    }

    /// <summary>Glimmer's own published table names its chooser.</summary>
    [Fact]
    public void GlimmersOwnTablesNameTheirChooser()
    {
        foreach (var name in new[] { "add_adjustment", "apply_filter", "add_layer_effect" })
        {
            var own = Operations.First(o => (string?)o["name"] == name);
            var parameters = own["params"]!["properties"]!["params"]!;
            Assert.True((string?)parameters["x-byTypeKey"] == "type" && parameters["x-byType"] is not null, name);
        }
    }

    [Fact]
    public void ToolListsAreFilteredLikeTheOriginal()
    {
        var all = AgentPlanner.ToolSchemas();
        Assert.Equal(AgentPlanner.Ops.Count, all.Count);
        static string Name(JsonObject t) => (string)t["function"]!["name"]!;
        Assert.DoesNotContain("develop_raw", AgentPlanner.ToolSchemas(rawLayer: false).Select(Name));
        Assert.Contains("develop_raw", all.Select(Name));
        Assert.True(AgentPlanner.ToolSchemas(shortList: true).Select(Name).All(AgentAPI.CoreOps.Contains));
        var gen = all.First(t => Name(t) == "generate_layer");
        Assert.EndsWith("(AI job: costs money and takes a while.)", (string)gen["function"]!["description"]!);
    }

    /// <summary>The sizes, for the record (the full list goes to cloud models and to siblings).</summary>
    [Fact]
    public void Sizes()
    {
        static int Bytes(object v) => System.Text.Encoding.UTF8.GetByteCount(v switch { JsonNode n => n.ToJsonString(), IEnumerable<JsonObject> l => new JsonArray(l.Select(x => (JsonNode?)x.DeepClone()).ToArray()).ToJsonString(), _ => "" });
        var full = Bytes(AgentPlanner.ToolSchemas());
        var shortList = Bytes(AgentPlanner.ToolSchemas(shortList: true));
        Assert.True(shortList < full && shortList > 1000);
    }
}
