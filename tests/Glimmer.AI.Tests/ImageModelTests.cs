// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/LocalModelTests.swift (the OpenRouter Image API and the image adapters),
// ProviderTests.swift (the image half of everyOfferedServiceShowsARefusal…) and PipelineTests.swift (ResponseParsingTests).
// Every test hands the code a fake handler: nothing reaches a network and nothing costs money.
using System.Text.Json.Nodes;
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

public class ImageModelTests
{
    /// <summary>A 1×1 PNG.</summary>
    const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    static RGBAImage Input => new(4, 4, new System.Numerics.Vector4(10, 20, 30, 255));
    static bool DataUrl(JsonNode? n) => ((string?)n)?.StartsWith("data:image/png;base64,") == true;

    static OpenRouterImageModel OpenRouter(FakeProvider fake, OpenRouterImageModel.RouteKind? route = null, string model = "google/gemini-3-pro-image") =>
        route is { } r
            ? new OpenRouterImageModel(new OpenRouterClient("sk-or", AIProvider.OpenRouter.ChatConfiguration(), fake), model, r)
            : new OpenRouterImageModel(new OpenRouterClient("sk-or", AIProvider.OpenRouter.ChatConfiguration(), fake), model);

    [Fact]
    public async Task TheImageAPIGetsPromptAndReferencesAndReturnsThePictureAndItsCost()
    {
        var fake = new FakeProvider((200, $$$"""{"created":1,"data":[{"b64_json":"{{{Pixel}}}","media_type":"image/png"}],"usage":{"prompt_tokens":3,"completion_tokens":1120,"total_tokens":1123,"cost":0.134}}"""));
        var result = await OpenRouter(fake, OpenRouterImageModel.RouteKind.Images).GenerateAsync("Make it snowy", [Input], 7);
        Assert.True(result.Image.Width == 1 && result.Image.Height == 1 && result.Cost == 0.134);
        var sent = fake.First;
        Assert.Equal("https://openrouter.ai/api/v1/images", sent.Url.ToString());
        Assert.Equal("Bearer sk-or", sent.Headers["Authorization"]);
        Assert.True((string?)sent.Json["model"] == "google/gemini-3-pro-image" && (string?)sent.Json["prompt"] == "Make it snowy");
        Assert.True((int?)sent.Json["seed"] == 7 && sent.Json["modalities"] is null && sent.Json["messages"] is null);
        var reference = sent.Json["input_references"]!.AsArray()[0]!;
        Assert.Equal("image_url", (string?)reference["type"]);
        Assert.True(DataUrl(reference["image_url"]!["url"]));
    }

    [Fact]
    public async Task PlainGenerationSendsNoReferences()
    {
        var fake = new FakeProvider((200, $$$"""{"data":[{"b64_json":"{{{Pixel}}}","media_type":"image/png"}],"usage":{"cost":0.03}}"""));
        await OpenRouter(fake, OpenRouterImageModel.RouteKind.Images).GenerateAsync("A red balloon", [], null);
        Assert.True(fake.First.Json["input_references"] is null && fake.First.Json["seed"] is null);
    }

    [Fact]
    public async Task AnAnswerWithoutAPictureSaysSo()
    {
        var fake = new FakeProvider((200, """{"data":[],"usage":{"cost":0}}"""));
        var error = await Assert.ThrowsAsync<OpenRouterError>(() => OpenRouter(fake, OpenRouterImageModel.RouteKind.Images).GenerateAsync("p", [], null));
        Assert.Equal(OpenRouterErrorKind.NoImageInResponse, error.Kind);
    }

    /// <summary>The route Glimmer has used so far is unchanged, and still the default.</summary>
    [Fact]
    public async Task TheOldRouteIsUntouchedAndTheDefault()
    {
        var fake = new FakeProvider((200, $$$"""{"choices":[{"message":{"role":"assistant","content":"","images":[{"type":"image_url","image_url":{"url":"data:image/png;base64,{{{Pixel}}}"}}]}}],"usage":{"cost":0.1}}"""));
        var old = OpenRouter(fake, model: "google/gemini-3-pro-image-preview");
        Assert.Equal(OpenRouterImageModel.RouteKind.Chat, old.Route);
        await old.GenerateAsync("p", [], null);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", fake.First.Url.ToString());
        Assert.Equal(["image", "text"], fake.First.Json["modalities"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public async Task AResultIsKeptAndReadAgainWithoutAnotherCall()
    {
        var answer = $$$"""{"choices":[{"message":{"role":"assistant","content":"","images":[{"type":"image_url","image_url":{"url":"data:image/png;base64,{{{Pixel}}}"}}]}}],"usage":{"cost":0.1}}""";
        var fake = new FakeProvider((200, answer), (200, answer));
        using var folder = new TempFolder();
        var model = new OpenRouterImageModel(new OpenRouterClient("sk-or", AIProvider.OpenRouter.ChatConfiguration(), fake), "m", cacheDirectory: folder.Sub("cache"));
        var first = await model.GenerateAsync("p", [Input], 1);
        var second = await model.GenerateAsync("p", [Input], 1);
        Assert.Equal(0.1, first.Cost);
        Assert.Equal(0, second.Cost);                 // read from the cache: nothing to pay
        Assert.Single(fake.Requests);
        var other = await model.GenerateAsync("another", [Input], 1);   // a different request is a miss
        Assert.Equal(0.1, other.Cost);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task OpenAIGeneratesAndEditsWithThePicturesInTheRequest()
    {
        var fake = new FakeProvider((200, $$$"""{"created":1,"data":[{"b64_json":"{{{Pixel}}}"}],"usage":{"input_tokens":10,"output_tokens":272}}"""),
                                    (200, $$$"""{"created":1,"data":[{"b64_json":"{{{Pixel}}}"}]}"""));
        var model = AIProvider.Named("openai")!.ImageModel("sk-test", "some-image-model", handler: fake)!;
        Assert.Equal(1, (await model.GenerateAsync("A red balloon", [], null)).Image.Width);
        await model.GenerateAsync("Make it snowy", [Input], null);
        var sent = fake.Requests;
        Assert.True(sent[0].Url.ToString() == "https://api.openai.com/v1/images/generations" && sent[0].Headers["Authorization"] == "Bearer sk-test");
        Assert.True((string?)sent[0].Json["prompt"] == "A red balloon" && sent[0].Json["images"] is null);
        Assert.Equal("https://api.openai.com/v1/images/edits", sent[1].Url.ToString());
        Assert.True(DataUrl(sent[1].Json["images"]!.AsArray()[0]!["image_url"]));
    }

    [Fact]
    public async Task GoogleSendsInlinePartsAndReadsThePictureFromTheAnswer()
    {
        var fake = new FakeProvider((200, $$$"""{"candidates":[{"content":{"parts":[{"text":"Here you are."},{"inlineData":{"mimeType":"image/png","data":"{{{Pixel}}}"}}]}}]}"""),
                                    (200, """{"candidates":[{"content":{"parts":[{"text":"I can't make that picture."}]}}]}"""));
        var model = AIProvider.Named("google")!.ImageModel("gk", "some-image-model", handler: fake)!;
        Assert.Equal(1, (await model.GenerateAsync("Make it snowy", [Input], null)).Image.Height);
        var sent = fake.First;
        Assert.Equal("https://generativelanguage.googleapis.com/v1/models/some-image-model:generateContent", sent.Url.ToString());
        Assert.True(sent.Headers["x-goog-api-key"] == "gk" && !sent.Headers.ContainsKey("Authorization"));
        var parts = sent.Json["contents"]!.AsArray()[0]!["parts"]!.AsArray();
        Assert.Equal("Make it snowy", (string?)parts[0]!["text"]);
        Assert.Equal("image/png", (string?)parts[^1]!["inline_data"]!["mime_type"]);
        // An answer with words and no picture says what the model said.
        var error = await Assert.ThrowsAsync<OpenRouterError>(() => model.GenerateAsync("p", [], null));
        Assert.Contains("I can't make that picture.", error.Message);
    }

    [Fact]
    public async Task FalGetsDataURIsAndNeverAStoredResult()
    {
        var fake = new FakeProvider((200, $$$"""{"images":[{"url":"data:image/png;base64,{{{Pixel}}}","content_type":"image/png"}]}"""),
                                    (200, $$$"""{"images":[{"url":"data:image/png;base64,{{{Pixel}}}"}]}"""),
                                    (200, """{"images":[{"url":"https://v3.fal.media/files/x/out.png"}]}"""));
        var model = AIProvider.Named("fal")!.ImageModel("fk", "fal-ai/some-model", handler: fake)!;
        await model.GenerateAsync("A red balloon", [], 3);
        await model.GenerateAsync("Make it snowy", [Input], null);
        var sent = fake.Requests;
        Assert.True(sent[0].Url.ToString() == "https://fal.run/fal-ai/some-model" && sent[0].Headers["Authorization"] == "Key fk");
        Assert.True(sent[0].Json["sync_mode"]!.GetValue<bool>() && (int?)sent[0].Json["seed"] == 3 && sent[0].Json["image_urls"] is null);
        Assert.Equal("https://fal.run/fal-ai/some-model/edit", sent[1].Url.ToString());
        Assert.True(DataUrl(sent[1].Json["image_urls"]!.AsArray()[0]));
        // A link instead of the picture means it was stored at fal: not fetched, and said plainly.
        var error = await Assert.ThrowsAsync<ProviderHttpError>(() => model.GenerateAsync("p", [], null));
        Assert.Contains("doesn't fetch stored results", error.Message);
        Assert.Equal(3, fake.Requests.Count);   // three calls to fal.run, and no request to the stored file
    }

    [Fact]
    public async Task HedraUploadsSubmitsWaitsAndDownloads()
    {
        var fake = new FakeProvider(
            (200, """{"url":"https://uploads.hedra.example/handle?sig=abc","content_type":"image/png","expires_at":"2026-11-16T09:00:00Z"}"""),
            (202, """{"job_id":"job_1"}"""),
            (200, """{"status":"IN_PROGRESS"}"""),
            (200, """{"status":"COMPLETED"}"""),
            (200, """{"status":"COMPLETED","outputs":[{"status":"COMPLETED","url":"https://outputs.hedra.example/signed?x=1","asset_id":"asset_1"}]}"""),
            (200, "base64:" + Pixel));
        var model = new HedraImageModel("id:secret", "some-image-model", handler: fake, pollInterval: TimeSpan.FromMilliseconds(5));
        var result = await model.GenerateAsync("Make it snowy", [Input], null);
        Assert.Equal(1, result.Image.Width);
        var sent = fake.Requests;
        Assert.Equal(["https://api.hedra.com/v3/files", "https://api.hedra.com/v3/models/some-image-model",
                      "https://api.hedra.com/v3/jobs/job_1/status", "https://api.hedra.com/v3/jobs/job_1/status",
                      "https://api.hedra.com/v3/jobs/job_1", "https://outputs.hedra.example/signed?x=1"], sent.Select(s => s.Url.ToString()));
        // The upload is the picture's bytes in a multipart body.
        Assert.True(sent[0].Headers["Content-Type"].StartsWith("multipart/form-data; boundary=") && sent[0].Headers["Authorization"] == "Key id:secret");
        Assert.True(sent[0].Body.AsSpan().IndexOf(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) >= 0);
        // The job refers to the upload by its handle, unchanged.
        var image = sent[1].Json["input"]!["images"]!.AsArray()[0]!;
        Assert.True((string?)image["source"] == "url" && (string?)image["url"] == "https://uploads.hedra.example/handle?sig=abc");
        // The key is not sent with the signed download.
        Assert.False(sent[5].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task AFailedHedraJobSaysSoAndAnErrorIsShownAsItCame()
    {
        var fake = new FakeProvider((202, """{"job_id":"job_2"}"""), (200, """{"status":"FAILED"}"""));
        var model = new HedraImageModel("k", "m", handler: fake, pollInterval: TimeSpan.FromMilliseconds(5));
        var failed = await Assert.ThrowsAsync<ProviderHttpError>(() => model.GenerateAsync("p", [], null));
        Assert.Equal("Hedra answered HTTP 200: The job failed at Hedra.", failed.Message);
        fake.Script((402, """{"error":"Insufficient wallet balance"}"""));
        var refused = await Assert.ThrowsAsync<ProviderHttpError>(() => model.GenerateAsync("p", [], null));
        Assert.Equal("Hedra answered HTTP 402: Insufficient wallet balance", refused.Message);
    }

    [Fact]
    public async Task AHedraJobThatNeverFinishesStopsAtItsDeadline()
    {
        var fake = new FakeProvider((202, """{"job_id":"job_3"}"""), (200, """{"status":"IN_PROGRESS"}"""), (200, """{"status":"IN_PROGRESS"}"""), (200, """{"status":"IN_PROGRESS"}"""));
        var model = new HedraImageModel("k", "m", handler: fake, pollInterval: TimeSpan.FromMilliseconds(5), deadline: TimeSpan.FromMilliseconds(8));
        var error = await Assert.ThrowsAsync<ProviderHttpError>(() => model.GenerateAsync("p", [], null));
        Assert.Equal("Hedra answered HTTP 200: Hedra's job didn't finish in time.", error.Message);
    }

    [Fact]
    public void EveryNewImageServiceIsBuiltAndOffered()
    {
        foreach (var slug in new[] { "openai", "google", "fal", "hedra" })
            Assert.True(AIProvider.Named(slug)!.Built.Contains(AIProvider.Capability.Image) && AIProvider.Named(slug)!.Offers(AIProvider.Capability.Image), slug);
        Assert.Equal(["openrouter", "openai", "google", "fal", "hedra"], AIProvider.Offering(AIProvider.Capability.Image).Select(p => p.Slug));
        Assert.True(AIProvider.Named("openrouter")!.ImageModel("k", "m") is null && AIProvider.Named("berget")!.ImageModel("k", "m") is null);
    }

    /// <summary>
    /// Every service that is offered, one by one (they are all shown since 2026-11-16, most of them never called): what the service
    /// answers in refusal reaches the person word for word with its status, and an answer that can't be used is an error, never a picture.
    /// </summary>
    [Fact]
    public async Task EveryOfferedImageServiceShowsARefusalWordForWordAndMakesNothingOfAnUnusableAnswer()
    {
        const string words = "This request cannot be served: the account is restricted (ref 7Q-ZETA).";
        var refusal = $$$"""{"error":{"message":"{{{words}}}","type":"restricted"}}""";
        var fake = new FakeProvider();
        var images = new List<(string, IImageModel)> { ("openrouter", new OpenRouterImageModel(new OpenRouterClient("k", AIProvider.OpenRouter.ChatConfiguration(), fake), "m", OpenRouterImageModel.RouteKind.Chat)) };
        foreach (var p in AIProvider.Offering(AIProvider.Capability.Image).Where(p => p.Slug != "openrouter"))
            images.Add((p.Slug, p.ImageModel("k", "m", "e", fake)!));
        Assert.Equal(["openrouter", "openai", "google", "fal", "hedra"], images.Select(i => i.Item1));
        foreach (var (slug, model) in images)
        {
            fake.Script((422, refusal));
            var error = await Assert.ThrowsAnyAsync<Exception>(() => model.GenerateAsync("a red ball", [], null));
            Assert.True(error.Message.Contains(words) && (error.Message.Contains("422") || slug == "openrouter"), $"{slug}: {error.Message}");
            fake.Script((200, """{"ok":true}"""), (200, """{"ok":true}"""), (200, """{"ok":true}"""));
            await Assert.ThrowsAnyAsync<Exception>(() => model.GenerateAsync("a red ball", [], null));   // an answer with no picture in it is no picture
        }
    }

    // ---- ResponseParsingTests (PipelineTests.swift)

    static readonly string Png = "data:image/png;base64," + Convert.ToBase64String(new RGBAImage(2, 2, new System.Numerics.Vector4(1, 2, 3, 255)).PngData());

    [Fact]
    public void ImagesArray()
    {
        var json = TJson.Obj($$$"""{"choices": [{"message": {"content": "here", "images": [{"image_url": {"url": "{{{Png}}}"}}]}}], "usage": {"cost": 0.04}}""");
        Assert.NotNull(OpenRouterClient.ExtractImage(json));
        Assert.Equal(0.04, OpenRouterClient.Cost(json));
    }

    [Fact]
    public void ContentParts()
    {
        var json = TJson.Obj($$$"""{"choices": [{"message": {"content": [{"type": "output_image", "url": "{{{Png}}}"}]}}]}""");
        Assert.NotNull(OpenRouterClient.ExtractImage(json));
    }

    [Fact]
    public void EmbeddedInText()
    {
        var json = TJson.Obj($$$"""{"choices": [{"message": {"content": "Sure! ![img]({{{Png}}}) enjoy"}}]}""");
        Assert.NotNull(OpenRouterClient.ExtractImage(json));
        var none = TJson.Obj("""{"choices": [{"message": {"content": "I can't do that."}}]}""");
        Assert.Null(OpenRouterClient.ExtractImage(none));
        Assert.Equal("I can't do that.", OpenRouterClient.ExtractText(none));
    }
}
