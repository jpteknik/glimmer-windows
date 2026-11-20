// Ported from Packages/GlimmerKit/Sources/GlimmerAI/ImageProviders.swift
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Glimmer.Core;

namespace Glimmer.AI;

/// <summary>
/// Image models of services other than OpenRouter: OpenAI, Google, fal and Hedra. Each takes
/// the one request shape all of Glimmer's picture features use (a prompt and up to a few
/// pictures in, one picture out), built from the service's documentation as read on
/// 2026-11-16. None has been tried with a real call; each stays hidden until one has.
///
/// The pictures always travel in the request as bytes (rule 10 of the-apps/PROVIDERS.md):
/// nothing here makes a service fetch from the machine, and nothing is put on public storage.
/// </summary>
internal static class ImageHTTP
{
    /// <summary>Sends <paramref name="request"/> and returns the body of a 2xx answer; anything else is the service's own words.</summary>
    public static async Task<byte[]> SendAsync(RequestSpec request, string provider, HttpClient http, CancellationToken ct)
    {
        HttpData data;
        try { data = await Http.DataAsync(http, request, ct).ConfigureAwait(false); }
        catch (Exception error) when (Http.IsConnectionFailure(error, ct)) { throw OpenRouterError.Network(Http.Describe(error)); }
        if (data.Status is < 200 or >= 300)
            throw new ProviderHttpError(provider, data.Status, ProviderHttpError.MessageFrom(data.Body));
        return data.Body;
    }

    public static JsonObject Json(byte[] data, string provider) =>
        Glimmer.AI.Json.ParseObject(data) ?? throw new ProviderHttpError(provider, 200, "The answer wasn't in the form Glimmer expects.");

    public static RequestSpec Post(Uri url, IEnumerable<KeyValuePair<string, string>> headers, JsonObject body)
    {
        var request = new RequestSpec(url, "POST") { Timeout = TimeSpan.FromSeconds(300) };
        foreach (var (name, value) in headers) request.SetHeader(name, value);
        request.Body = Glimmer.AI.Json.Bytes(body);
        return request;
    }

    /// <summary>The picture in a base64 string or a <c>data:</c> URL.</summary>
    public static byte[]? Picture(string text)
    {
        var encoded = text;
        if (text.StartsWith("data:", StringComparison.Ordinal))
        {
            var comma = text.IndexOf(',');
            encoded = comma < 0 ? "" : text[(comma + 1)..];
        }
        // `.ignoreUnknownCharacters`: whitespace and the like are skipped.
        var clean = new StringBuilder(encoded.Length);
        foreach (var c in encoded)
            if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=') clean.Append(c);
        var s = clean.ToString().TrimEnd('=');
        if (s.Length % 4 == 1) return null;
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(s); } catch (FormatException) { return null; }
    }

    public static AIImageResult Result(byte[]? data, string provider, string? said = null)
    {
        if (data is null || data.Length == 0) throw OpenRouterError.NoImageInResponse(said ?? $"{provider} returned no picture.");
        RGBAImage image;
        try { image = RGBAImage.Decode(data); } catch (InvalidDataException) { throw OpenRouterError.NoImageInResponse(said ?? $"{provider} returned no picture."); }
        return new AIImageResult(image, null);
    }

    public static string DataUrl(RGBAImage image) => "data:image/png;base64," + Convert.ToBase64String(image.Limited(2048).PngData());
}

/// <summary>
/// OpenAI's Image API: <c>POST /v1/images/generations</c>, and <c>POST /v1/images/edits</c> with the
/// input pictures as data URLs ("A fully qualified URL or base64-encoded data URL"). The
/// answer is base64 in <c>data[0].b64_json</c>.
/// </summary>
public sealed class OpenAIImageModel : IImageModel
{
    readonly string key, model;
    readonly Uri baseUrl;
    readonly HttpClient http;

    public OpenAIImageModel(string key, string model, Uri? baseUrl = null, HttpMessageHandler? handler = null)
    {
        this.key = key; this.model = model; this.baseUrl = baseUrl ?? new Uri("https://api.openai.com/v1");
        http = KeyStaysHome.CreateClient(handler);
    }

    public async Task<AIImageResult> GenerateAsync(string prompt, IReadOnlyList<RGBAImage> images, int? seed, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["model"] = model, ["prompt"] = prompt, ["n"] = 1 };
        string path;
        if (images.Count == 0) path = "images/generations";
        else
        {
            path = "images/edits";
            body["images"] = new JsonArray(images.Select(i => (JsonNode?)new JsonObject { ["image_url"] = ImageHTTP.DataUrl(i) }).ToArray());
        }
        var request = ImageHTTP.Post(Http.Append(baseUrl, path), [new("Authorization", $"Bearer {key}")], body);
        var json = ImageHTTP.Json(await ImageHTTP.SendAsync(request, "OpenAI", http, cancellationToken).ConfigureAwait(false), "OpenAI");
        var encoded = Glimmer.AI.Json.Objects(json["data"])?.FirstOrDefault() is { } first && Glimmer.AI.Json.Str(first["b64_json"]) is { } b ? ImageHTTP.Picture(b) : null;
        return ImageHTTP.Result(encoded, "OpenAI");
    }
}

/// <summary>
/// Google's image models through <c>generateContent</c> (the form with a documented REST body):
/// the prompt and the input pictures as <c>inline_data</c> parts; the picture comes back as an
/// inline part among the answer's parts.
/// </summary>
public sealed class GoogleImageModel : IImageModel
{
    readonly string key, model;
    readonly Uri baseUrl;
    readonly HttpClient http;

    public GoogleImageModel(string key, string model, Uri? baseUrl = null, HttpMessageHandler? handler = null)
    {
        this.key = key; this.model = model; this.baseUrl = baseUrl ?? new Uri("https://generativelanguage.googleapis.com/v1");
        http = KeyStaysHome.CreateClient(handler);
    }

    public async Task<AIImageResult> GenerateAsync(string prompt, IReadOnlyList<RGBAImage> images, int? seed, CancellationToken cancellationToken = default)
    {
        var parts = new JsonArray { new JsonObject { ["text"] = prompt } };
        foreach (var image in images)
            parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "image/png", ["data"] = Convert.ToBase64String(image.Limited(2048).PngData()) } });
        var request = ImageHTTP.Post(Http.Append(baseUrl, $"models/{model}:generateContent"), [new("x-goog-api-key", key)],
                                     new JsonObject { ["contents"] = new JsonArray(new JsonObject { ["parts"] = parts }) });
        var json = ImageHTTP.Json(await ImageHTTP.SendAsync(request, "Google", http, cancellationToken).ConfigureAwait(false), "Google");
        var content = Glimmer.AI.Json.Objects(json["candidates"])?.FirstOrDefault() is { } candidate ? Glimmer.AI.Json.Obj(candidate["content"]) : null;
        var answer = Glimmer.AI.Json.Objects(content?["parts"]) ?? [];
        var said = new List<string>();
        foreach (var part in answer)
        {
            if (Glimmer.AI.Json.Str(part["text"]) is { } text) said.Add(text);
            var inline = Glimmer.AI.Json.Obj(part["inlineData"] ?? part["inline_data"]);
            if (Glimmer.AI.Json.Str(inline?["data"]) is { } encoded && ImageHTTP.Picture(encoded) is { } data)
                return ImageHTTP.Result(data, "Google");
        }
        return ImageHTTP.Result(null, "Google", said.Count == 0 ? null : string.Join(" ", said));
    }
}

/// <summary>
/// fal, through its direct endpoint <c>https://fal.run/&lt;model id&gt;</c>. Input pictures go as data
/// URIs in <c>image_urls</c> and the result is asked for as a data URI (<c>sync_mode</c>), so nothing
/// is uploaded to fal's storage, which is public by default, and the result is not kept in
/// its request history. If a model refuses data URIs the call fails with fal's own message;
/// Glimmer never falls back to an upload.
/// </summary>
public sealed class FalImageModel : IImageModel
{
    readonly string key, model, editModel;
    readonly Uri baseUrl;
    readonly HttpClient http;

    /// <param name="editModel">The model id for requests with input pictures (fal has separate ids for editing, usually <c>&lt;id&gt;/edit</c>).</param>
    public FalImageModel(string key, string model, string? editModel = null, Uri? baseUrl = null, HttpMessageHandler? handler = null)
    {
        this.key = key; this.model = model; this.editModel = editModel ?? model + "/edit"; this.baseUrl = baseUrl ?? new Uri("https://fal.run");
        http = KeyStaysHome.CreateClient(handler);
    }

    public async Task<AIImageResult> GenerateAsync(string prompt, IReadOnlyList<RGBAImage> images, int? seed, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["prompt"] = prompt, ["num_images"] = 1, ["sync_mode"] = true };
        if (images.Count > 0) body["image_urls"] = new JsonArray(images.Select(i => (JsonNode?)ImageHTTP.DataUrl(i)).ToArray());
        if (seed is { } s) body["seed"] = s;
        var request = ImageHTTP.Post(Http.Append(baseUrl, images.Count == 0 ? model : editModel), [new("Authorization", $"Key {key}")], body);
        var json = ImageHTTP.Json(await ImageHTTP.SendAsync(request, "fal", http, cancellationToken).ConfigureAwait(false), "fal");
        if (!(Glimmer.AI.Json.Objects(json["images"])?.FirstOrDefault() is { } first && Glimmer.AI.Json.Str(first["url"]) is { } url))
            return ImageHTTP.Result(null, "fal");
        // With sync_mode the picture is in the answer. A plain URL would mean it was stored on
        // fal's side after all: it is not fetched, and the call fails rather than pretend.
        if (!url.StartsWith("data:", StringComparison.Ordinal))
            throw new ProviderHttpError("fal", 200, "fal returned a link instead of the picture itself. Glimmer asked for the picture in the answer and doesn't fetch stored results.");
        return ImageHTTP.Result(ImageHTTP.Picture(url), "fal");
    }
}

/// <summary>
/// Hedra's v3 API: input pictures are uploaded as bytes (<c>POST /v3/files</c>, a private upload
/// handle that expires after an hour), the job is submitted (<c>POST /v3/models/&lt;model&gt;</c>),
/// waited for (<c>GET /v3/jobs/&lt;id&gt;/status</c>) and its result downloaded from a signed URL,
/// which is used once and never logged.
/// </summary>
public sealed class HedraImageModel : IImageModel
{
    readonly string key, model;
    readonly Uri baseUrl;
    readonly HttpClient http;
    readonly TimeSpan pollInterval, deadline;

    public HedraImageModel(string key, string model, Uri? baseUrl = null, HttpMessageHandler? handler = null, TimeSpan? pollInterval = null, TimeSpan? deadline = null)
    {
        this.key = key; this.model = model; this.baseUrl = baseUrl ?? new Uri("https://api.hedra.com/v3");
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(3);
        this.deadline = deadline ?? TimeSpan.FromSeconds(600);
        http = KeyStaysHome.CreateClient(handler);
    }

    IEnumerable<KeyValuePair<string, string>> Headers => [new("Authorization", $"Key {key}")];

    RequestSpec Get(string path)
    {
        var request = new RequestSpec(Http.Append(baseUrl, path), "GET");
        foreach (var (name, value) in Headers) request.SetHeader(name, value);
        return request;
    }

    public async Task<AIImageResult> GenerateAsync(string prompt, IReadOnlyList<RGBAImage> images, int? seed, CancellationToken cancellationToken = default)
    {
        var input = new JsonObject { ["prompt"] = prompt };
        if (images.Count > 0)
        {
            var handles = new JsonArray();
            foreach (var image in images)
                handles.Add(new JsonObject { ["source"] = "url", ["url"] = await UploadAsync(image.Limited(2048).PngData(), cancellationToken).ConfigureAwait(false) });
            input["images"] = handles;
        }
        var submit = ImageHTTP.Post(Http.Append(baseUrl, $"models/{model}"), Headers, new JsonObject { ["input"] = input });
        var ack = ImageHTTP.Json(await ImageHTTP.SendAsync(submit, "Hedra", http, cancellationToken).ConfigureAwait(false), "Hedra");
        // The job's id goes into the path of requests that carry the key: only a plain identifier is accepted.
        if (KeyStaysHome.PathSafe(Glimmer.AI.Json.Str(ack["job_id"])) is not { } job)
            throw new ProviderHttpError("Hedra", 202, "Hedra didn't return a job to wait for.");
        var start = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = Glimmer.AI.Json.Str(ImageHTTP.Json(await ImageHTTP.SendAsync(Get($"jobs/{job}/status"), "Hedra", http, cancellationToken).ConfigureAwait(false), "Hedra")["status"]);
            if (state == "COMPLETED") break;
            if (state == "FAILED") throw new ProviderHttpError("Hedra", 200, "The job failed at Hedra.");
            if (DateTime.UtcNow - start >= deadline) throw new ProviderHttpError("Hedra", 200, "Hedra's job didn't finish in time.");
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
        var result = ImageHTTP.Json(await ImageHTTP.SendAsync(Get($"jobs/{job}"), "Hedra", http, cancellationToken).ConfigureAwait(false), "Hedra");
        var output = Glimmer.AI.Json.Objects(result["outputs"])?.FirstOrDefault();
        if (output is null || Glimmer.AI.Json.Str(output["status"]) != "COMPLETED" || KeyStaysHome.Fetchable(Glimmer.AI.Json.Str(output["url"])) is not { } link)
            return ImageHTTP.Result(null, "Hedra");
        // The signed URL carries its own authorisation: no key is sent with it, and it is not written anywhere.
        var download = new RequestSpec(link, "GET") { Timeout = TimeSpan.FromSeconds(120) };
        return ImageHTTP.Result(await ImageHTTP.SendAsync(download, "Hedra", http, cancellationToken).ConfigureAwait(false), "Hedra");
    }

    /// <summary>Uploads a picture's bytes; returns the handle to put in the job's input.</summary>
    async Task<string> UploadAsync(byte[] png, CancellationToken ct)
    {
        var boundary = "glimmer-" + Guid.NewGuid().ToString("D").ToUpperInvariant();
        var request = new RequestSpec(Http.Append(baseUrl, "files"), "POST") { Timeout = TimeSpan.FromSeconds(120) };
        foreach (var (name, value) in Headers) request.SetHeader(name, value);
        request.ContentType = $"multipart/form-data; boundary={boundary}";
        using var body = new MemoryStream();
        void Write(string s) { var b = Encoding.UTF8.GetBytes(s); body.Write(b, 0, b.Length); }
        Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"input.png\"\r\nContent-Type: image/png\r\n\r\n");
        body.Write(png, 0, png.Length);
        Write($"\r\n--{boundary}--\r\n");
        request.Body = body.ToArray();
        var json = ImageHTTP.Json(await ImageHTTP.SendAsync(request, "Hedra", http, ct).ConfigureAwait(false), "Hedra");
        return Glimmer.AI.Json.Str(json["url"]) ?? throw new ProviderHttpError("Hedra", 200, "Hedra didn't return a handle for the uploaded picture.");
    }
}

public sealed partial class AIProvider
{
    /// <summary>
    /// The image model for this service. <paramref name="editModel"/> is fal's separate model id for requests
    /// with input pictures. The key is passed in for this one model and kept nowhere else.
    /// <c>handler</c> is the transport (tests pass a fake one).
    /// </summary>
    public IImageModel? ImageModel(string key, string model, string? editModel = null, HttpMessageHandler? handler = null)
    {
        if (!Built.Contains(Capability.Image)) return null;
        return Slug switch
        {
            "openai" => new OpenAIImageModel(key, model, handler: handler),
            "google" => new GoogleImageModel(key, model, handler: handler),
            "fal" => new FalImageModel(key, model, editModel, handler: handler),
            "hedra" => new HedraImageModel(key, model, handler: handler),
            _ => null,   // OpenRouter's has a result cache and two routes: `OpenRouterImageModel`
        };
    }
}
