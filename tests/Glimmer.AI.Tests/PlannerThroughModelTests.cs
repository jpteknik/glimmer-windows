// Ported from the tests of TextModelTests.swift, LocalModelTests.swift and ProviderTests.swift that go through AgentPlanner:
// the planner and review, the checked text while it arrives, the evaluation's judge, the short tool list, the plan checks that read a plan.
// (The source-scanning guards of TextModelGuardTests and OnDeviceRouter's "not sent the operations" scan read the Mac app's Swift files;
// the first is replaced by the type system and the first test of TextModelTests.cs, the second has no counterpart: Windows has no on-device router.)
using System.Text.Json.Nodes;
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

public class PlannerThroughModelTests
{
    static AgentContext Context => new(800, 600, 1, false, "pixel", "Photo");

    [Fact]
    public async Task ThePlannerGoesThroughTheTextModel()
    {
        var model = new ScriptedTextModel { Text = "Black and white.", Calls = [new("add_adjustment", """{"type":"black_white"}""")] };
        var (plan, cost) = await AgentPlanner.PlanAsync("Make it black and white", Context, [], model);
        Assert.Equal(["add_adjustment"], plan.Steps.Select(s => s.Op));
        Assert.Equal("Black and white.", plan.Message);
        Assert.Null(cost);
        var asked = model.Asked[0];
        Assert.True(asked.Kind == "tools" && asked.Temperature == 0.2 && !asked.Streamed);
        Assert.True(asked.Tools.ToHashSet().SetEquals(AgentPlanner.Ops.Select(o => o.Name).Where(n => n != "develop_raw")));
    }

    [Fact]
    public async Task ThePlannerStreamsWhenAsked()
    {
        var model = new ScriptedTextModel { Text = "Done." };
        var heard = "";
        await AgentPlanner.PlanAsync("Hello", Context, [], model, onText: t => heard = t);
        Assert.Equal("Done.", heard);
        Assert.True(model.Asked[0].Streamed);
    }

    [Fact]
    public async Task ReviewOffersOnlyFreeTools()
    {
        var model = new ScriptedTextModel { Text = "Looks right." };
        await AgentPlanner.ReviewAsync("g", [], [1], [2], model);
        var paid = AgentPlanner.Ops.Where(o => o.IsAIJob).Select(o => o.Name).ToHashSet();
        Assert.DoesNotContain(model.Asked[0].Tools, paid.Contains);
    }

    [Fact]
    public async Task ReviewKeepsAtMostThreeFreeCorrections()
    {
        var model = new ScriptedTextModel
        {
            Text = "Adjusting.",
            Calls = [new("add_adjustment", "{}"), new("auto_enhance", "{}"), new("deselect", "{}"), new("select_all", "{}"), new("generate_layer", """{"prompt":"x"}""")],
        };
        var plan = await AgentPlanner.ReviewAsync("g", [], [1], [2], model);
        Assert.Equal(["add_adjustment", "auto_enhance", "deselect"], plan.Steps.Select(s => s.Op));
    }

    [Fact]
    public async Task SuggestionsAndDistractionsAskForJSONWithThePicture()
    {
        var model = new ScriptedTextModel { Text = """{"suggestions":[]}""" };
        await AgentPlanner.SuggestionsAsync([1, 2, 3], "en", model);
        Assert.True(model.Asked[0].Kind == "reply" && model.Asked[0].Json && model.Asked[0].Images == 1 && model.Asked[0].Temperature == 0.7);
        model.Text = """{"distractions":[]}""";
        await AIPipelines.FindDistractionsAsync(new RGBAImage(8, 8, new System.Numerics.Vector4(1, 2, 3, 255)), model);
        Assert.True(model.Asked[1].Json && model.Asked[1].Images == 1 && model.Asked[1].Temperature == 0.2);
    }

    [Fact]
    public async Task FindingDistractionsReturnsTheCleanedBoxesAndTheCost()
    {
        var model = new ScriptedTextModel { Text = """{"distractions":[{"label":"pole","box":{"x":0.2,"y":0.2,"width":0.1,"height":0.4}}],"message":"One."}""" };
        var (items, message, _) = await AIPipelines.FindDistractionsAsync(new RGBAImage(8, 8, new System.Numerics.Vector4(1, 2, 3, 255)), model);
        Assert.True(items.Count == 1 && items[0].Label == "pole" && message == "One.");
    }

    [Fact]
    public async Task TheSuggestionsPromptCarriesTheLanguage()
    {
        string? seen = null;
        var model = new CapturingModel(user => seen = user) { Text = """{"suggestions":[]}""" };
        await AgentPlanner.SuggestionsAsync([1], "sv", model);
        Assert.Contains("language with code \"sv\"", seen);
    }

    sealed class CapturingModel(Action<string> onAsk) : ITextModel
    {
        public string ProviderName => "Capturing";
        public string ModelID => "c";
        public string Text = "";
        public Task<TextReply> ReplyAsync(string? system, string user, IReadOnlyList<byte[]> images, string imageType, bool json, double temperature, CancellationToken ct = default)
        { onAsk(user); return Task.FromResult(new TextReply(Text, null)); }
        public Task<TextToolReply> CallToolsAsync(IReadOnlyList<JsonObject> messages, IReadOnlyList<JsonObject> tools, double temperature, Action<string>? onText, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // ---- LocalModelTests: the judge, the figures check on a plan, the text while it arrives

    static AgentPlan Plan((string, (string, AgentValue)[])[] steps, params string[] dropped) =>
        new(steps.Select(s => new AgentStep(s.Item1, s.Item2)), null, dropped);

    [Fact]
    public void Judging()
    {
        var crop = LocalModelEvaluation.Cases[2];
        Assert.True(LocalModelEvaluation.Judge(Plan([("crop", [("aspect", "16:9")])]), crop));
        Assert.False(LocalModelEvaluation.Judge(Plan([("crop", [("aspect", "4:3")])]), crop));
        Assert.False(LocalModelEvaluation.Judge(Plan([("crop", [("aspect", "16:9"), ("shape", "wide")])]), crop));   // a made-up parameter
        Assert.False(LocalModelEvaluation.Judge(Plan([("crop", [("aspect", "16:9")])], "make_wide"), crop));          // a made-up tool
        Assert.False(LocalModelEvaluation.Judge(Plan([]), crop));
        var half = LocalModelEvaluation.Cases[9];
        Assert.True(LocalModelEvaluation.Judge(Plan([("resize_image", [("scale", 0.5)])]), half));
        Assert.False(LocalModelEvaluation.Judge(Plan([("resize_image", [("scale", 2)])]), half));
        // The test document is 2400 × 1600: half of it is also a width of 1200, a height of 800, or both.
        Assert.True(LocalModelEvaluation.Judge(Plan([("resize_image", [("width", 1200)])]), half));
        Assert.True(LocalModelEvaluation.Judge(Plan([("resize_image", [("width", 1200), ("height", 800)])]), half));
        Assert.False(LocalModelEvaluation.Judge(Plan([("resize_image", [("width", 600)])]), half));
        Assert.False(LocalModelEvaluation.Judge(Plan([("resize_image", [("width", 1200), ("height", 1600)])]), half));
    }

    [Fact]
    public async Task AModelThatAnswersNothingFailsEveryCase()
    {
        var report = await LocalModelEvaluation.RunAsync(new ScriptedTextModel(), LocalModelEvaluation.Cases.Take(2).ToList());
        Assert.True(report.Passed == 0 && !report.Passes);
        Assert.Equal("scripted-1", report.Model);
        Assert.StartsWith("error: The assistant returned an empty answer.", report.Outcomes[0].Got);   // nothing said, nothing called
    }

    [Fact]
    public async Task AModelThatAnswersRightPassesTheCaseItWasGiven()
    {
        var model = new ScriptedTextModel { Calls = [new("add_adjustment", """{"type":"black_white","params":{}}""")] };
        var seen = new List<LocalModelEvaluation.Outcome>();
        var report = await LocalModelEvaluation.RunAsync(model, LocalModelEvaluation.Cases.Take(1).ToList(), seen.Add);
        Assert.True(report.Outcomes[0].Passed && seen.Count == 1);
        Assert.Contains("add_adjustment(params: …, type: black_white)", report.Outcomes[0].Got);
    }

    [Fact]
    public void WhenNothingIsLeftGlimmerStatesWhatItKnows()
    {
        var context = new AgentContext(2400, 1600, 1, false, "pixel", "Photo");
        var plan = new AgentPlan([], "It is 4000 by 3000 pixels.");
        plan = AgentPlanner.CheckFigures(plan, "How big is it?", context, [], null);
        Assert.Equal("The document is 2400 × 1600 pixels and has 1 layer.", plan.Message);
        Assert.Equal(["number 4000", "number 3000"], plan.RemovedFigures.ToArray());
        // The evaluation fails such an answer, and passes one that matches or has no figure.
        var test = LocalModelEvaluation.Cases.First(c => c.AnswerOnly);
        Assert.False(LocalModelEvaluation.Judge(plan, test));
        Assert.True(LocalModelEvaluation.Judge(new AgentPlan([], "2400 × 1600 pixels."), test));
        Assert.False(LocalModelEvaluation.Judge(new AgentPlan([new AgentStep("resize_image")]), test));
    }

    [Fact]
    public void ASuggestionKeepsItsProposalButNeverAPrice()
    {
        var reply = """{"suggestions":[{"title":"Crop 4:5","goal":"Crop to 4:5 around the face."},{"title":"Upscale","goal":"Upscale it for only $0.05."}]}""";
        Assert.Equal(["Crop 4:5"], AgentPlanner.ParseSuggestions(reply).Select(s => s.Title));
    }

    [Fact]
    public void ACostClaimWithoutAFigureIsTheKnownGapAndTheVerdictLeavesItOut()
    {
        var gap = LocalModelEvaluation.Cases.First(c => c.KnownGap);
        Assert.False(LocalModelEvaluation.Judge(new AgentPlan([], "Yes, generating is free."), gap));
        Assert.True(LocalModelEvaluation.Judge(new AgentPlan([], "The `generate_layer` function is an AI job that costs money."), gap));
        Assert.True(LocalModelEvaluation.Judge(new AgentPlan([], "No, it is not free."), gap));
        Assert.True(LocalModelEvaluation.Judge(new AgentPlan([], "Prices are the service's; the model and its price are shown in Settings › AI."), gap));
        var outcomes = Enumerable.Range(0, 12).Select(_ => new LocalModelEvaluation.Outcome("g", "en", 1, true, "")).Append(new LocalModelEvaluation.Outcome("gap", "en", 1, false, "") { KnownGap = true });
        var report = new LocalModelEvaluation.Report("m", outcomes);
        Assert.True(report.Passed == 12 && report.Scored.Count == 12 && report.GapLine.Contains("0 of 1"));
    }

    [Fact]
    public async Task ThePlannerHandsOnOnlyCheckedTextWhileItArrives()
    {
        var model = new ScriptedTextModel
        {
            Streamed = ["The document is 99", "The document is 9999 pixels wide. ", "The document is 9999 pixels wide. Done"],
            Text = "The document is 9999 pixels wide. Done.",
        };
        var seen = new List<string>();
        var context = new AgentContext(2400, 1600, 1, false, "pixel", "Photo");
        var (plan, _) = await AgentPlanner.PlanAsync("How wide is it?", context, [], model, onText: seen.Add);
        Assert.DoesNotContain(seen, s => s.Contains('9'));
        Assert.True(plan.Message == "Done." && plan.RemovedFigures.SequenceEqual(["number 9999"]));
    }

    // ---- ShortToolListTests

    /// <summary>
    /// What a small model is sent. The series rule (2026-11-16, after Jämmer's on-device assistant fell from 14 of 14 to 10 of 14
    /// unmeasured when its list grew): the list sent to a small model does not change without a recorded decision and a new evaluation run.
    /// Decision 2026-11-15 (abe4a84): crop, resize_image and set_layer took the places of apply_filter, apply_preset and auto_color;
    /// eleven tools before and after. Measured with this list 2026-11-16: qwen3:8b.
    /// </summary>
    static readonly string[] Decided =
    [
        "add_adjustment", "add_text", "add_waveform", "auto_enhance", "crop", "generate_layer",
        "image_edit_composite", "remove_background", "resize_image", "select_subject", "set_layer",
    ];

    /// <summary>The size of that list as JSON when it was measured on the Mac, and the room a reworded description may take.</summary>
    const int MeasuredBytes = 9992;
    const int Room = 300;

    [Fact]
    public void TheListSentToASmallModelIsTheOneThatWasMeasured()
    {
        var tools = AgentPlanner.ToolSchemas(shortList: true);
        var names = tools.Select(t => (string)t["function"]!["name"]!).Order(StringComparer.Ordinal).ToList();
        Assert.True(names.SequenceEqual(Decided), "The short list changed. Run the evaluation for every model offered by default, record the decision and the numbers here, then update this list.");
        var bytes = System.Text.Encoding.UTF8.GetByteCount(Glimmer.AI.Json.SerializeSorted(new JsonArray(tools.Select(t => (JsonNode?)t.DeepClone()).ToArray())));
        Assert.True(bytes <= MeasuredBytes + Room, $"The short list is {bytes} bytes; it was measured at {MeasuredBytes}. A longer list needs a new evaluation run and a new number here.");
    }

    [Fact]
    public void ARequestThatNamesNoEditMustChangeNothing()
    {
        var poem = LocalModelEvaluation.Cases.First(c => c.ChangesNothing);
        Assert.True(LocalModelEvaluation.Judge(new AgentPlan([], "The sea is wide…"), poem));
        // A plan for it is tolerated only because it waits for Run: the request doesn't name its step.
        var written = new AgentPlan([new AgentStep("add_text", ("text", "The sea is wide"))]);
        Assert.True(!written.RunsAtOnce(poem.Goal) && LocalModelEvaluation.Judge(written, poem));
        // "Write a poem about the sea" with the text "sea" would be named: the whole text is in the request.
        Assert.True(new AgentPlan([new AgentStep("add_text", ("text", "sea"))]).RunsAtOnce(poem.Goal));
    }

    // ---- PlanCheckTests that read a plan

    [Fact]
    public void AutoEnhanceBesideWhatWasAskedIsOfferedAndTheRestRuns()
    {
        var padded = new AgentPlan([new AgentStep("auto_enhance"), new AgentStep("add_adjustment", ("type", "black_white"))]);
        var split = padded.WithoutExtras("Make it black and white")!.Value;
        Assert.True(split.Runs.Steps.Select(s => s.Op).SequenceEqual(["add_adjustment"]) && split.Offered.Select(s => s.Op).SequenceEqual(["auto_enhance"]));
        Assert.True(split.Runs.RunsAtOnce("Make it black and white"));
        // The evaluation's must-run-at-once case accepts that, and still wants the right step.
        var test = LocalModelEvaluation.Cases.First(c => c.MustRunAtOnce);
        Assert.True(LocalModelEvaluation.Judge(padded, test));
        Assert.False(LocalModelEvaluation.Judge(new AgentPlan([new AgentStep("auto_enhance"), new AgentStep("add_adjustment", ("type", "levels"))]), test));
    }

    [Fact]
    public void AnythingElseUnaskedKeepsTheWholePlanWaiting()
    {
        // Another unnamed step: no split.
        Assert.Null(new AgentPlan([new AgentStep("crop", ("aspect", "1:1")), new AgentStep("add_adjustment", ("type", "black_white"))]).WithoutExtras("Make it black and white"));
        // Only the extra, nothing that was asked: it waits as a plan, it is not run and not split.
        Assert.Null(new AgentPlan([new AgentStep("auto_enhance")]).WithoutExtras("Write a poem about the sea"));
        Assert.Null(new AgentPlan([new AgentStep("auto_enhance"), new AgentStep("select_subject")]).WithoutExtras("select the subject"));
        // A plan that is named throughout has nothing to split.
        Assert.Null(new AgentPlan([new AgentStep("auto_enhance")]).WithoutExtras("Improve the photo"));
        // The no-edit cases still require that nothing changes without confirmation.
        var poem = LocalModelEvaluation.Cases.First(c => c.ChangesNothing);
        Assert.True(LocalModelEvaluation.Judge(new AgentPlan([new AgentStep("auto_enhance")]), poem));
    }

    [Fact]
    public void AMissingKindIsFilledInOnThePlanAndTheStepSaysSo()
    {
        var adjustments = AdjustmentKind.AllCases.Select(k => k.RawValue()).ToList();
        var filters = FilterKind.AllCases.Select(k => k.RawValue()).ToList();
        var plan = new AgentPlan([new AgentStep("add_adjustment", rationale: "Why.")]);
        var (done, filled) = plan.Completed("Make it black and white", adjustments, filters);
        Assert.True(filled == 1 && done.Steps[0].String("type") == "black_white" && done.Steps[0].Rationale == "Why. (type taken from your request)");
        Assert.True(done.RunsAtOnce("Make it black and white"));
        Assert.Equal(0, plan.Completed("make it brighter", adjustments, filters).Filled);
    }

    // ---- ProviderTests: the planner through the adapters of the services that are not OpenRouter

    [Fact]
    public async Task ThePlannerThroughTheChatAdaptersAsksForEveryToolButDevelopRawOnAnOrdinaryLayer()
    {
        var answer = """{"id":"x","choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":"Black and white.","tool_calls":[{"id":"call_1","type":"function","function":{"name":"add_adjustment","arguments":"{\"type\":\"black_white\"}"}}]}}]}""";
        var fake = new FakeProvider((200, answer));
        var p = AIProvider.Named("berget")!;
        var model = new OpenRouterTextModel(new OpenRouterClient("bk", p.ChatConfiguration(), fake), "some-model", p.Name);
        var (plan, cost) = await AgentPlanner.PlanAsync("Make it black and white", Context, [], model);
        Assert.True(plan.Steps.Select(s => s.Op).SequenceEqual(["add_adjustment"]) && plan.Message == "Black and white." && cost is null);
        Assert.Equal(AgentPlanner.ToolSchemas(rawLayer: false).Count, fake.First.Json["tools"]!.AsArray().Count);   // an ordinary layer: every tool but develop_raw
    }

    [Fact]
    public async Task OpenAIGetsAResponsesRequestAndItsToolCallsBecomeThePlan()
    {
        var answer = """
            {"id":"resp_1","object":"response","status":"completed","model":"m","output":[
              {"type":"reasoning","id":"rs_1","summary":[]},
              {"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"Black and white, then cropped."}]},
              {"id":"fc_1","call_id":"call_1","type":"function_call","name":"add_adjustment","arguments":"{\"type\":\"black_white\"}"},
              {"id":"fc_2","call_id":"call_2","type":"function_call","name":"crop","arguments":"{\"aspect\":\"16:9\"}"}],
             "usage":{"input_tokens":900,"output_tokens":40,"total_tokens":940}}
            """;
        var fake = new FakeProvider((200, answer));
        var model = AIProvider.Named("openai")!.TextModel("sk-test", "some-model", handler: fake)!;
        var (plan, cost) = await AgentPlanner.PlanAsync("Black and white, 16:9", Context, [("earlier", "done")], model, image: [7]);
        Assert.True(plan.Steps.Select(s => s.Op).SequenceEqual(["add_adjustment", "crop"]) && plan.Message == "Black and white, then cropped." && cost is null);
        var sent = fake.First;
        Assert.True(sent.Json["stream"] is null && sent.Url.ToString() == "https://api.openai.com/v1/responses");
        var input = sent.Json["input"]!.AsArray();
        Assert.True((string?)input[0]!["role"] == "system" && input.Count == 4);
        var parts = input[^1]!["content"]!.AsArray();
        Assert.Contains(parts, x => (string?)x!["type"] == "input_text");
        Assert.Contains(parts, x => (string?)x!["type"] == "input_image" && ((string?)x["image_url"])!.StartsWith("data:image/"));
    }

    [Fact]
    public async Task ARefusalFromTheResponsesAPIIsShownAsItCameThroughThePlanner()
    {
        var fake = new FakeProvider((400, """{"error":{"message":"The model `gpt-0` does not exist.","type":"invalid_request_error","code":"model_not_found"}}"""));
        var model = AIProvider.Named("openai")!.TextModel("k", "gpt-0", handler: fake)!;
        var error = await Assert.ThrowsAsync<ProviderHttpError>(() => AgentPlanner.PlanAsync("g", Context, [], model));
        Assert.Equal("OpenAI answered HTTP 400: The model `gpt-0` does not exist.", error.Message);
    }

    [Fact]
    public async Task ClaudeGetsAMessagesRequestAndItsToolUseBecomesThePlan()
    {
        var answer = """
            {"id":"msg_1","type":"message","role":"assistant","model":"m","stop_reason":"tool_use","content":[
              {"type":"thinking","thinking":"","signature":"x"},
              {"type":"text","text":"Black and white."},
              {"type":"tool_use","id":"toolu_1","name":"add_adjustment","input":{"type":"black_white"}}],
             "usage":{"input_tokens":900,"output_tokens":40}}
            """;
        var fake = new FakeProvider((200, answer));
        var model = AIProvider.Named("anthropic")!.TextModel("sk-ant-test", "some-claude", handler: fake)!;
        var (plan, cost) = await AgentPlanner.PlanAsync("Black and white", Context, [("earlier", "done")], model, image: [7]);
        Assert.True(plan.Steps.Select(s => s.Op).SequenceEqual(["add_adjustment"]) && plan.Steps[0].String("type") == "black_white");
        Assert.True(plan.Message == "Black and white." && cost is null);
        var sent = fake.First;
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.Url.ToString());
        Assert.Equal(["user", "assistant", "user"], sent.Json["messages"]!.AsArray().Select(t => (string?)t!["role"]));
    }

    [Fact]
    public async Task ADeclinedRequestSaysSoAndChangesNothingThroughThePlanner()
    {
        var fake = new FakeProvider((200, """{"id":"msg_2","type":"message","role":"assistant","stop_reason":"refusal","stop_details":{"type":"refusal","category":"cyber"},"content":[]}"""));
        var model = AIProvider.Named("anthropic")!.TextModel("k", "m", handler: fake)!;
        var error = await Assert.ThrowsAsync<AnthropicTextModel.Refusal>(() => AgentPlanner.PlanAsync("g", Context, [], model));
        Assert.Equal("Claude declined this request (cyber). Nothing was changed.", error.Message);
    }

    [Fact]
    public async Task MlxPlansWithAPlainRequest()
    {
        var fake = new FakeProvider((200, """{"choices":[{"message":{"role":"assistant","content":"Done.","tool_calls":[{"id":"c1","type":"function","function":{"name":"crop","arguments":"{\"aspect\":\"1:1\"}"}}]}}]}"""));
        var model = AIProvider.Named("mlx")!.TextModel("", "mlx-community/some-model", handler: fake)!;
        var (plan, cost) = await AgentPlanner.PlanAsync("Square", Context, [], model);
        Assert.True(plan.Steps.Select(s => s.Op).SequenceEqual(["crop"]) && cost is null);
        Assert.Null(fake.First.Json["tool_choice"]);
    }

    [Fact]
    public async Task ThePlannerRequestToOllamaIsNativeShortAndStatesItsContext()
    {
        var answer = """{"model":"qwen3","created_at":"2026-11-16T00:00:00Z","message":{"role":"assistant","content":"","tool_calls":[{"type":"function","function":{"index":0,"name":"crop","arguments":{"aspect":"16:9"}}}]},"done":true}""";
        var fake = new FakeProvider((200, answer));
        var model = new OllamaTextModel("qwen3:8b", new Uri("http://127.0.0.1:11434/v1"), handler: fake);
        var (plan, cost) = await AgentPlanner.PlanAsync("Crop to 16:9", Context, [], model, image: [1, 2, 3]);
        Assert.True(plan.Steps.Select(s => s.Op).SequenceEqual(["crop"]) && plan.Steps[0].String("aspect") == "16:9" && cost is null);
        var sent = fake.First;
        Assert.Equal("http://127.0.0.1:11434/api/chat", sent.Url.ToString());
        Assert.Equal(16_384, (int?)sent.Json["options"]!["num_ctx"]);
        // Only the everyday operations, and no sibling tools.
        var names = sent.Json["tools"]!.AsArray().Select(t => (string)t!["function"]!["name"]!).ToHashSet();
        Assert.True(names.SetEquals(AgentPlanner.Ops.Select(o => o.Name).Where(AgentAPI.CoreOps.Contains)) && names.Count is >= 8 and <= 15);
    }

    [Fact]
    public async Task AModelWithoutToolsFailsWithOllamasOwnWordsThroughThePlanner()
    {
        var fake = new FakeProvider((400, """{"error":"registry.ollama.ai/library/gemma3:4b does not support tools"}"""));
        var model = new OllamaTextModel("gemma3:4b", handler: fake);
        var error = await Assert.ThrowsAsync<ProviderHttpError>(() => AgentPlanner.PlanAsync("g", Context, [], model));
        Assert.Equal("Ollama answered HTTP 400: registry.ollama.ai/library/gemma3:4b does not support tools", error.Message);
    }
}
