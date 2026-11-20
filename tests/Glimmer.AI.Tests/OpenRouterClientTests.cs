// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/OpenRouterClientTests.swift
// (the client's request, retry and streaming tests are new: the original covered them through the planner)
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Glimmer.AI;

namespace Glimmer.AI.Tests;

public class OpenRouterErrorTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes(@"{""error"": {""message"": ""Nope"", ""code"": 400}}");

    [Fact]
    public void SuccessIsNotAnError() => Assert.Null(OpenRouterError.From(200, Array.Empty<byte>(), null));

    [Fact]
    public void AuthAndCreditErrorsAreFatal()
    {
        var invalid = OpenRouterError.From(401, Body, null);
        var credits = OpenRouterError.From(402, Body, null);
        Assert.Equal(OpenRouterError.InvalidAPIKey(), invalid);
        Assert.Equal(OpenRouterError.InsufficientCredits(), credits);
        Assert.False(invalid!.IsRetryable);
        Assert.False(credits!.IsRetryable);
    }

    [Fact]
    public void RateLimitCarriesRetryAfter()
    {
        var error = OpenRouterError.From(429, Array.Empty<byte>(), "12");
        Assert.Equal(OpenRouterError.RateLimited(12), error);
        Assert.True(error!.IsRetryable);
    }

    [Fact]
    public void MapsOtherStatuses()
    {
        Assert.Equal(OpenRouterError.BadRequest("Nope"), OpenRouterError.From(400, Body, null));
        Assert.Equal(OpenRouterError.ContentPolicy("Nope"), OpenRouterError.From(403, Body, null));
        Assert.Equal(OpenRouterError.Unavailable(503), OpenRouterError.From(503, Array.Empty<byte>(), null));
        Assert.Equal(OpenRouterError.UnexpectedResponse(418, null), OpenRouterError.From(418, Array.Empty<byte>(), null));
    }

    [Fact]
    public void TheMessagesAreTheOriginalsEnglish()
    {
        Assert.Equal("Add your OpenRouter API key in Settings → AI.", OpenRouterError.MissingAPIKey().Message);
        Assert.Equal("OpenRouter rejected the API key.", OpenRouterError.InvalidAPIKey().Message);
        Assert.Equal("Your OpenRouter account is out of credits.", OpenRouterError.InsufficientCredits().Message);
        Assert.Equal("The request was blocked by content policy. Nope", OpenRouterError.ContentPolicy("Nope").Message);
        Assert.Equal("OpenRouter could not process the request.", OpenRouterError.BadRequest(null).Message);
        Assert.Equal("OpenRouter is unavailable (HTTP 503).", OpenRouterError.Unavailable(503).Message);
        Assert.Equal("Unexpected response (HTTP 418). teapot", OpenRouterError.UnexpectedResponse(418, "teapot").Message);
        Assert.Equal("Glimmer couldn't reach the AI service: The Internet connection appears to be offline. Check your internet connection and try again. The tools that run on this computer keep working.",
                     OpenRouterError.Network("The Internet connection appears to be offline.").Message);
        Assert.Equal("The model didn't return an image. It said: " + new string('a', 300), OpenRouterError.NoImageInResponse(new string('a', 400)).Message);
    }

    [Fact]
    public void DecodesKeyInfo()
    {
        var json = @"{""data"": {""label"": ""sk-or-v1-abc..."", ""usage"": 1.5, ""limit"": null, ""is_free_tier"": false}}";
        var info = OpenRouterKeyInfo.FromJson(JsonNode.Parse(json)!["data"])!;
        Assert.Equal(1.5, info.Usage);
        Assert.Null(info.Limit);
        Assert.False(info.IsFreeTier);
        Assert.Null(OpenRouterKeyInfo.FromJson(JsonNode.Parse(@"{""usage"": ""lots""}")));
    }

    [Fact]
    public async Task MissingKeyFailsBeforeNetwork()
    {
        var fake = new FakeProvider((200, "{}"));
        var client = new OpenRouterClient("", handler: fake);
        var error = await Assert.ThrowsAsync<OpenRouterError>(() => client.KeyInfoAsync());
        Assert.Equal(OpenRouterError.MissingAPIKey(), error);
        Assert.Empty(fake.Requests);
    }
}

public class SecretStoreTests
{
    [Fact]
    public void InMemoryStore()
    {
        var store = new InMemorySecretStore();
        Assert.Null(store.Read());
        store.Write("sk-test");
        Assert.Equal("sk-test", store.Read());
        store.Delete();
        Assert.Null(store.Read());
    }

    [Fact]
    public void TheEnvironmentStoreReadsAVariableAndChangesNothing()
    {
        var env = new Dictionary<string, string?> { ["OPENROUTER_API_KEY"] = "sk-env", ["EMPTY"] = "" };
        var store = new EnvironmentSecretStore("OPENROUTER_API_KEY", name => env.GetValueOrDefault(name));
        Assert.Equal("sk-env", store.Read());
        Assert.Null(new EnvironmentSecretStore("EMPTY", name => env.GetValueOrDefault(name)).Read());
        Assert.Null(new EnvironmentSecretStore("NOPE", name => env.GetValueOrDefault(name)).Read());
        Assert.Throws<SecretStoreException>(() => store.Write("x"));
        Assert.Throws<SecretStoreException>(() => store.Delete());
    }

    [Fact]
    public void AKeyInTheStoreWinsOverTheEnvironment()
    {
        var env = new EnvironmentSecretStore("K", _ => "from-env");
        var mine = new InMemorySecretStore();
        var both = new FallbackSecretStore(mine, env);
        Assert.Equal("from-env", both.Read());
        both.Write("saved");
        Assert.Equal("saved", both.Read());
        both.Delete();
        Assert.Equal("from-env", both.Read());
    }
}

public class OpenRouterClientTests
{
    private static OpenRouterClient Client(FakeProvider fake, string key = "sk-or", OpenRouterConfiguration? configuration = null)
    {
        var client = new OpenRouterClient(key, configuration, fake);
        client.Delay = (_, _) => Task.CompletedTask;
        return client;
    }

    private const string ToolAnswer = @"{""choices"":[{""message"":{""role"":""assistant"",""content"":""Done."",""tool_calls"":[{""id"":""c1"",""type"":""function"",""function"":{""name"":""crop"",""arguments"":""{\""aspect\"":\""1:1\""}""}}]}}],""usage"":{""cost"":0.002}}";

    private static readonly JsonObject Message = (JsonObject)JsonNode.Parse(@"{""role"":""user"",""content"":""Square""}")!;
    private static readonly JsonObject Tool = (JsonObject)JsonNode.Parse(@"{""type"":""function"",""function"":{""name"":""crop"",""parameters"":{""type"":""object""}}}")!;

    [Fact]
    public async Task KeyInfoIsAskedForWithTheKeyAndTheAppTitleOnly()
    {
        var fake = new FakeProvider((200, @"{""data"": {""usage"": 2.5, ""limit"": 10}}"));
        var info = await Client(fake).KeyInfoAsync();
        Assert.Equal(2.5, info.Usage);
        var sent = fake.First;
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("https://openrouter.ai/api/v1/key", sent.Url.ToString());
        Assert.Equal("Bearer sk-or", sent.Headers["Authorization"]);
        Assert.Equal("Glimmer", sent.Headers["X-Title"]);
        // Nothing identifying beyond what the original sends: no user agent, no referer, no content type without a body.
        Assert.False(sent.Headers.ContainsKey("User-Agent"));
        Assert.False(sent.Headers.ContainsKey("HTTP-Referer"));
        Assert.False(sent.Headers.ContainsKey("Content-Type"));
    }

    [Fact]
    public async Task AnUnreadableKeyAnswerIsUnexpected()
    {
        var error = await Assert.ThrowsAsync<OpenRouterError>(() => Client(new FakeProvider((200, @"{""data"": 3}"))).KeyInfoAsync());
        Assert.Equal(OpenRouterError.UnexpectedResponse(200, "unreadable key info"), error);
    }

    [Fact]
    public async Task TheTextRequestIsTheOriginalsBody()
    {
        var fake = new FakeProvider((200, ToolAnswer));
        var reply = await Client(fake).TextCompletionAsync("m", "sys", "user text", new[] { new byte[] { 1, 2 } }, "image/jpeg", jsonMode: true, temperature: 0.7);
        Assert.Equal("Done.", reply.Text);
        Assert.Equal(0.002, reply.Cost);
        var sent = fake.First;
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", sent.Url.ToString());
        Assert.Equal("application/json", sent.Headers["Content-Type"]);
        Assert.Equal("m", (string)sent.Json["model"]!);
        Assert.Equal(0.7, (double)sent.Json["temperature"]!);
        Assert.True((bool)sent.Json["usage"]!["include"]!);
        Assert.Equal("json_object", (string)sent.Json["response_format"]!["type"]!);
        var messages = sent.Json["messages"]!.AsArray();
        Assert.Equal("system", (string)messages[0]!["role"]!);
        var parts = messages[1]!["content"]!.AsArray();
        Assert.Equal("text", (string)parts[0]!["type"]!);
        Assert.Equal("data:image/jpeg;base64," + Convert.ToBase64String(new byte[] { 1, 2 }), (string)parts[1]!["image_url"]!["url"]!);
    }

    [Fact]
    public async Task APlainTextRequestHasAStringContent()
    {
        var fake = new FakeProvider((200, ToolAnswer));
        await Client(fake).TextCompletionAsync("m", null, "hello");
        var messages = fake.First.Json["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Equal("hello", (string)messages[0]!["content"]!);
        Assert.Null(fake.First.Json["response_format"]);
    }

    [Fact]
    public async Task TheDialectLeavesOutWhatAServiceRefuses()
    {
        var fake = new FakeProvider((200, ToolAnswer));
        var configuration = new OpenRouterConfiguration { Dialect = ChatDialect.Plain with { ToolChoice = false, Temperature = false, ReasoningEffort = "low" } };
        await Client(fake, configuration: configuration).ToolCompletionAsync("m", new[] { Message }, new[] { Tool });
        var json = fake.First.Json;
        Assert.Null(json["usage"]);
        Assert.Null(json["tool_choice"]);
        Assert.Null(json["temperature"]);
        Assert.Equal("low", (string)json["reasoning_effort"]!);
        Assert.True((bool)json["parallel_tool_calls"]!);
    }

    [Fact]
    public async Task AToolCompletionReturnsTextCallsAndCost()
    {
        var fake = new FakeProvider((200, ToolAnswer), (200, ToolAnswer));
        var client = Client(fake);
        var reply = await client.ToolCompletionAsync("m", new[] { Message }, new[] { Tool });
        Assert.Equal("Done.", reply.Text);
        Assert.Equal(new TextToolCall("crop", @"{""aspect"":""1:1""}"), Assert.Single(reply.ToolCalls));
        Assert.Equal(0.002, reply.Cost);
        Assert.Equal("auto", (string)fake.First.Json["tool_choice"]!);
        Assert.True((bool)fake.First.Json["parallel_tool_calls"]!);
        Assert.Equal(0.2, (double)fake.First.Json["temperature"]!);
        // The same message objects can be sent again (each request has its own copy).
        await client.ToolCompletionAsync("m", new[] { Message }, new[] { Tool });
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task ArgumentsThatAreAnObjectBecomeAString()
    {
        var fake = new FakeProvider((200, @"{""choices"":[{""message"":{""content"":null,""tool_calls"":[{""function"":{""name"":""crop"",""arguments"":{""aspect"":""1:1""}}},{""function"":{""name"":""undo""}}]}}]}"));
        var reply = await Client(fake).ToolCompletionAsync("m", new[] { Message }, new[] { Tool });
        Assert.Null(reply.Text);
        Assert.Equal(new[] { new TextToolCall("crop", @"{""aspect"":""1:1""}"), new TextToolCall("undo", "{}") }, reply.ToolCalls);
    }

    [Fact]
    public async Task AnAnswerWithoutAMessageIsUnexpected()
    {
        var error = await Assert.ThrowsAsync<OpenRouterError>(() => Client(new FakeProvider((200, "<html>hi</html>"))).ToolCompletionAsync("m", new[] { Message }, new[] { Tool }));
        Assert.Equal(OpenRouterError.UnexpectedResponse(200, "no message in response"), error);
        error = await Assert.ThrowsAsync<OpenRouterError>(() => Client(new FakeProvider((200, @"{""choices"":[]}"))).TextCompletionAsync("m", null, "x"));
        Assert.Equal(OpenRouterError.UnexpectedResponse(200, "no text in response"), error);
    }

    [Fact]
    public async Task AFatalErrorIsNotRetried()
    {
        var fake = new FakeProvider((401, @"{""error"":{""message"":""bad key""}}"), (200, ToolAnswer));
        var error = await Assert.ThrowsAsync<OpenRouterError>(() => Client(fake).TextCompletionAsync("m", null, "x"));
        Assert.Equal(OpenRouterError.InvalidAPIKey(), error);
        Assert.Single(fake.Requests);
        fake = new FakeProvider((402, "{}"), (200, ToolAnswer));
        error = await Assert.ThrowsAsync<OpenRouterError>(() => Client(fake).TextCompletionAsync("m", null, "x"));
        Assert.Equal(OpenRouterError.InsufficientCredits(), error);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task ARateLimitWaitsForRetryAfterAndTries4TimesAtMost()
    {
        var waited = new List<TimeSpan>();
        var fake = new FakeProvider(new FakeProvider.Answer(429, "{}", new Dictionary<string, string> { ["Retry-After"] = "7" }), new FakeProvider.Answer(200, ToolAnswer));
        var client = Client(fake);
        client.Delay = (span, _) => { waited.Add(span); return Task.CompletedTask; };
        var reply = await client.TextCompletionAsync("m", null, "x");
        Assert.Equal("Done.", reply.Text);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, waited);

        // Retry-After is capped at a minute.
        waited.Clear();
        fake.Script(new FakeProvider.Answer(429, "{}", new Dictionary<string, string> { ["Retry-After"] = "600" }), new FakeProvider.Answer(200, ToolAnswer));
        await client.TextCompletionAsync("m", null, "x");
        Assert.Equal(new[] { TimeSpan.FromSeconds(60) }, waited);

        // Always rate limited: the first try and four retries, then the error.
        waited.Clear();
        fake.Script(Enumerable.Repeat(new FakeProvider.Answer(429, "{}"), 10).ToArray());
        var error = await Assert.ThrowsAsync<OpenRouterError>(() => client.TextCompletionAsync("m", null, "x"));
        Assert.Equal(OpenRouterErrorKind.RateLimited, error.Kind);
        Assert.Equal(5, fake.Requests.Count);
        Assert.Equal(4, waited.Count);
    }

    [Fact]
    public async Task ServerErrorsAndDroppedConnectionsBackOffExponentiallyThenSucceed()
    {
        var waited = new List<TimeSpan>();
        var fake = new FakeProvider(
            new FakeProvider.Answer(503, "{}"), new FakeProvider.Answer(0, "", Throw: new HttpRequestException("The connection was reset.")),
            new FakeProvider.Answer(502, "{}"), new FakeProvider.Answer(200, ToolAnswer));
        var client = Client(fake);
        client.Jitter = () => 1.0;
        client.Delay = (span, _) => { waited.Add(span); return Task.CompletedTask; };
        var reply = await client.TextCompletionAsync("m", null, "x");
        Assert.Equal("Done.", reply.Text);
        Assert.Equal(new[] { TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, waited);
    }

    [Fact]
    public async Task ADeadConnectionEndsAsANetworkErrorInTheOriginalsWords()
    {
        var fake = new FakeProvider(Enumerable.Repeat(new FakeProvider.Answer(0, "", Throw: new HttpRequestException("No such host is known.")), 10).ToArray());
        var error = await Assert.ThrowsAsync<OpenRouterError>(() => Client(fake).TextCompletionAsync("m", null, "x"));
        Assert.Equal(OpenRouterErrorKind.Network, error.Kind);
        Assert.StartsWith("Glimmer couldn't reach the AI service: No such host is known.", error.Message);
        Assert.Equal(5, fake.Requests.Count);
    }

    [Fact]
    public async Task ACancelIsNotARetryableFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var fake = new FakeProvider((200, ToolAnswer));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(fake).TextCompletionAsync("m", null, "x", ct: cts.Token));
    }

    [Fact]
    public async Task AnotherServicesRefusalCarriesItsNameStatusAndWords()
    {
        var configuration = new OpenRouterConfiguration { ProviderName = "Berget", Dialect = ChatDialect.Plain };
        var fake = new FakeProvider((404, @"{""error"":{""message"":""The model `glm-9` does not exist.""}}"));
        var error = await Assert.ThrowsAsync<ProviderHttpError>(() => Client(fake, configuration: configuration).TextCompletionAsync("m", null, "x"));
        Assert.Equal("Berget answered HTTP 404: The model `glm-9` does not exist.", error.Message);
        // Not JSON: the body as it came.
        fake = new FakeProvider((401, "Unauthorized"));
        error = await Assert.ThrowsAsync<ProviderHttpError>(() => Client(fake, configuration: configuration).TextCompletionAsync("m", null, "x"));
        Assert.Equal("Berget answered HTTP 401: Unauthorized", error.Message);
        // `{"error": "text"}` and `{"detail": "text"}`.
        fake = new FakeProvider((400, @"{""error"":""plain words""}"));
        error = await Assert.ThrowsAsync<ProviderHttpError>(() => Client(fake, configuration: configuration).TextCompletionAsync("m", null, "x"));
        Assert.Equal("Berget answered HTTP 400: plain words", error.Message);
        // What is worth retrying keeps its kind: the retry loop still works for it.
        fake = new FakeProvider((503, "{}"), (200, ToolAnswer));
        Assert.Equal("Done.", (await Client(fake, configuration: configuration).TextCompletionAsync("m", null, "x")).Text);
    }

    [Fact]
    public async Task ALocalServerNeedsNoKeyAndIsSentNone()
    {
        var fake = new FakeProvider((200, ToolAnswer));
        var configuration = new OpenRouterConfiguration(new Uri("http://127.0.0.1:11434/v1")) { NeedsKey = false, Dialect = ChatDialect.Plain };
        await Client(fake, key: "", configuration: configuration).TextCompletionAsync("m", null, "x");
        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions", fake.First.Url.ToString());
        Assert.False(fake.First.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task ImageCompletionFindsThePictureInEachOfTheThreeShapes()
    {
        var png = new byte[] { 137, 80, 78, 71 };
        var b64 = Convert.ToBase64String(png);
        var shapes = new[]
        {
            $@"{{""model"":""g"",""choices"":[{{""message"":{{""images"":[{{""image_url"":{{""url"":""data:image/png;base64,{b64}""}}}}]}}}}],""usage"":{{""cost"":0.13}}}}",
            $@"{{""choices"":[{{""message"":{{""content"":[{{""type"":""text"",""text"":""here""}},{{""type"":""image_url"",""image_url"":{{""url"":""data:image/png;base64,{b64}""}}}}]}}}}]}}",
            $@"{{""choices"":[{{""message"":{{""content"":""![x](data:image/png;base64,{b64}) enjoy""}}}}]}}",
        };
        foreach (var shape in shapes)
        {
            var fake = new FakeProvider((200, shape));
            var result = await Client(fake).ImageCompletionAsync("google/gemini-3-pro-image", "a red ball", new[] { new byte[] { 1 } }, seed: 7);
            Assert.Equal(png, result.ImageData);
            var sent = fake.First;
            Assert.Equal(7, (int)sent.Json["seed"]!);
            Assert.Equal("image", (string)sent.Json["modalities"]![0]!);
            Assert.True((bool)sent.Json["usage"]!["include"]!);
        }
        var first = await Client(new FakeProvider((200, shapes[0]))).ImageCompletionAsync("m", "t", Array.Empty<byte[]>());
        Assert.Equal(0.13, first.Cost);
        Assert.Equal("g", first.Model);
    }

    [Fact]
    public async Task ImageCompletionWithoutAPictureSaysWhatTheModelSaid()
    {
        var fake = new FakeProvider((200, @"{""choices"":[{""message"":{""content"":""Which colour?""}}]}"));
        var error = await Assert.ThrowsAsync<OpenRouterError>(() => Client(fake).ImageCompletionAsync("m", "t", Array.Empty<byte[]>()));
        Assert.Equal(OpenRouterError.NoImageInResponse("Which colour?"), error);
        error = await Assert.ThrowsAsync<OpenRouterError>(() => Client(new FakeProvider((200, "no json"))).ImageCompletionAsync("m", "t", Array.Empty<byte[]>()));
        Assert.Equal(OpenRouterError.UnexpectedResponse(200, "not JSON"), error);
    }

    [Fact]
    public async Task TheImageApiGetsPromptAndReferencesAndReturnsThePictureAndItsCost()
    {
        const string pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
        var fake = new FakeProvider((200, $@"{{""created"":1,""data"":[{{""b64_json"":""{pixel}"",""media_type"":""image/png""}}],""usage"":{{""cost"":0.134}}}}"));
        var result = await Client(fake).ImageGenerationAsync("google/gemini-3-pro-image", "Make it snowy", new[] { new byte[] { 9 } }, seed: 3);
        Assert.Equal(Convert.FromBase64String(pixel), result.ImageData);
        Assert.Equal(0.134, result.Cost);
        Assert.Equal("google/gemini-3-pro-image", result.Model);
        var sent = fake.First;
        Assert.Equal("https://openrouter.ai/api/v1/images", sent.Url.ToString());
        Assert.Equal("Make it snowy", (string)sent.Json["prompt"]!);
        Assert.Equal(1, (int)sent.Json["n"]!);
        Assert.Equal(3, (int)sent.Json["seed"]!);
        var reference = sent.Json["input_references"]!.AsArray()[0]!;
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(new byte[] { 9 }), (string)reference["image_url"]!["url"]!);

        // Plain generation sends no references.
        fake = new FakeProvider((200, $@"{{""data"":[{{""b64_json"":""{pixel}""}}]}}"));
        await Client(fake).ImageGenerationAsync("m", "p", Array.Empty<byte[]>());
        Assert.Null(fake.First.Json["input_references"]);
        Assert.Null(fake.First.Json["seed"]);

        // An answer without a picture says so, with the service's own message when it has one.
        var error = await Assert.ThrowsAsync<OpenRouterError>(() => Client(new FakeProvider((200, @"{""error"":{""message"":""no budget""}}"))).ImageGenerationAsync("m", "p", Array.Empty<byte[]>()));
        Assert.Equal(OpenRouterError.NoImageInResponse("no budget"), error);
    }

    [Fact]
    public async Task AStreamedReplyIsPutTogetherAndTheTextIsHeardAsItGrows()
    {
        static string Sse(params string[] events) => string.Concat(events.Select(e => $"data: {e}\n\n"));
        var fake = new FakeProvider((200, Sse(
            @"{""choices"":[{""delta"":{""content"":""Crop""}}]}",
            @"{""choices"":[{""delta"":{""content"":""ping.""}}]}",
            @"{""choices"":[{""delta"":{""tool_calls"":[{""index"":0,""function"":{""name"":""cr"",""arguments"":""{\""asp""}}]}}]}",
            @"{""choices"":[{""delta"":{""tool_calls"":[{""index"":0,""function"":{""name"":""op"",""arguments"":""ect\"":\""1:1\""}""}}]}}]}",
            @"{""choices"":[{""delta"":{""tool_calls"":[{""index"":1,""function"":{""name"":""undo""}}]}}],""usage"":{""cost"":0.01}}",
            "[DONE]")));
        var heard = new List<string>();
        var reply = await Client(fake).ToolCompletionStreamingAsync("m", new[] { Message }, new[] { Tool }, heard.Add);
        Assert.Equal(new[] { "Crop", "Cropping." }, heard);
        Assert.Equal("Cropping.", reply.Text);
        Assert.Equal(new[] { new TextToolCall("crop", @"{""aspect"":""1:1""}"), new TextToolCall("undo", "{}") }, reply.ToolCalls);
        Assert.Equal(0.01, reply.Cost);
        Assert.True((bool)fake.First.Json["stream"]!);
    }

    [Fact]
    public async Task AStreamThatCantStartFallsBackToTheOrdinaryCall()
    {
        var fake = new FakeProvider((503, "{}"), (200, ToolAnswer));
        var heard = new List<string>();
        var reply = await Client(fake).ToolCompletionStreamingAsync("m", new[] { Message }, new[] { Tool }, heard.Add);
        Assert.Equal("Done.", reply.Text);
        Assert.Empty(heard);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Null(fake.Requests[1].Json["stream"]);
    }

    [Fact]
    public async Task AKeyedRequestNeverFollowsARedirectToAnotherHost()
    {
        var fake = new FakeProvider(
            new FakeProvider.Answer(302, "", new Dictionary<string, string> { ["Location"] = "https://evil.example/collect" }),
            new FakeProvider.Answer(200, ToolAnswer));
        var error = await Assert.ThrowsAsync<OpenRouterError>(() => Client(fake).KeyInfoAsync());
        Assert.Equal(OpenRouterError.UnexpectedResponse(302, null), error);
        Assert.Single(fake.Requests);

        // Another service says so with its name.
        var configuration = new OpenRouterConfiguration { ProviderName = "Berget", Dialect = ChatDialect.Plain };
        fake.Script(new FakeProvider.Answer(302, "", new Dictionary<string, string> { ["Location"] = "https://evil.example/collect" }));
        var refusal = await Assert.ThrowsAsync<ProviderHttpError>(() => Client(fake, configuration: configuration).TextCompletionAsync("m", null, "x"));
        Assert.Equal("Berget answered HTTP 302.", refusal.Message);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public void DefaultsAndTheRetirementNotes()
    {
        Assert.Equal("google/gemini-3-pro-image", AIDefaults.ImageModel);
        Assert.Equal("google/gemini-3.5-flash", AIDefaults.TextModel);
        Assert.Contains("gemini-3.5-flash-lite", AIDefaults.Retirement(" google/gemini-2.5-flash "));
        Assert.Contains("June 25, 2026", AIDefaults.Retirement("google/gemini-3-pro-image-preview"));
        Assert.Null(AIDefaults.Retirement(AIDefaults.TextModel));
        Assert.Equal("https://openrouter.ai/api/v1", AIDefaults.EndpointFrom(null).ToString());
        Assert.Equal("https://openrouter.ai/api/v1", AIDefaults.EndpointFrom("not a url").ToString());
        Assert.Equal("https://openrouter.ai/api/v1", AIDefaults.EndpointFrom("ftp://x.example/").ToString());
        Assert.Equal("https://gateway.example/v1", AIDefaults.EndpointFrom("  https://gateway.example/v1 ").ToString());
    }
}
