// Tests for the port of App/AgentServer.swift: the local agent API over a real loopback socket (port chosen by the
// system), its guards (Host, Origin, the agent header), documents, steps, jobs, undo and images.
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Glimmer.Core;

namespace Glimmer.Editor.Tests;

[Collection(SharedModelStoreCollection.Name)]
public sealed class AgentServerTests : IDisposable
{
    private readonly Harness h = new();
    private readonly SharedModelStore models;
    private readonly DocumentOpener opener;
    private readonly AgentServer server;
    private readonly HttpClient client = new();

    public AgentServerTests()
    {
        models = new SharedModelStore(h);
        opener = new DocumentOpener(h.Environment);
        server = new AgentServer(opener, [0], appVersion: "1.0-test");
        server.Start();
        client.BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/");
    }

    public void Dispose()
    {
        server.Dispose();
        client.Dispose();
        models.Dispose();
        h.Dispose();
    }

    private (HttpStatusCode Status, JsonObject? Body, HttpResponseMessage Response) Send(HttpMethod method, string path, string? json = null,
                                                                                         bool agentHeader = true, Action<HttpRequestMessage>? change = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (agentHeader) request.Headers.Add("X-Glimmer-Agent", "1");
        change?.Invoke(request);
        var response = h.Scheduler.Await(client.SendAsync(request));
        var text = h.Scheduler.Await(response.Content.ReadAsStringAsync());
        JsonObject? body = null;
        try { body = JsonNode.Parse(text) as JsonObject; }
        catch (Exception) { }
        return (response.StatusCode, body, response);
    }

    private string NewDocument(int width = 64, int height = 48)
    {
        var (status, body, _) = Send(HttpMethod.Post, "api/v1/documents", $$"""{"width": {{width}}, "height": {{height}}, "name": "Test"}""");
        Assert.Equal(HttpStatusCode.Created, status);
        return body!["document"]!["id"]!.GetValue<string>();
    }

    [Fact]
    public void TheServerListensOnLoopbackAndSaysWhereTheGuideIs()
    {
        Assert.NotNull(server.Port);
        Assert.Null(server.Failure);
        Assert.Equal($"Learn all about Glimmer and how to interact with it: fetch and follow this guide http://127.0.0.1:{server.Port}/glimmer",
                     server.AgentPrompt);
        var (status, body, _) = Send(HttpMethod.Get, "api/v1/status", agentHeader: false);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Glimmer", body!["app"]!["name"]!.GetValue<string>());
        Assert.Equal("1.0-test", body["app"]!["version"]!.GetValue<string>());
        Assert.Equal((int)server.Port!, body["port"]!.GetValue<int>());
        Assert.Equal(Environment.ProcessId, body["pid"]!.GetValue<int>());
        var guide = h.Scheduler.Await(client.GetStringAsync("glimmer"));
        Assert.Contains($"127.0.0.1:{server.Port}", guide);
    }

    [Fact]
    public void RequestsFromElsewhereOrWithoutTheHeaderAreRefused()
    {
        // DNS rebinding: a Host that isn't this machine.
        var (status, body, _) = Send(HttpMethod.Get, "api/v1/status", agentHeader: false, change: r => r.Headers.Host = "evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("forbidden", body!["error"]!["code"]!.GetValue<string>());
        // A web page.
        (status, _, _) = Send(HttpMethod.Get, "api/v1/status", agentHeader: false, change: r => r.Headers.Add("Origin", "https://example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        // A POST without the agent header.
        (status, _, _) = Send(HttpMethod.Post, "api/v1/documents", """{"width": 10, "height": 10}""", agentHeader: false);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(opener.Sessions);
    }

    [Fact]
    public void AnotherServerOnATakenPortSaysSo()
    {
        using var second = new AgentServer(opener, [server.Port!.Value]);
        second.Start();
        Assert.Null(second.Port);
        Assert.Equal($"Ports {server.Port} are all in use.", second.Failure);
    }

    [Fact]
    public void ADocumentIsMadeEditedUndoneAndRead()
    {
        var id = NewDocument();
        var session = Assert.Single(opener.Sessions);
        Assert.True(session.MadeByAPI);
        Assert.Equal("Test", session.ExplicitName);
        var (status, body, _) = Send(HttpMethod.Post, $"api/v1/documents/{id}/steps",
                                     """{"steps": [{"op": "add_adjustment", "params": {"type": "vibrance"}}, {"op": "add_text", "params": {"text": "Hi"}}]}""");
        Assert.Equal(HttpStatusCode.OK, status);
        var results = body!["results"]!.AsArray();
        Assert.Equal(["done", "done"], results.Select(r => r!["status"]!.GetValue<string>()));
        Assert.NotNull(results[1]!["info"]!["layer"]);
        var layers = body["document"]!["layers"]!.AsArray();
        Assert.Equal("text", layers[0]!["kind"]!.GetValue<string>());
        Assert.Equal("Hi", layers[0]!["text"]!.GetValue<string>());
        // The whole request is one undo step.
        (status, body, _) = Send(HttpMethod.Post, $"api/v1/documents/{id}/undo");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Agent Edit", body!["undone"]!.GetValue<string>());
        Assert.Equal(1, body["document"]!["layerCount"]!.GetValue<int>());
        (status, body, _) = Send(HttpMethod.Post, $"api/v1/documents/{id}/undo");
        Assert.Equal(422, (int)status);
        Assert.Equal("nothing_to_undo", body!["error"]!["reason"]?.GetValue<string>() ?? body["error"]!["code"]!.GetValue<string>());
        // The picture, as PNG.
        var image = h.Scheduler.Await(client.GetByteArrayAsync($"api/v1/documents/{id}/image"));
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], image.Take(4));
    }

    [Fact]
    public void StepsAreCheckedBeforeAnyRuns()
    {
        var id = NewDocument();
        var (status, body, _) = Send(HttpMethod.Post, $"api/v1/documents/{id}/steps", """{"steps": [{"op": "juggle"}]}""");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("unknown op \"juggle\"", body!["error"]!["message"]!.GetValue<string>());
        (status, body, _) = Send(HttpMethod.Post, $"api/v1/documents/{id}/steps",
                                 """{"steps": [{"op": "add_adjustment", "params": {"type": "vibrance"}}, {"op": "crop", "params": {"widht": 10}}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("widht", body!["error"]!["message"]!.GetValue<string>());
        // A paid step needs allowPaid.
        (status, body, _) = Send(HttpMethod.Post, $"api/v1/documents/{id}/steps", """{"steps": [{"op": "generate_layer", "params": {"prompt": "a cat"}}]}""");
        Assert.Equal(HttpStatusCode.PaymentRequired, status);
        Assert.Equal("paid_not_allowed", body!["error"]!["code"]!.GetValue<string>());
        // A step that needs a model that isn't here: refused, never downloaded.
        (status, body, _) = Send(HttpMethod.Post, $"api/v1/documents/{id}/steps", """{"steps": [{"op": "select_sky"}]}""");
        Assert.Equal(422, (int)status);
        Assert.Equal("model_missing", body!["error"]!["code"]!.GetValue<string>());
        // Nothing ran.
        Assert.Equal(1, Assert.Single(opener.Sessions).State.AllLayers.Length);
    }

    [Fact]
    public void AsyncStepsRunAsAJob()
    {
        var id = NewDocument();
        var (status, body, _) = Send(HttpMethod.Post, $"api/v1/documents/{id}/steps",
                                     """{"async": true, "steps": [{"op": "add_adjustment", "params": {"type": "vibrance"}}]}""");
        Assert.Equal(HttpStatusCode.Accepted, status);
        var job = body!["job"]!["id"]!.GetValue<string>();
        JsonObject? read = null;
        h.Scheduler.RunUntil(() =>
        {
            read = Send(HttpMethod.Get, $"api/v1/jobs/{job}", agentHeader: false).Body;
            return read?["job"]?["status"]?.GetValue<string>() is "done" or "failed";
        });
        Assert.Equal("done", read!["job"]!["status"]!.GetValue<string>());
        Assert.Equal(1.0, read["job"]!["progress"]!.GetValue<double>());
        Assert.Equal("done", read["job"]!["result"]!["results"]![0]!["status"]!.GetValue<string>());
        (status, _, _) = Send(HttpMethod.Get, "api/v1/jobs/NOPE", agentHeader: false);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public void UnknownPathsMethodsAndQueriesAreNamed()
    {
        var (status, _, _) = Send(HttpMethod.Get, "api/v1/nothing", agentHeader: false);
        Assert.Equal(HttpStatusCode.NotFound, status);
        (status, _, _) = Send(HttpMethod.Delete, "api/v1/documents");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, status);
        var (s2, body, _) = Send(HttpMethod.Get, "api/v1/documents?colour=red", agentHeader: false);
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Contains("colour", body!["error"]!["message"]!.GetValue<string>());
        var ops = Send(HttpMethod.Get, "api/v1/operations", agentHeader: false).Body!;
        Assert.Contains(ops["operations"]!.AsArray(), o => o!["name"]?.GetValue<string>() == "add_adjustment" || o!["op"]?.GetValue<string>() == "add_adjustment");
    }

    [Fact]
    public void AnImageBecomesADocumentOrALayer()
    {
        using var bitmap = new SkiaSharp.SKBitmap(30, 20);
        bitmap.Erase(SkiaSharp.SKColors.Blue);
        using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray();
        var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/documents?name=Blue") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        request.Headers.Add("X-Glimmer-Agent", "1");
        var response = h.Scheduler.Await(client.SendAsync(request));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = JsonNode.Parse(h.Scheduler.Await(response.Content.ReadAsStringAsync()))!["document"]!["id"]!.GetValue<string>();
        var add = new HttpRequestMessage(HttpMethod.Post, $"api/v1/documents/{id}/layers?fit=none&smartObject=false") { Content = new ByteArrayContent(bytes) };
        add.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        add.Headers.Add("X-Glimmer-Agent", "1");
        response = h.Scheduler.Await(client.SendAsync(add));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonNode.Parse(h.Scheduler.Await(response.Content.ReadAsStringAsync()))!;
        Assert.Equal(2, body["document"]!["layerCount"]!.GetValue<int>());
        Assert.Equal(30, body["layer"]!["bounds"]!["width"]!.GetValue<int>());
    }
}
