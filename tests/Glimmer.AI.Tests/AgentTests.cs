// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/AgentTests.swift
using System.Text.Json;
using System.Text.Json.Nodes;
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

public class AgentPlannerTests
{
    [Fact]
    public void PromptListsEveryOpAndItsOptions()
    {
        var prompt = AgentPlanner.SystemPrompt();
        foreach (var op in AgentPlanner.Ops) Assert.Contains($"- {op.Name}", prompt);
        Assert.Contains("gaussian_blur", prompt);
        Assert.Contains("hue_saturation", prompt);
        Assert.Contains(Preset.All[0].Name, prompt);
        Assert.True(prompt.Contains("layer.duplicate") || prompt.Contains(AgentPlanner.RunnableCommands[0].Id.RawValue));
    }

    [Fact]
    public void ParsesFencedJSONAndDropsUnknownOps()
    {
        var reply = """
            Sure! Here is the plan:
            ```json
            {"steps": [
              {"op": "add_adjustment", "params": {"type": "black_white", "params": {}}, "rationale": "Mono look."},
              {"op": "teleport", "params": {}},
              "garbage",
              {"op": "apply_filter", "params": {"type": "vignette", "params": {"amount": 0.4}}}
            ], "message": "Black & white with a vignette {braces in strings are fine}."}
            ```
            """;
        var plan = AgentPlanner.ParsePlan(reply);
        Assert.Equal(["add_adjustment", "apply_filter"], plan.Steps.Select(s => s.Op));
        Assert.Equal(["teleport"], plan.DroppedOps.ToArray());
        Assert.Equal(0.4, plan.Steps[1].Object("params")["amount"].AsNumber);
        Assert.Equal("black_white", plan.Steps[0].String("type"));
        Assert.Contains("braces", plan.Message);
        Assert.False(plan.UsesAI);
    }

    [Fact]
    public void ProseBecomesAChatAnswer()
    {
        var plan = AgentPlanner.ParsePlan("Curves lets you shape tones.");
        Assert.True(plan.Steps.IsEmpty && plan.Message == "Curves lets you shape tones.");
    }

    [Fact]
    public void AiStepsAreFlagged()
    {
        var plan = AgentPlanner.ParsePlan("""{"steps":[{"op":"generate_layer","params":{"prompt":"a fox"}}]}""");
        Assert.True(plan.UsesAI);
    }

    [Fact]
    public void UserPromptCarriesContextAndHistory()
    {
        var ctx = new AgentContext(800, 600, 3, true, "raster", "Photo");
        var p = AgentPlanner.UserPrompt("warmer", ctx, [("make it b&w", "Added Black & White.")]);
        Assert.True(p.Contains("800×600") && p.Contains("hasSelection: true") && p.Contains("make it b&w") && p.EndsWith("Return the JSON plan now."));
    }

    [Fact]
    public void HexColours()
    {
        Assert.Equal(new SRGBColor(1, 128.0 / 255, 0), HexColors.Parse("#ff8000"));
        Assert.Equal(new SRGBColor(0, 1, 0), HexColors.Parse("00ff00cc"));
        Assert.Null(HexColors.Parse("red"));
    }

    [Fact]
    public void ToolSchemasCoverEveryOp()
    {
        var tools = AgentPlanner.ToolSchemas();
        Assert.Equal(AgentPlanner.Ops.Count, tools.Count);
        var text = new JsonArray(tools.Select(t => (JsonNode?)t.DeepClone()).ToArray()).ToJsonString();
        Assert.Contains("\"enum\"", text);
        JsonObject Function(string name) => (JsonObject)tools.First(t => (string?)t["function"]!["name"] == name)["function"]!;
        var required = ((JsonArray)Function("apply_filter")["parameters"]!["required"]!).Select(n => (string?)n).ToList();
        Assert.Contains("type", required);
        var textRequired = ((JsonArray)Function("add_text")["parameters"]!["required"]!).Select(n => (string?)n).ToList();
        Assert.True(textRequired.Contains("text") && !textRequired.Contains("color") && !textRequired.Contains("size"));
    }

    /// <summary><c>develop_raw</c> is offered to the assistant only for a RAW layer, and never in the short list.</summary>
    [Fact]
    public void DevelopRawIsOfferedOnlyForARawLayer()
    {
        static List<string> Names(IEnumerable<JsonObject> tools) => tools.Select(t => (string)t["function"]!["name"]!).ToList();
        Assert.DoesNotContain("develop_raw", Names(AgentPlanner.ToolSchemas(rawLayer: false)));
        Assert.DoesNotContain("develop_raw", Names(AgentPlanner.ToolSchemas(shortList: true, rawLayer: true)));
        Assert.Equal(AgentAPI.HasRAW, Names(AgentPlanner.ToolSchemas(rawLayer: true)).Contains("develop_raw"));
        // The unseen cases are judged as written: the right step for a RAW layer, and never develop_raw for an ordinary one.
        var brighter = LocalModelEvaluation.RawCases[0];
        var ordinary = LocalModelEvaluation.RawCases[2];
        var develop = new AgentPlan([new AgentStep("develop_raw", ("exposure", 1))]);
        var adjust = new AgentPlan([new AgentStep("add_adjustment", ("type", "exposure"))]);
        Assert.True(LocalModelEvaluation.Judge(develop, brighter) && !LocalModelEvaluation.Judge(adjust, brighter));
        Assert.True(LocalModelEvaluation.Judge(adjust, ordinary) && !LocalModelEvaluation.Judge(develop, ordinary));
        Assert.True(LocalModelEvaluation.Cases.All(c => !c.RawLayer && c.MustNotUse is null));   // the scored set is unchanged
    }

    [Fact]
    public void ToolCallsBecomeSteps()
    {
        var response = new TextToolReply("Black and white with a vignette.",
        [
            new("add_adjustment", """{"type":"black_white","params":{},"rationale":"Monochrome."}"""),
            new("apply_filter", """{"type":"vignette","params":"{\"amount\":0.4}"}"""),
            new("teleport", "{}"),
        ], null);
        var plan = AgentPlanner.PlanFrom(response);
        Assert.Equal(["add_adjustment", "apply_filter"], plan.Steps.Select(s => s.Op));
        Assert.Equal("Monochrome.", plan.Steps[0].Rationale);
        Assert.False(plan.Steps[0].Params.ContainsKey("rationale"));
        Assert.Equal(0.4, plan.Steps[1].Object("params")["amount"].AsNumber);
        Assert.Equal(["teleport"], plan.DroppedOps.ToArray());
        Assert.Equal("Black and white with a vignette.", plan.Message);
    }

    [Fact]
    public void ToolReplyWithoutCallsIsAChatAnswer()
    {
        var plan = AgentPlanner.PlanFrom(new TextToolReply("Which layer do you mean?", [], null));
        Assert.True(plan.Steps.IsEmpty && plan.Message == "Which layer do you mean?");
        Assert.Throws<AgentError>(() => AgentPlanner.PlanFrom(new TextToolReply(" ", [], null)));
    }

    [Fact]
    public void ToolMessagesCarryHistoryAndState()
    {
        var context = new AgentContext(800, 600, 2, true, "raster", "Photo");
        var messages = AgentPlanner.ToolMessages("warmer", context, [("hi", "Hello!")]);
        Assert.Equal(4, messages.Count);
        Assert.True((string?)messages[1]["content"] == "hi" && (string?)messages[2]["role"] == "assistant");
        var last = (string?)messages[3]["content"] ?? "";
        Assert.True(last.Contains("800×600") && last.Contains("there is a selection") && last.EndsWith("warmer"));
    }

    [Fact]
    public void ExtractsToolCallsFromAResponse()
    {
        var json = TJson.Obj("""
            {"choices": [{"message": {"content": null, "tool_calls": [
              {"id": "1", "type": "function", "function": {"name": "select_all", "arguments": "{}"}},
              {"id": "2", "type": "function", "function": {"name": "fill", "arguments": {"color": "#ff0000"}}}]}}]}
            """);
        var calls = OpenRouterClient.ExtractToolCalls(json);
        Assert.Equal(["select_all", "fill"], calls.Select(c => c.Name));
        Assert.Contains("#ff0000", calls[1].Arguments);
    }
}

public class AgentVisionAndStreamingTests
{
    [Fact]
    public void ImageRidesAlongWithTheGoal()
    {
        var context = new AgentContext(8, 8, 1, false, "raster", "A");
        var messages = AgentPlanner.ToolMessages("warmer", context, [], image: [1, 2, 3]);
        var parts = Assert.IsType<JsonArray>(messages[^1]["content"]);
        Assert.True(parts.Count == 2 && ((string?)parts[0]!["text"])!.EndsWith("warmer"));
        Assert.Equal("data:image/jpeg;base64,AQID", (string?)parts[1]!["image_url"]!["url"]);
    }

    [Fact]
    public void ReviewGetsBothPicturesAndNoPaidTools()
    {
        var m = AgentPlanner.ReviewMessages("darker", [new AgentStep("add_adjustment", ("type", "exposure"))], [1], [2]);
        var parts = Assert.IsType<JsonArray>(m[1]["content"]);
        Assert.True(parts.Count == 3 && ((string?)parts[0]!["text"])!.Contains("add_adjustment"));
        var names = AgentPlanner.ReviewToolSchemas().Select(t => (string)t["function"]!["name"]!).ToList();
        Assert.True(names.Contains("add_adjustment") && !names.Contains("generate_layer") && !names.Contains("inpaint_selection") && !names.Contains("image_edit_composite"));
    }

    [Fact]
    public void StreamedChunksAreMerged()
    {
        var acc = new OpenRouterClient.StreamAccumulator();
        static JsonObject Chunk(JsonObject delta) => new() { ["choices"] = new JsonArray(new JsonObject { ["delta"] = delta }) };
        Assert.Equal("Black ", acc.Consume(Chunk(new JsonObject { ["content"] = "Black " })));
        Assert.Null(acc.Consume(Chunk(TJson.Obj("""{"tool_calls": [{"index": 0, "function": {"name": "add_adjustment", "arguments": "{\"type\":"}}]}"""))));
        acc.Consume(Chunk(TJson.Obj("""{"tool_calls": [{"index": 0, "function": {"arguments": "\"black_white\"}"}}, {"index": 1, "function": {"name": "deselect", "arguments": ""}}]}""")));
        acc.Consume(Chunk(new JsonObject { ["content"] = "and white." }));
        acc.Consume(TJson.Obj("""{"choices": [], "usage": {"cost": 0.002}}"""));
        var r = acc.Response;
        Assert.True(r.Text == "Black and white." && r.Cost == 0.002);
        Assert.Equal([new TextToolCall("add_adjustment", "{\"type\":\"black_white\"}"), new TextToolCall("deselect", "{}")], r.ToolCalls);
    }
}

public class SuggestionTests
{
    [Fact]
    public void ParsesAndLimitsSuggestions()
    {
        var text = """
            ```json
            {"suggestions": [{"title": "Warm Glow", "goal": "Warm the sunset tones."}, {"title": "Warm Glow", "goal": "dup"},
             {"title": "", "goal": "x"}, {"title": "Moody B&W", "goal": "Convert to high-contrast black and white."},
             {"title": "Sharpen Bridge", "goal": "Sharpen the bridge."}, {"title": "Fourth", "goal": "no"}]}
            ```
            """;
        var s = AgentPlanner.ParseSuggestions(text);
        Assert.Equal(["Warm Glow", "Moody B&W", "Sharpen Bridge"], s.Select(x => x.Title));
        Assert.Empty(AgentPlanner.ParseSuggestions("sorry"));
    }
}
