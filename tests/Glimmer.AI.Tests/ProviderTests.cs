// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/ProviderTests.swift
// (the parts that test the register, the keys, the model lists, the chat adapters and KeyStaysHome;
// the original ran the adapters through AgentPlanner, here they are called directly)
using System.Text;
using System.Text.Json.Nodes;
using Glimmer.AI;

namespace Glimmer.AI.Tests;

public class ProviderRegisterTests
{
    /// The slugs the series agreed on (the-apps/PROVIDERS.md, rule 1), for what Glimmer uses.
    [Fact]
    public void SlugsAreTheSeriesOwn()
    {
        Assert.Equal(new[] { "openrouter", "openai", "anthropic", "xai", "google", "berget", "grunden", "fal", "hedra", "ollama", "mlx" }, AIProvider.All.Select(p => p.Slug));
        Assert.Equal(AIProvider.All.Count, AIProvider.All.Select(p => p.Slug).Distinct().Count());
        foreach (var p in AIProvider.All)
        {
            Assert.True(p.Slug == p.Slug.ToLowerInvariant() && !p.Slug.Contains(' '));
            Assert.True(Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var url) && url.Host.Length > 0, p.Slug);
            Assert.True(p.Serves.Contains(AIProvider.Capability.Text) == (p.TextAdapter is not null), p.Slug);
        }
    }

    /// Every cloud service that is built is offered (Anders, 2026-11-16), and those nobody has called say so.
    [Fact]
    public void OnlyWhatIsDecidedIsOffered()
    {
        Assert.Equal(new[] { "openrouter", "openai", "anthropic", "xai", "google", "berget", "grunden", "ollama" }, AIProvider.Offering(AIProvider.Capability.Text).Select(p => p.Slug));
        Assert.Equal(new[] { "openrouter", "openai", "google", "fal", "hedra" }, AIProvider.Offering(AIProvider.Capability.Image).Select(p => p.Slug));
        foreach (var p in AIProvider.All.Where(p => p.Kind == AIProvider.KindOf.Cloud))
        {
            Assert.True(p.Offered.SetEquals(p.Built), p.Slug);
            Assert.Equal(!new[] { "openrouter", "berget" }.Contains(p.Slug), p.FromDocumentationOnly);
        }
        foreach (var p in AIProvider.All)
        {
            Assert.True(p.Offered.IsSubsetOf(p.Built), $"{p.Slug} is offered for something that isn't built");
            Assert.True(p.Built.IsSubsetOf(p.Serves), p.Slug);
        }
        // A local server is offered only with a model that has passed Glimmer's evaluation named.
        foreach (var p in AIProvider.All.Where(p => p.Kind == AIProvider.KindOf.LocalServer))
            Assert.Equal(p.EvaluatedModels.Count == 0, p.Offered.Count == 0);
        Assert.Equal(new[] { "qwen3:8b" }, AIProvider.Named("ollama")!.EvaluatedModels);
        Assert.Empty(AIProvider.Named("mlx")!.Offered);
    }

    [Fact]
    public void ALocalServerNeedsNoKeyAndFalIsUntouched()
    {
        Assert.False(AIProvider.Named("ollama")!.NeedsKey);
        Assert.False(AIProvider.Named("ollama")!.Dialect.ToolChoice);
        Assert.True(AIProvider.Named("fal")!.Built.SetEquals(new[] { AIProvider.Capability.Image }));
        Assert.True(AIProvider.Named("fal")!.Offered.SetEquals(new[] { AIProvider.Capability.Image }));
        Assert.Contains("yourself", AIProvider.Named("hedra")!.TermsNote);
    }

    [Fact]
    public void TheManualTableListsWhatIsOfferedAndSaysWhatIsUntried()
    {
        var table = AIProvider.ManualTable();
        Assert.True(table.Contains("| OpenRouter |") && table.Contains("| Berget |") && table.Contains("| Grunden |"));
        Assert.True(table.Contains("| Ollama |") && table.Contains("shown only while it is running"));
        Assert.True(table.Contains("| Hedra | images |") && table.Contains("| OpenAI | text (the Assistant), images |") && !table.Contains("MLX"));
        // Berget has had a real call; those nobody has called say so.
        Assert.DoesNotContain("Built from Berget's documentation", table);
        Assert.Contains("Built from Grunden's documentation; not yet tried with a real call.", table);
        Assert.Contains("Built from fal.ai's documentation; not yet tried with a real call.", table);
        Assert.Contains("Stockholm, Sweden", table);
    }

    [Fact]
    public void TheChatConfigurationCarriesWhatTheServiceAccepts()
    {
        var google = AIProvider.Named("google")!.ChatConfiguration();
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai", google.BaseUrl.ToString());
        Assert.False(google.Dialect.ParallelToolCalls);
        Assert.False(google.Dialect.JsonObject);
        Assert.False(google.Dialect.UsageInclude);
        Assert.Equal("Google", google.ProviderName);
        Assert.Null(AIProvider.OpenRouter.ChatConfiguration().ProviderName);
        Assert.True(AIProvider.OpenRouter.ChatConfiguration().Dialect.UsageInclude);
        Assert.Equal("low", AIProvider.Named("grunden")!.Dialect.ReasoningEffort);
        Assert.False(AIProvider.Named("ollama")!.ChatConfiguration().NeedsKey);
        Assert.Equal("https://gateway.example/v1", AIProvider.OpenRouter.ChatConfiguration(new Uri("https://gateway.example/v1")).BaseUrl.ToString());
        Assert.Equal("Add your Berget API key in Settings › AI.", AIProvider.Named("berget")!.MissingKeyMessage);
        Assert.Equal("api.berget.ai", AIProvider.Named("berget")!.Host);
    }
}

public class ProviderKeyTests
{
    /// A key store made of a dictionary; a failing account's write or delete throws.
    private sealed class Box
    {
        public readonly object Gate = new();
        public Dictionary<string, string> Items = new();
        public HashSet<string> FailWrite = new(), FailDelete = new(), DropWrite = new();
        public sealed class Failure : Exception { }
    }

    private sealed class Item : ISecretStore
    {
        private readonly Box box;
        private readonly string account;
        public Item(Box box, string account) { this.box = box; this.account = account; }
        public string? Read() { lock (box.Gate) return box.Items.GetValueOrDefault(account); }
        public void Write(string value)
        {
            lock (box.Gate)
            {
                if (box.FailWrite.Contains(account)) throw new Box.Failure();
                if (!box.DropWrite.Contains(account)) box.Items[account] = value;
            }
        }
        public void Delete()
        {
            lock (box.Gate)
            {
                if (box.FailDelete.Contains(account)) throw new Box.Failure();
                box.Items.Remove(account);
            }
        }
    }

    private static ProviderKeys Keys(Box box) => new(account => new Item(box, account));

    [Fact]
    public void TheOldOpenRouterItemMovesToTheSlug()
    {
        var box = new Box { Items = { [ProviderKeys.OldOpenRouterAccount] = "sk-or-test" } };
        Assert.Equal("sk-or-test", Keys(box).Key("openrouter"));
        Assert.Equal(new Dictionary<string, string> { ["openrouter"] = "sk-or-test" }, box.Items);
        Assert.Equal("sk-or-test", Keys(box).Key("openrouter"));
    }

    [Fact]
    public void AKeyIsStoredUnderItsSlugAndOthersAreUntouched()
    {
        var box = new Box { Items = { ["openrouter"] = "a" } };
        Keys(box).StoreFor("berget").Write("b");
        Assert.Equal("b", Keys(box).Key("berget"));
        Assert.Null(Keys(box).Key("grunden"));
        Assert.Equal(new Dictionary<string, string> { ["openrouter"] = "a", ["berget"] = "b" }, box.Items);
        Assert.Equal(new Dictionary<string, bool>
        {
            ["openrouter"] = true, ["berget"] = true, ["grunden"] = false, ["openai"] = false, ["anthropic"] = false, ["xai"] = false,
            ["google"] = false, ["fal"] = false, ["hedra"] = false,
        }, Keys(box).Status());
    }

    /// Interrupted before the new item is written: the key is still under the old account and is found.
    [Fact]
    public void InterruptedBeforeTheNewItemIsWritten()
    {
        var box = new Box { Items = { [ProviderKeys.OldOpenRouterAccount] = "k" }, FailWrite = { "openrouter" } };
        Assert.Throws<Box.Failure>(() => Keys(box).Key("openrouter"));
        Assert.Equal(new Dictionary<string, string> { [ProviderKeys.OldOpenRouterAccount] = "k" }, box.Items);
        box.FailWrite.Clear();
        Assert.Equal("k", Keys(box).Key("openrouter"));
        Assert.Equal(new Dictionary<string, string> { ["openrouter"] = "k" }, box.Items);
    }

    /// The write seemed to work but the item isn't there: the old one is kept.
    [Fact]
    public void TheOldItemStaysWhenTheNewOneCantBeReadBack()
    {
        var box = new Box { Items = { [ProviderKeys.OldOpenRouterAccount] = "k" }, DropWrite = { "openrouter" } };
        Assert.Equal("k", Keys(box).Key("openrouter"));
        Assert.Equal(new Dictionary<string, string> { [ProviderKeys.OldOpenRouterAccount] = "k" }, box.Items);
    }

    /// Interrupted between writing the new item and removing the old: the key works, and the leftover goes at the next start.
    [Fact]
    public void InterruptedBeforeTheOldItemIsRemoved()
    {
        var box = new Box { Items = { [ProviderKeys.OldOpenRouterAccount] = "k" }, FailDelete = { ProviderKeys.OldOpenRouterAccount } };
        Assert.Throws<Box.Failure>(() => Keys(box).Key("openrouter"));
        Assert.Equal(new Dictionary<string, string> { [ProviderKeys.OldOpenRouterAccount] = "k", ["openrouter"] = "k" }, box.Items);
        box.FailDelete.Clear();
        Assert.Equal("k", Keys(box).Key("openrouter"));
        Keys(box).RemoveLeftoverOpenRouterItem();
        Assert.Equal(new Dictionary<string, string> { ["openrouter"] = "k" }, box.Items);
    }

    /// A leftover old item that differs from the new key is someone's other key: it is not removed.
    [Fact]
    public void ADifferentOldItemIsLeftAlone()
    {
        var box = new Box { Items = { [ProviderKeys.OldOpenRouterAccount] = "old", ["openrouter"] = "new" } };
        Keys(box).RemoveLeftoverOpenRouterItem();
        Assert.Equal(2, box.Items.Count);
    }

    [Fact]
    public void KeysKeptInMemoryForOneRun()
    {
        var keys = ProviderKeys.InMemory();
        Assert.Null(keys.Key("berget"));
        keys.StoreFor("berget").Write("b");
        Assert.Equal("b", keys.Key("berget"));
        Assert.True(keys.Status()["berget"]);
    }
}

public class ModelCatalogTests
{
    /// Entries as api.berget.ai/v1/models returned them on 2026-11-16 (shortened to the fields read).
    internal const string Berget = @"
    {""object"":""list"",""data"":[
     {""id"":""mistralai/Mistral-Small-3.2-24B-Instruct-2506"",""object"":""model"",""owned_by"":""mistralai"",""pricing"":{""currency"":""EUR"",""input"":0.3,""output"":0.3,""unit"":""€ / M Token""},""capabilities"":{""embeddings"":false,""function_calling"":true,""json_mode"":true,""streaming"":true,""vision"":false},""model_type"":""text""},
     {""id"":""zai-org/GLM-5.3-Flash"",""object"":""model"",""pricing"":{""currency"":""EUR"",""input"":0.25,""output"":0.5,""unit"":""€ / M Token""},""capabilities"":{""function_calling"":true,""json_mode"":true,""streaming"":true,""vision"":true},""model_type"":""text""},
     {""id"":""KBLab/kb-whisper-large"",""object"":""model"",""pricing"":{""currency"":""EUR"",""input"":3.3e-05,""output"":3.3e-05,""unit"":""€ / second""},""capabilities"":{""function_calling"":false,""streaming"":true,""vision"":false},""model_type"":""speech-to-text""},
     {""id"":""intfloat/multilingual-e5-large"",""object"":""model"",""pricing"":{""currency"":""EUR"",""input"":0.03,""output"":0},""capabilities"":{""embeddings"":true,""function_calling"":false},""model_type"":""embedding""}]}";

    /// Entries as api.grunden.ai/v1/models returned them on 2026-11-16.
    internal const string Grunden = @"
    {""object"":""list"",""data"":[
     {""id"":""glm-5.3"",""object"":""model"",""owned_by"":""grunden.ai"",""type"":""chat"",""endpoint"":""/v1/chat/completions"",""context_window"":1048576,""capabilities"":[""chat"",""reasoning"",""tools""],""reasoning_levels"":[""low"",""high"",""max""],""input_modalities"":[""text""],""tool_calling"":true,""pricing"":{""input_per_1m_tokens"":{""amount"":""0.90"",""currency"":""EUR""},""output_per_1m_tokens"":{""amount"":""1.80"",""currency"":""EUR""}}},
     {""id"":""gemma-4-26b"",""object"":""model"",""type"":""chat"",""capabilities"":[""chat"",""thinking_toggle"",""tools"",""vision""],""input_modalities"":[""text"",""image""],""tool_calling"":true,""pricing"":{""input_per_1m_tokens"":{""amount"":""0.15"",""currency"":""EUR""},""output_per_1m_tokens"":{""amount"":""0.45"",""currency"":""EUR""}}},
     {""id"":""bge-m3"",""object"":""model"",""type"":""embedding"",""capabilities"":[""embeddings""]},
     {""id"":""kb-whisper-large"",""object"":""model"",""type"":""stt"",""capabilities"":[""audio_transcription""]}]}";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void BergetsListGivesToolsVisionAndPrice()
    {
        var models = ModelCatalog.TextModels("berget", Bytes(Berget));
        Assert.Equal(new[] { "mistralai/Mistral-Small-3.2-24B-Instruct-2506", "zai-org/GLM-5.3-Flash" }, models.Select(m => m.Id));
        Assert.True(models[0].Tools == true && models[0].Vision == false);
        Assert.True(models[1].Vision);
        Assert.Equal("€0.25 in, €0.50 out per million tokens", models[1].PriceLine);
    }

    [Fact]
    public void GrundensListGivesToolsVisionAndPrice()
    {
        var models = ModelCatalog.TextModels("grunden", Bytes(Grunden));
        Assert.Equal(new[] { "gemma-4-26b", "glm-5.3" }, models.Select(m => m.Id));
        Assert.True(models[0].Vision == true && models[1].Vision == false && models[1].Tools == true);
        Assert.Equal("€0.90 in, €1.80 out per million tokens", models[1].PriceLine);
    }

    [Fact]
    public void APlainListGivesIdsOnlyAndNoPrice()
    {
        // OpenRouter's list states prices per token, as text; Glimmer shows them per million tokens. A model without prices has none.
        var listed = ModelCatalog.TextModels("openrouter", Bytes(@"{""data"":[{""id"":""acme/large"",""pricing"":{""prompt"":""0.000003"",""completion"":""0.000015""},""architecture"":{""input_modalities"":[""text"",""image""]},""supported_parameters"":[""tools"",""temperature""]},{""id"":""acme/bare""},{""id"":""acme/free"",""pricing"":{""prompt"":""0"",""completion"":""0""}}]}"));
        Assert.Equal("$3.00 in, $15.00 out per million tokens", listed.First(m => m.Id == "acme/large").PriceLine);
        Assert.True(listed.First(m => m.Id == "acme/large").Tools == true && listed.First(m => m.Id == "acme/large").Vision == true);
        Assert.Null(listed.First(m => m.Id == "acme/bare").PriceLine);
        Assert.Equal("$0.00 in, $0.00 out per million tokens", listed.First(m => m.Id == "acme/free").PriceLine);
        var models = ModelCatalog.TextModels("ollama", Bytes(@"{""object"":""list"",""data"":[{""id"":""qwen3:8b"",""object"":""model""}]}"));
        Assert.Equal(new[] { new ProviderModel("qwen3:8b") }, models);
        Assert.True(models[0].PriceLine is null && models[0].Tools is null);
    }

    [Fact]
    public void SomethingThatIsNotAListSaysSo()
    {
        var error = Assert.Throws<ModelCatalog.ListError>(() => ModelCatalog.TextModels("berget", Bytes("<html>")));
        Assert.Equal("Couldn't read Berget's model list: the answer isn't a model list.", error.Message);
    }

    [Fact]
    public async Task TheModelListIsFetchedFromTheService()
    {
        var fake = new FakeProvider((200, Grunden));
        var models = await ModelCatalog.FetchTextModelsAsync(AIProvider.Named("grunden")!, key: null, handler: fake);
        Assert.Equal(new[] { "gemma-4-26b", "glm-5.3" }, models.Select(m => m.Id));
        Assert.Equal("https://api.grunden.ai/v1/models", fake.First.Url.ToString());
        Assert.False(fake.First.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task AKeyIsSentWhenThereIsOneAndAFailureSaysWhoFailed()
    {
        var fake = new FakeProvider((200, Berget), (500, @"{""error"":{""message"":""down""}}"));
        await ModelCatalog.FetchTextModelsAsync(AIProvider.Named("berget")!, key: "bk", handler: fake);
        Assert.Equal("Bearer bk", fake.First.Headers["Authorization"]);
        var error = await Assert.ThrowsAsync<ProviderHttpError>(() => ModelCatalog.FetchTextModelsAsync(AIProvider.Named("berget")!, key: "bk", handler: fake));
        Assert.Equal("Berget answered HTTP 500: down", error.Message);
    }
}

public class ChatAdapterTests
{
    private const string ToolAnswer = @"{""id"":""x"",""object"":""chat.completion"",""model"":""m"",""choices"":[{""index"":0,""finish_reason"":""tool_calls"",""message"":{""role"":""assistant"",""content"":""Black and white."",""tool_calls"":[{""id"":""call_1"",""type"":""function"",""function"":{""name"":""add_adjustment"",""arguments"":""{\""type\"":\""black_white\""}""}}]}}],""usage"":{""prompt_tokens"":10,""completion_tokens"":5,""total_tokens"":15}}";

    private static readonly JsonObject System1 = (JsonObject)JsonNode.Parse(@"{""role"":""system"",""content"":""You plan edits.""}")!;
    private static readonly JsonObject User1 = (JsonObject)JsonNode.Parse(@"{""role"":""user"",""content"":""Make it black and white""}")!;
    private static readonly JsonObject Tool1 = (JsonObject)JsonNode.Parse(@"{""type"":""function"",""function"":{""name"":""add_adjustment"",""description"":""Adjust"",""parameters"":{""type"":""object"",""properties"":{""type"":{""type"":""string""}}}}}")!;

    private static ITextModel Model(string slug, string key, FakeProvider fake) => AIProvider.Named(slug)!.TextModel(key, "some-model", handler: fake)!;

    private static Task<TextToolReply> Plan(ITextModel model, params JsonObject[] messages) =>
        model.CallToolsAsync(messages.Length == 0 ? new[] { System1, User1 } : messages, new[] { Tool1 }, 0.2, null);

    [Fact]
    public async Task BergetGetsAPlainRequestWithItsKey()
    {
        var fake = new FakeProvider((200, ToolAnswer));
        var reply = await Plan(Model("berget", "bk", fake));
        Assert.Equal("Black and white.", reply.Text);
        Assert.Equal("add_adjustment", Assert.Single(reply.ToolCalls).Name);
        Assert.Null(reply.Cost);
        var sent = fake.First;
        Assert.Equal("https://api.berget.ai/v1/chat/completions", sent.Url.ToString());
        Assert.Equal("Bearer bk", sent.Headers["Authorization"]);
        Assert.Null(sent.Json["usage"]);                       // OpenRouter's own field
        Assert.Equal("auto", (string)sent.Json["tool_choice"]!);
        Assert.Null(sent.Json["reasoning_effort"]);
        Assert.Single(sent.Json["tools"]!.AsArray());
    }

    [Fact]
    public async Task GrundenIsAskedToReasonLittle()
    {
        var fake = new FakeProvider((200, ToolAnswer));
        await Plan(Model("grunden", "sk-grunden-x", fake));
        var sent = fake.First;
        Assert.Equal("https://api.grunden.ai/v1/chat/completions", sent.Url.ToString());
        Assert.Equal("low", (string)sent.Json["reasoning_effort"]!);
        Assert.Null(sent.Json["usage"]);
    }

    [Fact]
    public async Task GoogleGetsItsCompatibilityRouteWithoutWhatItsPageDoesNotShow()
    {
        var fake = new FakeProvider((200, ToolAnswer));
        var picture = (JsonObject)JsonNode.Parse(@"{""role"":""user"",""content"":[{""type"":""text"",""text"":""g""},{""type"":""image_url"",""image_url"":{""url"":""data:image/png;base64,CQ==""}}]}")!;
        await Plan(Model("google", "gk", fake), System1, picture);
        var sent = fake.First;
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", sent.Url.ToString());
        Assert.Equal("Bearer gk", sent.Headers["Authorization"]);
        Assert.Null(sent.Json["parallel_tool_calls"]);
        Assert.Null(sent.Json["usage"]);
        Assert.Equal("auto", (string)sent.Json["tool_choice"]!);
        // The picture goes as a data URL in the user message.
        var parts = sent.Json["messages"]!.AsArray().Last()!["content"]!.AsArray();
        Assert.Contains(parts, p => ((string?)p!["image_url"]?["url"])?.StartsWith("data:image/") == true);
    }

    [Fact]
    public async Task OllamaThroughItsCompatibleRouteGetsNoKeyAndNoToolChoice()
    {
        var fake = new FakeProvider((200, ToolAnswer));
        var model = new OpenRouterTextModel(new OpenRouterClient("", AIProvider.Named("ollama")!.ChatConfiguration(), fake), "m", "Ollama");
        await Plan(model);
        var sent = fake.First;
        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions", sent.Url.ToString());
        Assert.False(sent.Headers.ContainsKey("Authorization"));
        Assert.Null(sent.Json["tool_choice"]);
        Assert.NotNull(sent.Json["tools"]);
    }

    [Fact]
    public async Task OpenRouterIsSentWhatItWasSentBefore()
    {
        var fake = new FakeProvider((200, ToolAnswer));
        await Plan(Model("openrouter", "sk-or", fake));
        var sent = fake.First;
        Assert.NotNull(sent.Json["usage"]);
        Assert.Equal("auto", (string)sent.Json["tool_choice"]!);
        Assert.True((bool)sent.Json["parallel_tool_calls"]!);
        Assert.Equal(0.2, (double)sent.Json["temperature"]!);
    }

    [Fact]
    public async Task AProvidersRefusalIsShownAsItCameWithItsStatus()
    {
        var fake = new FakeProvider((404, @"{""error"":{""message"":""The model `glm-9` does not exist or has been retired."",""type"":""model_not_found""}}"));
        var error = await Assert.ThrowsAsync<ProviderHttpError>(() => Plan(Model("berget", "bk", fake)));
        Assert.Equal("Berget answered HTTP 404: The model `glm-9` does not exist or has been retired.", error.Message);
        fake.Script((401, "Unauthorized"));
        error = await Assert.ThrowsAsync<ProviderHttpError>(() => Plan(Model("grunden", "bad", fake)));
        Assert.Equal("Grunden answered HTTP 401: Unauthorized", error.Message);
    }

    /// Every service that is offered, one by one (they are all shown since 2026-11-16, most of them never called):
    /// what the service answers in refusal reaches the person word for word with its status, and an answer that
    /// can't be used is an error, never a plan.
    [Fact]
    public async Task EveryOfferedTextServiceShowsARefusalWordForWordAndMakesNothingOfAnUnusableAnswer()
    {
        const string words = "This request cannot be served: the account is restricted (ref 7Q-ZETA).";
        var refusal = $@"{{""error"":{{""message"":""{words}"",""type"":""restricted""}}}}";
        var texts = AIProvider.Offering(AIProvider.Capability.Text).Where(p => p.Kind == AIProvider.KindOf.Cloud).ToList();
        Assert.Equal(new[] { "openrouter", "openai", "anthropic", "xai", "google", "berget", "grunden" }, texts.Select(p => p.Slug));
        foreach (var p in texts)
        {
            var fake = new FakeProvider((422, refusal));
            var model = p.TextModel("k", "m", handler: fake)!;
            var error = await Assert.ThrowsAnyAsync<Exception>(() => model.CallToolsAsync(new[] { User1 }, new[] { Tool1 }, 0.2, null));
            // OpenRouter's errors are put in Glimmer's own words around the service's (tried since long); the others carry the status.
            Assert.True(error.Message.Contains(words) && (error.Message.Contains("422") || p.Slug == "openrouter"), $"{p.Slug}: {error.Message}");

            fake.Script((200, "<html><body>Gateway says hello</body></html>"), (200, "<html><body>Gateway says hello</body></html>"));
            try
            {
                var reply = await model.CallToolsAsync(new[] { User1 }, new[] { Tool1 }, 0.2, null);
                Assert.Empty(reply.ToolCalls);   // an unusable answer is never a set of steps
            }
            catch (Exception)
            {
                // An error is the other right answer.
            }
        }
    }

    [Fact]
    public async Task ACloudProviderWithoutAKeyIsNotCalled()
    {
        foreach (var slug in new[] { "berget", "openai", "anthropic", "xai" })
        {
            var fake = new FakeProvider((200, ToolAnswer));
            var error = await Assert.ThrowsAsync<OpenRouterError>(() => Plan(Model(slug, "", fake)));
            Assert.Equal(OpenRouterError.MissingAPIKey(), error);
            Assert.Empty(fake.Requests);
        }
    }

    [Fact]
    public async Task ATextReplyAsksForJsonAccordingToWhatTheServiceTakes()
    {
        var fake = new FakeProvider((200, ToolAnswer), (200, ToolAnswer));
        var reply = await Model("berget", "bk", fake).ReplyAsync("s", "u", Array.Empty<byte[]>(), "image/png", json: true, 0.7);
        Assert.Equal("Black and white.", reply.Text);
        Assert.Equal("json_object", (string)fake.First.Json["response_format"]!["type"]!);
        await Model("google", "gk", fake).ReplyAsync("s", "u", Array.Empty<byte[]>(), "image/png", json: true, 0.7);
        Assert.Null(fake.Requests[1].Json["response_format"]);   // Google's page shows none
    }

    // MARK: Ollama, through its own chat API

    /// An answer in the form of Ollama's /api/chat documentation: arguments as an object.
    private const string OllamaAnswer = @"{""model"":""qwen3"",""created_at"":""2026-11-16T00:00:00Z"",""message"":{""role"":""assistant"",""content"":"""",""tool_calls"":[{""type"":""function"",""function"":{""index"":0,""name"":""crop"",""arguments"":{""aspect"":""16:9""}}}]},""done"":true}";

    [Fact]
    public async Task TheOllamaRequestIsNativeShortAndStatesItsContext()
    {
        var fake = new FakeProvider((200, OllamaAnswer));
        var model = new OllamaTextModel("qwen3:8b", new Uri("http://127.0.0.1:11434/v1"), handler: fake);
        Assert.True(model.PrefersShortToolList);
        Assert.Equal("Ollama", model.ProviderName);
        var picture = (JsonObject)JsonNode.Parse(@"{""role"":""user"",""content"":[{""type"":""text"",""text"":""Crop to 16:9""},{""type"":""image_url"",""image_url"":{""url"":""data:image/jpeg;base64,AQID""}}]}")!;
        var reply = await model.CallToolsAsync(new[] { System1, picture }, new[] { Tool1 }, 0.2, null);
        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal(new TextToolCall("crop", @"{""aspect"":""16:9""}"), call);
        Assert.Null(reply.Text);
        var sent = fake.First;
        Assert.Equal("http://127.0.0.1:11434/api/chat", sent.Url.ToString());
        Assert.False(sent.Headers.ContainsKey("Authorization"));
        Assert.Equal(16_384, (int)sent.Json["options"]!["num_ctx"]!);
        Assert.False((bool)sent.Json["stream"]!);
        Assert.False((bool)sent.Json["think"]!);
        Assert.Null(sent.Json["tool_choice"]);
        // The picture travels as base64 in `images`, the text as a plain string.
        var user = sent.Json["messages"]!.AsArray().Last()!;
        Assert.Equal("Crop to 16:9", (string)user["content"]!);
        Assert.Equal("AQID", (string)user["images"]![0]!);
    }

    [Fact]
    public async Task AModelWithoutToolsFailsWithOllamasOwnWords()
    {
        var fake = new FakeProvider((400, @"{""error"":""registry.ollama.ai/library/gemma3:4b does not support tools""}"));
        var model = new OllamaTextModel("gemma3:4b", handler: fake);
        var error = await Assert.ThrowsAsync<ProviderHttpError>(() => model.CallToolsAsync(new[] { User1 }, new[] { Tool1 }, 0.2, null));
        Assert.Equal("Ollama answered HTTP 400: registry.ollama.ai/library/gemma3:4b does not support tools", error.Message);
    }

    [Fact]
    public async Task OllamaJsonRepliesAskForJsonAndPicturesGoAsBase64()
    {
        var fake = new FakeProvider((200, @"{""message"":{""role"":""assistant"",""content"":""{\""suggestions\"":[]}""},""done"":true}"));
        var model = new OllamaTextModel("m", handler: fake);
        var reply = await model.ReplyAsync("s", "u", new[] { new byte[] { 1, 2, 3 } }, "image/png", json: true, 0.2);
        Assert.Equal(@"{""suggestions"":[]}", reply.Text);
        Assert.Equal("json", (string)fake.First.Json["format"]!);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), (string)fake.First.Json["messages"]!.AsArray().Last()!["images"]![0]!);
    }

    [Fact]
    public async Task OllamaNotRunningSaysSoInItsOwnWords()
    {
        var fake = new FakeProvider(new FakeProvider.Answer(0, "", Throw: new HttpRequestException("Connection refused")));
        var model = new OllamaTextModel("m", handler: fake);
        var error = await Assert.ThrowsAsync<OllamaTextModel.OllamaError>(() => model.CallToolsAsync(new[] { User1 }, new[] { Tool1 }, 0.2, null));
        Assert.Equal("Glimmer couldn't reach Ollama at 127.0.0.1:11434. Is Ollama running? (Connection refused)", error.Message);
        fake.Script((200, @"{""nothing"":true}"));
        error = await Assert.ThrowsAsync<OllamaTextModel.OllamaError>(() => model.CallToolsAsync(new[] { User1 }, new[] { Tool1 }, 0.2, null));
        Assert.Equal("Ollama's answer wasn't in the form Glimmer expects.", error.Message);
    }

    [Fact]
    public async Task InstalledModelsComeWithWhatOllamaSaysTheyCanDo()
    {
        var fake = new FakeProvider(
            (200, @"{""models"":[{""name"":""qwen3:8b"",""model"":""qwen3:8b"",""size"":5225388164},{""name"":""gemma3:4b"",""model"":""gemma3:4b"",""size"":3338801804}]}"),
            (200, @"{""capabilities"":[""completion"",""tools"",""thinking""],""model_info"":{""qwen3.context_length"":40960}}"),
            (200, @"{""capabilities"":[""completion"",""vision""],""model_info"":{""gemma3.context_length"":131072}}"));
        var models = await OllamaTextModel.InstalledAsync(handler: fake);
        Assert.Equal(new[]
        {
            new OllamaTextModel.Installed("gemma3:4b", false, true, 3_338_801_804),
            new OllamaTextModel.Installed("qwen3:8b", true, false, 5_225_388_164),
        }, models);
        Assert.Equal("http://127.0.0.1:11434/api/tags", fake.First.Url.ToString());
        var abilities = await OllamaTextModel.AbilitiesAsync("m", handler: new FakeProvider((200, @"{""capabilities"":[""tools""],""model_info"":{""x.context_length"":8192}}")));
        Assert.Equal((true, false, (int?)8192), abilities);
    }

    [Fact]
    public async Task NoServerMeansNoList()
    {
        var fake = new FakeProvider((404, "not found"));
        await Assert.ThrowsAsync<OllamaTextModel.OllamaError>(() => OllamaTextModel.InstalledAsync(handler: fake));
    }

    // MARK: Responses API: OpenAI and xAI

    private const string ResponsesAnswer = @"{""id"":""resp_1"",""object"":""response"",""status"":""completed"",""output"":[
      {""type"":""reasoning"",""id"":""rs_1"",""summary"":[]},
      {""type"":""message"",""id"":""msg_1"",""role"":""assistant"",""content"":[{""type"":""output_text"",""text"":""Black and white, then cropped.""}]},
      {""id"":""fc_1"",""call_id"":""call_1"",""type"":""function_call"",""name"":""add_adjustment"",""arguments"":""{\""type\"":\""black_white\""}""},
      {""id"":""fc_2"",""call_id"":""call_2"",""type"":""function_call"",""name"":""crop"",""arguments"":""{\""aspect\"":\""16:9\""}""}],
     ""usage"":{""input_tokens"":900,""output_tokens"":40,""total_tokens"":940}}";

    [Fact]
    public async Task OpenAIGetsAResponsesRequestAndItsToolCallsComeBack()
    {
        var fake = new FakeProvider((200, ResponsesAnswer));
        var model = Model("openai", "sk-test", fake);
        var picture = (JsonObject)JsonNode.Parse(@"{""role"":""user"",""content"":[{""type"":""text"",""text"":""Black and white, 16:9""},{""type"":""image_url"",""image_url"":{""url"":""data:image/jpeg;base64,Bw==""}}]}")!;
        var earlier = (JsonObject)JsonNode.Parse(@"{""role"":""assistant"",""content"":""done""}")!;
        // Without a listener the answer is asked for whole (the streamed form has its own test).
        var reply = await model.CallToolsAsync(new[] { System1, User1, earlier, picture }, new[] { Tool1 }, 0.2, null);
        Assert.Equal(new[] { "add_adjustment", "crop" }, reply.ToolCalls.Select(c => c.Name));
        Assert.Equal("Black and white, then cropped.", reply.Text);
        Assert.Null(reply.Cost);
        var sent = fake.First;
        Assert.Null(sent.Json["stream"]);
        Assert.Equal("https://api.openai.com/v1/responses", sent.Url.ToString());
        Assert.Equal("Bearer sk-test", sent.Headers["Authorization"]);
        Assert.True(sent.Json["messages"] is null && sent.Json["temperature"] is null && sent.Json["tool_choice"] is null);
        // Tools in the flat form.
        var tool = sent.Json["tools"]!.AsArray()[0]!;
        Assert.Equal("function", (string)tool["type"]!);
        Assert.True(tool["name"] is not null && tool["parameters"] is not null && tool["function"] is null);
        // The conversation as input items; the picture as input_image with its data URL.
        var input = sent.Json["input"]!.AsArray();
        Assert.Equal("system", (string)input[0]!["role"]!);
        Assert.Equal(4, input.Count);
        var parts = input.Last()!["content"]!.AsArray();
        Assert.Contains(parts, p => (string?)p!["type"] == "input_text");
        Assert.Contains(parts, p => (string?)p!["type"] == "input_image" && ((string?)p!["image_url"])!.StartsWith("data:image/"));
    }

    [Fact]
    public async Task XAIGoesToItsOwnAddressInTheSameForm()
    {
        var fake = new FakeProvider((200, ResponsesAnswer));
        var model = Model("xai", "xai-test", fake);
        var reply = await model.ReplyAsync("s", "u", new[] { new byte[] { 1 } }, "image/jpeg", json: true, 0.7);
        Assert.Equal("Black and white, then cropped.", reply.Text);
        var sent = fake.First;
        Assert.Equal("https://api.x.ai/v1/responses", sent.Url.ToString());
        Assert.Equal("Bearer xai-test", sent.Headers["Authorization"]);
        Assert.True(sent.Json["temperature"] is null && sent.Json["response_format"] is null && sent.Json["text"] is null);
        var user = sent.Json["input"]!.AsArray().Last()!["content"]!.AsArray();
        Assert.Equal("data:image/jpeg;base64," + Convert.ToBase64String(new byte[] { 1 }), (string)user.Last()!["image_url"]!);
    }

    [Fact]
    public async Task ARefusalFromTheResponsesApiIsShownAsItCame()
    {
        var fake = new FakeProvider((400, @"{""error"":{""message"":""The model `gpt-0` does not exist."",""type"":""invalid_request_error"",""code"":""model_not_found""}}"));
        var model = AIProvider.Named("openai")!.TextModel("k", "gpt-0", handler: fake)!;
        var error = await Assert.ThrowsAsync<ProviderHttpError>(() => model.CallToolsAsync(new[] { User1 }, new[] { Tool1 }, 0.2, null));
        Assert.Equal("OpenAI answered HTTP 400: The model `gpt-0` does not exist.", error.Message);
    }

    [Fact]
    public void TheRegisterBuildsTheRightAdapterAndNothingForWhatIsNotBuilt()
    {
        Assert.IsType<ResponsesTextModel>(AIProvider.Named("openai")!.TextModel("k", "m"));
        Assert.IsType<ResponsesTextModel>(AIProvider.Named("xai")!.TextModel("k", "m"));
        Assert.IsType<OpenRouterTextModel>(AIProvider.Named("berget")!.TextModel("k", "m"));
        Assert.IsType<OllamaTextModel>(AIProvider.Named("ollama")!.TextModel("", "m"));
        Assert.IsType<AnthropicTextModel>(AIProvider.Named("anthropic")!.TextModel("k", "m"));
        Assert.NotNull(AIProvider.Named("mlx")!.TextModel("", "m"));
        Assert.False(AIProvider.Named("mlx")!.Offers(AIProvider.Capability.Text));
        Assert.Null(AIProvider.Named("fal")!.TextModel("k", "m"));
        Assert.Equal(new[] { "openrouter", "openai", "anthropic", "xai", "google", "berget", "grunden", "ollama" }, AIProvider.Offering(AIProvider.Capability.Text).Select(p => p.Slug));
    }

    // MARK: Anthropic

    private const string ClaudeAnswer = @"{""id"":""msg_1"",""type"":""message"",""role"":""assistant"",""model"":""m"",""stop_reason"":""tool_use"",""content"":[
      {""type"":""thinking"",""thinking"":"""",""signature"":""x""},
      {""type"":""text"",""text"":""Black and white.""},
      {""type"":""tool_use"",""id"":""toolu_1"",""name"":""add_adjustment"",""input"":{""type"":""black_white""}}],
     ""usage"":{""input_tokens"":900,""output_tokens"":40}}";

    [Fact]
    public async Task ClaudeGetsAMessagesRequestAndItsToolUseBecomesTheReply()
    {
        var fake = new FakeProvider((200, ClaudeAnswer));
        var model = Model("anthropic", "sk-ant-test", fake);
        var picture = (JsonObject)JsonNode.Parse(@"{""role"":""user"",""content"":[{""type"":""text"",""text"":""Black and white""},{""type"":""image_url"",""image_url"":{""url"":""data:image/jpeg;base64,Bw==""}}]}")!;
        var earlier = (JsonObject)JsonNode.Parse(@"{""role"":""assistant"",""content"":""done""}")!;
        var reply = await model.CallToolsAsync(new[] { System1, User1, earlier, picture }, new[] { Tool1 }, 0.2, null);
        Assert.Equal(new TextToolCall("add_adjustment", @"{""type"":""black_white""}"), Assert.Single(reply.ToolCalls));
        Assert.Equal("Black and white.", reply.Text);
        Assert.Null(reply.Cost);
        var sent = fake.First;
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.Url.ToString());
        Assert.True(sent.Headers["x-api-key"] == "sk-ant-test" && sent.Headers["anthropic-version"] == "2026-10-21" && !sent.Headers.ContainsKey("Authorization"));
        Assert.Equal("application/json", sent.Headers["Content-Type"]);
        // What the current models refuse is not sent.
        Assert.True(sent.Json["temperature"] is null && sent.Json["tool_choice"] is null && sent.Json["thinking"] is null);
        Assert.Equal(16_000, (int)sent.Json["max_tokens"]!);
        // The system prompt is top-level; the turns are user/assistant only.
        Assert.Equal("You plan edits.", (string)sent.Json["system"]!);
        var turns = sent.Json["messages"]!.AsArray();
        Assert.Equal(new[] { "user", "assistant", "user" }, turns.Select(t => (string)t!["role"]!));
        var blocks = turns.Last()!["content"]!.AsArray();
        var image = blocks.First(b => (string?)b!["type"] == "image")!["source"]!;
        Assert.True((string?)image["type"] == "base64" && (string?)image["media_type"] == "image/jpeg" && (string?)image["data"] == "Bw==");
        // Tools in Anthropic's form.
        var tool = sent.Json["tools"]!.AsArray()[0]!;
        Assert.True(tool["name"] is not null && tool["input_schema"] is not null && tool["function"] is null && tool["type"] is null);
        Assert.Equal("Adjust", (string)tool["description"]!);
    }

    [Fact]
    public async Task AClaudeReplyPutsTheImagesBeforeTheText()
    {
        var fake = new FakeProvider((200, ClaudeAnswer));
        var reply = await Model("anthropic", "k", fake).ReplyAsync("s", "describe", new[] { new byte[] { 7 } }, "image/png", json: false, 0.2);
        Assert.Equal("Black and white.", reply.Text);
        var content = fake.First.Json["messages"]!.AsArray()[0]!["content"]!.AsArray();
        Assert.Equal(new[] { "image", "text" }, content.Select(c => (string)c!["type"]!));
        Assert.Equal("s", (string)fake.First.Json["system"]!);
    }

    [Fact]
    public async Task ADeclinedRequestSaysSoAndChangesNothing()
    {
        var fake = new FakeProvider((200, @"{""id"":""msg_2"",""type"":""message"",""role"":""assistant"",""stop_reason"":""refusal"",""stop_details"":{""type"":""refusal"",""category"":""cyber""},""content"":[]}"));
        var model = Model("anthropic", "k", fake);
        var error = await Assert.ThrowsAsync<AnthropicTextModel.Refusal>(() => model.CallToolsAsync(new[] { User1 }, new[] { Tool1 }, 0.2, null));
        Assert.Equal("Claude declined this request (cyber). Nothing was changed.", error.Message);
        Assert.Equal(new AnthropicTextModel.Refusal("cyber"), error);
    }

    [Fact]
    public async Task ClaudesErrorsAndModelListUseItsOwnHeaders()
    {
        var fake = new FakeProvider(
            (401, @"{""type"":""error"",""error"":{""type"":""authentication_error"",""message"":""invalid x-api-key""}}"),
            (200, @"{""data"":[{""type"":""model"",""id"":""claude-example-1"",""display_name"":""Example""}],""has_more"":false}"));
        var provider = AIProvider.Named("anthropic")!;
        var model = provider.TextModel("bad", "m", handler: fake)!;
        var error = await Assert.ThrowsAsync<ProviderHttpError>(() => model.ReplyAsync(null, "u", Array.Empty<byte[]>(), "image/png", false, 0.2));
        Assert.Equal("Anthropic answered HTTP 401: invalid x-api-key", error.Message);
        var models = await ModelCatalog.FetchTextModelsAsync(provider, key: "sk-ant-test", handler: fake);
        Assert.Equal(new[] { "claude-example-1" }, models.Select(m => m.Id));
        var list = fake.Requests.Last();
        Assert.True(list.Url.ToString() == "https://api.anthropic.com/v1/models" && list.Headers["x-api-key"] == "sk-ant-test" && list.Headers["anthropic-version"] == "2026-10-21");
        Assert.False(list.Headers.ContainsKey("Authorization"));
    }

    // MARK: Streaming for Anthropic and OpenAI, against event streams in the documented form

    private static string Sse(params string[] events) => string.Concat(events.Select(e => $"data: {e}\n\n"));

    [Fact]
    public async Task AnthropicStreamsTextAndPutsAToolCallTogether()
    {
        var fake = new FakeProvider((200, Sse(
            @"{""type"":""message_start"",""message"":{""id"":""m1"",""content"":[]}}",
            @"{""type"":""content_block_start"",""index"":0,""content_block"":{""type"":""text"",""text"":""""}}",
            @"{""type"":""content_block_delta"",""index"":0,""delta"":{""type"":""text_delta"",""text"":""Cropping ""}}",
            @"{""type"":""content_block_delta"",""index"":0,""delta"":{""type"":""text_delta"",""text"":""to square.""}}",
            @"{""type"":""content_block_stop"",""index"":0}",
            @"{""type"":""content_block_start"",""index"":1,""content_block"":{""type"":""tool_use"",""id"":""t1"",""name"":""crop"",""input"":{}}}",
            @"{""type"":""content_block_delta"",""index"":1,""delta"":{""type"":""input_json_delta"",""partial_json"":""{\""aspect\"":""}}",
            @"{""type"":""content_block_delta"",""index"":1,""delta"":{""type"":""input_json_delta"",""partial_json"":""\""1:1\""}""}}",
            @"{""type"":""message_delta"",""delta"":{""stop_reason"":""tool_use""}}",
            @"{""type"":""message_stop""}")));
        var model = new AnthropicTextModel(AIProvider.Named("anthropic")!, "k", "m", fake);
        var shown = new List<string>();
        var reply = await model.CallToolsAsync(new[] { User1 }, new[] { Tool1 }, 0.2, shown.Add);
        Assert.Equal(new[] { "Cropping ", "Cropping to square." }, shown);
        Assert.Equal("Cropping to square.", reply.Text);
        Assert.Equal("crop", Assert.Single(reply.ToolCalls).Name);
        Assert.Equal(@"{""aspect"":""1:1""}", reply.ToolCalls[0].Arguments);
        Assert.True((bool)fake.First.Json["stream"]!);
    }

    [Fact]
    public async Task AStreamedRefusalAndAStreamedErrorAreSaidAsSuch()
    {
        var model = new AnthropicTextModel(AIProvider.Named("anthropic")!, "k", "m", new FakeProvider());
        var fake = new FakeProvider((200, Sse(@"{""type"":""message_delta"",""delta"":{""stop_reason"":""refusal"",""stop_details"":{""type"":""refusal"",""category"":""cyber""}}}")));
        model = new AnthropicTextModel(AIProvider.Named("anthropic")!, "k", "m", fake);
        var refusal = await Assert.ThrowsAsync<AnthropicTextModel.Refusal>(() => model.CallToolsAsync(new[] { User1 }, Array.Empty<JsonObject>(), 0.2, _ => { }));
        Assert.Equal(new AnthropicTextModel.Refusal("cyber"), refusal);

        fake.Script((200, Sse(@"{""type"":""error"",""error"":{""type"":""overloaded_error"",""message"":""Overloaded""}}")));
        var error = await Assert.ThrowsAsync<ProviderHttpError>(() => model.CallToolsAsync(new[] { User1 }, Array.Empty<JsonObject>(), 0.2, _ => { }));
        Assert.Equal("Anthropic answered HTTP 200: Overloaded", error.Message);

        fake.Script((401, @"{""type"":""error"",""error"":{""type"":""authentication_error"",""message"":""invalid x-api-key""}}"));
        error = await Assert.ThrowsAsync<ProviderHttpError>(() => model.CallToolsAsync(new[] { User1 }, Array.Empty<JsonObject>(), 0.2, _ => { }));
        Assert.Equal("Anthropic answered HTTP 401: invalid x-api-key", error.Message);
    }

    [Fact]
    public async Task OpenAIStreamsWithItsDocumentedEventsAndXAIDoesNot()
    {
        var fake = new FakeProvider((200, Sse(
            @"{""type"":""response.created"",""response"":{""id"":""resp_1""}}",
            @"{""type"":""response.output_text.delta"",""delta"":""Cropping ""}",
            @"{""type"":""response.output_text.delta"",""delta"":""to square.""}",
            @"{""type"":""response.output_item.added"",""response_id"":""resp_1"",""output_index"":1,""item"":{""type"":""function_call"",""id"":""fc_1"",""call_id"":""call_1"",""name"":""crop"",""arguments"":""""}}",
            @"{""type"":""response.function_call_arguments.delta"",""response_id"":""resp_1"",""item_id"":""fc_1"",""output_index"":1,""delta"":""{\""""}",
            @"{""type"":""response.output_item.done"",""response_id"":""resp_1"",""output_index"":1,""item"":{""type"":""function_call"",""id"":""fc_1"",""call_id"":""call_1"",""name"":""crop"",""arguments"":""{\""aspect\"":\""1:1\""}""}}",
            @"{""type"":""response.completed""}")));
        var openAI = new ResponsesTextModel(AIProvider.Named("openai")!, "k", "m", fake);
        var shown = new List<string>();
        var reply = await openAI.CallToolsAsync(new[] { User1 }, new[] { Tool1 }, 0.2, shown.Add);
        Assert.Equal(new[] { "Cropping ", "Cropping to square." }, shown);
        Assert.Equal("Cropping to square.", reply.Text);
        Assert.Equal(@"{""aspect"":""1:1""}", reply.ToolCalls[0].Arguments);
        Assert.True((bool)fake.First.Json["stream"]!);

        fake.Script((200, Sse(@"{""type"":""error"",""message"":""The server had an error.""}")));
        var error = await Assert.ThrowsAsync<ProviderHttpError>(() => openAI.CallToolsAsync(new[] { User1 }, Array.Empty<JsonObject>(), 0.2, _ => { }));
        Assert.Equal("OpenAI answered HTTP 200: The server had an error.", error.Message);

        fake.Script((200, Sse(@"{""type"":""response.failed"",""response"":{""error"":{""message"":""overloaded""}}}")));
        error = await Assert.ThrowsAsync<ProviderHttpError>(() => openAI.CallToolsAsync(new[] { User1 }, Array.Empty<JsonObject>(), 0.2, _ => { }));
        Assert.Equal("OpenAI answered HTTP 200: overloaded", error.Message);

        // xAI's pages name no events for this API: one whole answer, no "stream" in the request.
        fake.Script((200, @"{""output"":[{""type"":""message"",""content"":[{""type"":""output_text"",""text"":""Done.""}]}]}"));
        var xai = new ResponsesTextModel(AIProvider.Named("xai")!, "k", "m", fake);
        var heard = new List<string>();
        var whole = await xai.CallToolsAsync(new[] { User1 }, Array.Empty<JsonObject>(), 0.2, heard.Add);
        Assert.Equal("Done.", whole.Text);
        Assert.Equal(new[] { "Done." }, heard);
        Assert.Null(fake.First.Json["stream"]);
    }

    // MARK: MLX's server on this machine: the plainest chat-completions request, no key

    [Fact]
    public async Task MlxGetsAPlainRequestWithoutAKey()
    {
        var fake = new FakeProvider((200, @"{""choices"":[{""message"":{""role"":""assistant"",""content"":""Done."",""tool_calls"":[{""id"":""c1"",""type"":""function"",""function"":{""name"":""crop"",""arguments"":""{\""aspect\"":\""1:1\""}""}}]}}]}"));
        var model = AIProvider.Named("mlx")!.TextModel("", "mlx-community/some-model", handler: fake)!;
        var reply = await Plan(model);
        Assert.Equal("crop", Assert.Single(reply.ToolCalls).Name);
        Assert.Null(reply.Cost);
        var sent = fake.First;
        Assert.True(sent.Url.ToString() == "http://127.0.0.1:8080/v1/chat/completions" && !sent.Headers.ContainsKey("Authorization"));
        Assert.True(sent.Json["tool_choice"] is null && sent.Json["parallel_tool_calls"] is null && sent.Json["usage"] is null && sent.Json["response_format"] is null);
        Assert.Equal("mlx-community/some-model", (string)sent.Json["model"]!);
    }
}

/// A key never follows an address that came in a service's answer.
public class KeyStaysHomeTests
{
    [Fact]
    public void ARedirectToAnotherHostIsNotFollowed()
    {
        var original = new Uri("https://api.hedra.com/v3/jobs/j1");
        Assert.True(KeyStaysHome.MayFollow(original, new Uri("https://api.hedra.com/v3/jobs/j1/")));
        Assert.False(KeyStaysHome.MayFollow(original, new Uri("https://evil.example/collect")));
        Assert.False(KeyStaysHome.MayFollow(original, new Uri("http://api.hedra.com/v3/jobs/j1")));        // not down to plain http
        Assert.False(KeyStaysHome.MayFollow(original, new Uri("https://api.hedra.com.evil.example/x")));
        Assert.False(KeyStaysHome.MayFollow(original, null));
    }

    [Fact]
    public async Task TheHandlerFollowsASameHostRedirectWithTheKeyAndStopsAtAnotherHost()
    {
        var fake = new FakeProvider(
            new FakeProvider.Answer(307, "", new Dictionary<string, string> { ["Location"] = "/v1/moved" }),
            new FakeProvider.Answer(200, @"{""ok"":true}"));
        using var client = KeyStaysHome.CreateClient(fake);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1/call") { Content = new StringContent(@"{""a"":1}", Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
        using var response = await client.SendAsync(request);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal("https://api.example.com/v1/moved", fake.Requests[1].Url.ToString());
        Assert.Equal("Bearer secret", fake.Requests[1].Headers["Authorization"]);
        Assert.Equal(@"{""a"":1}", fake.Requests[1].BodyText);   // 307 keeps method and body

        fake.Script(new FakeProvider.Answer(302, "", new Dictionary<string, string> { ["Location"] = "https://evil.example/collect" }), new FakeProvider.Answer(200, "{}"));
        using var second = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1/call"));
        Assert.Equal(302, (int)second.StatusCode);
        Assert.Single(fake.Requests);   // the redirect itself is the answer; nothing was sent to the other host
    }

    [Fact]
    public void LinksAndIdentifiersFromAnAnswerAreNarrow()
    {
        Assert.NotNull(KeyStaysHome.Fetchable("https://outputs.hedra.example/signed?x=1"));
        Assert.True(KeyStaysHome.Fetchable("http://outputs.hedra.example/x") is null && KeyStaysHome.Fetchable("file:///etc/passwd") is null && KeyStaysHome.Fetchable(null) is null);
        Assert.Equal("job_1-A9", KeyStaysHome.PathSafe("job_1-A9"));
        Assert.True(KeyStaysHome.PathSafe("../../files") is null && KeyStaysHome.PathSafe("a/b") is null && KeyStaysHome.PathSafe("j?x=1") is null && KeyStaysHome.PathSafe("") is null);
        Assert.Null(KeyStaysHome.PathSafe(new string('a', 201)));
    }

    /// Every adapter that sends a key goes through the handler: an answer that redirects to another host is
    /// the answer, and no second request is made (the original checked that its source said `delegate: KeyStaysHome.shared`).
    [Fact]
    public async Task EveryKeyedCallRefusesForeignRedirects()
    {
        var redirect = new FakeProvider.Answer(302, "", new Dictionary<string, string> { ["Location"] = "https://evil.example/collect" });
        var user = (JsonObject)JsonNode.Parse(@"{""role"":""user"",""content"":""x""}")!;
        foreach (var slug in new[] { "openrouter", "openai", "anthropic", "xai", "google", "berget", "grunden" })
        {
            var fake = new FakeProvider(redirect, redirect, redirect, redirect, redirect);
            var model = AIProvider.Named(slug)!.TextModel("k", "m", handler: fake)!;
            var error = await Assert.ThrowsAnyAsync<Exception>(() => model.CallToolsAsync(new[] { user }, Array.Empty<JsonObject>(), 0.2, null));
            Assert.Contains("302", error.Message.Replace("HTTP 302", "302"));
            Assert.DoesNotContain(fake.Requests, r => r.Url.Host == "evil.example");
            // The streamed form too.
            fake = new FakeProvider(redirect, redirect, redirect, redirect, redirect);
            model = AIProvider.Named(slug)!.TextModel("k", "m", handler: fake)!;
            await Assert.ThrowsAnyAsync<Exception>(() => model.CallToolsAsync(new[] { user }, Array.Empty<JsonObject>(), 0.2, _ => { }));
            Assert.DoesNotContain(fake.Requests, r => r.Url.Host == "evil.example");
        }
        var list = new FakeProvider(redirect);
        await Assert.ThrowsAsync<ProviderHttpError>(() => ModelCatalog.FetchTextModelsAsync(AIProvider.Named("berget")!, key: "k", handler: list));
        Assert.DoesNotContain(list.Requests, r => r.Url.Host == "evil.example");
    }
}
