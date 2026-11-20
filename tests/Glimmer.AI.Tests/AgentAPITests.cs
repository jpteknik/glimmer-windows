// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/AgentAPITests.swift
// (the operation list comes from a small fake catalog; the real one is the Agent port's)
using System.Text;
using System.Text.Json.Nodes;
using Glimmer.AI;

namespace Glimmer.AI.Tests;

public class AgentAPITests
{
    private static byte[] Raw(string s) => Encoding.UTF8.GetBytes(s.Replace("\n", "\r\n"));

    private static byte[] Raw(string s, byte[] body) => Raw(s).Concat(body).ToArray();

    [Fact]
    public void ParsesARequestWithBodyAndQuery()
    {
        var data = Raw("POST /api/v1/documents/abc/image?maxSide=512&format=png HTTP/1.1\nHost: 127.0.0.1:9001\nX-Glimmer-Agent: 1\nContent-Length: 5\n\n", Encoding.UTF8.GetBytes("hello"));
        var parsed = Assert.IsType<AgentAPI.ParseResult.Parsed>(AgentAPI.Parse(data));
        var r = parsed.Request;
        Assert.Equal("POST", r.Method);
        Assert.Equal(new[] { "api", "v1", "documents", "abc", "image" }, r.Segments);
        Assert.Equal(new Dictionary<string, string> { ["maxSide"] = "512", ["format"] = "png" }, r.Query);
        Assert.Equal("1", r.Headers["x-glimmer-agent"]);
        Assert.Equal(Encoding.UTF8.GetBytes("hello"), r.Body);
    }

    [Fact]
    public void WaitsForTheWholeBody()
    {
        var data = Raw("POST /x HTTP/1.1\nHost: localhost\nContent-Length: 10\n\n", Encoding.UTF8.GetBytes("12345"));
        Assert.IsType<AgentAPI.ParseResult.Incomplete>(AgentAPI.Parse(data));
        Assert.IsType<AgentAPI.ParseResult.Incomplete>(AgentAPI.Parse(Raw("GET / HTTP/1.1\nHost: local")));
    }

    [Fact]
    public void RefusesChunkedAndHugeBodiesAndOtherNonsense()
    {
        Assert.IsType<AgentAPI.ParseResult.Invalid>(AgentAPI.Parse(Raw("POST /x HTTP/1.1\nTransfer-Encoding: chunked\n\n")));
        Assert.IsType<AgentAPI.ParseResult.Invalid>(AgentAPI.Parse(Raw("POST /x HTTP/1.1\nContent-Length: 999999999999\n\n")));
        Assert.IsType<AgentAPI.ParseResult.Invalid>(AgentAPI.Parse(Raw("POST /x HTTP/1.1\nContent-Length: nope\n\n")));
        Assert.IsType<AgentAPI.ParseResult.Invalid>(AgentAPI.Parse(Raw("GET\n\n")));
        Assert.Equal("Headers too large.", Assert.IsType<AgentAPI.ParseResult.Invalid>(AgentAPI.Parse(new byte[70_000])).Message);
        var notUtf8 = Encoding.ASCII.GetBytes("GET /x HTTP/1.1\r\nX: ").Concat(new byte[] { 0xC3, 0x28 }).Concat(Encoding.ASCII.GetBytes("\r\n\r\n")).ToArray();
        Assert.Equal("Headers are not UTF-8.", Assert.IsType<AgentAPI.ParseResult.Invalid>(AgentAPI.Parse(notUtf8)).Message);
    }

    [Fact]
    public void ThePathAndQueryAreDecodedAndAnEmptyPathIsTheRoot()
    {
        var parsed = Assert.IsType<AgentAPI.ParseResult.Parsed>(AgentAPI.Parse(Raw("get /a%20b?name=Holiday%20photo&flag HTTP/1.1\nHost: x\n\n")));
        Assert.Equal("GET", parsed.Request.Method);
        Assert.Equal("/a b", parsed.Request.Path);
        Assert.Equal(new Dictionary<string, string> { ["name"] = "Holiday photo", ["flag"] = "" }, parsed.Request.Query);
    }

    private static AgentAPI.Request Request(string method, Dictionary<string, string> headers) => new(method, "/api/v1/status", headers: headers);

    [Fact]
    public void SecurityRules()
    {
        Assert.Null(AgentAPI.Check(Request("GET", new() { ["host"] = "127.0.0.1:9001" }), 9001));
        Assert.Null(AgentAPI.Check(Request("GET", new() { ["host"] = "localhost:9001" }), 9001));
        // DNS rebinding: a foreign Host.
        Assert.Equal(403, AgentAPI.Check(Request("GET", new() { ["host"] = "evil.example:9001" }), 9001)?.Status);
        // From a web page.
        Assert.Equal(403, AgentAPI.Check(Request("GET", new() { ["host"] = "127.0.0.1:9001", ["origin"] = "https://evil.example" }), 9001)?.Status);
        // State changes need the agent header.
        Assert.Contains("X-Glimmer-Agent", AgentAPI.Check(Request("POST", new() { ["host"] = "127.0.0.1:9001" }), 9001)!.Message);
        Assert.Null(AgentAPI.Check(Request("POST", new() { ["host"] = "127.0.0.1:9001", ["x-glimmer-agent"] = "1" }), 9001));
    }

    [Fact]
    public void ErrorsAreJsonWithCodeAndMessage()
    {
        var r = AgentAPI.Response.Error(AgentAPI.APIError.NotFound("No such document."));
        Assert.Equal(404, r.Status);
        var obj = JsonNode.Parse(r.Body)!;
        Assert.Equal("not_found", (string)obj["error"]!["code"]!);
        Assert.StartsWith("HTTP/1.1 404 Not Found", Encoding.UTF8.GetString(r.Serialized.Take(30).ToArray()));
        var text = Encoding.UTF8.GetString(r.Serialized);
        Assert.Contains("Content-Length: " + r.Body.Length, text);
        Assert.Contains("Cache-Control: no-store", text);
        Assert.EndsWith("\n", Encoding.UTF8.GetString(r.Body));
    }

    [Fact]
    public void UnprocessableCarriesAReason()
    {
        var r = AgentAPI.Response.Error(AgentAPI.APIError.Unprocessable("nothing_to_undo", "Nothing to undo."));
        Assert.Equal(422, r.Status);
        var obj = JsonNode.Parse(r.Body)!;
        Assert.Equal("unprocessable", (string)obj["error"]!["code"]!);
        Assert.Equal("nothing_to_undo", (string)obj["error"]!["reason"]!);
    }

    [Fact]
    public void AModelMissingErrorNamesTheRegisterEntriesAndTheStep()
    {
        var error = AgentAPI.APIError.ModelMissing(2, "select_sky", new[] { "depth-anything-v2-small", "sam2.1-tiny" });
        Assert.Equal(422, error.Status);
        // The register's names: the Windows entries are the ONNX conversions (the Mac's say "Core ML").
        Assert.Contains("Step 2 (select_sky) needs Depth Anything V2 Small (ONNX) and SAM 2.1 Tiny (ONNX, 8-bit), not downloaded on this computer.", error.Message);
        Assert.Contains("downloads them in Glimmer › Settings › Models (POST /api/v1/settings/open with {\"pane\": \"models\"} opens it)", error.Message);
        var body = JsonNode.Parse(AgentAPI.Response.Error(error).Body)!["error"]!;
        Assert.Equal("depth-anything-v2-small", (string)body["requirement"]!);
        Assert.Equal(2, body["requirements"]!.AsArray().Count);
        Assert.Equal(2, (int)body["step"]!);
    }

    [Fact]
    public void ARefusedStepIsStepFailedWithAReason()
    {
        // SIBLINGS.md: inside /steps a refusal is always step_failed; unprocessable is only for other calls.
        var refused = AgentAPI.APIError.StepFailedBody(1, "transform_layer", "Would rasterize.", "bad_corners");
        Assert.Equal("step_failed", (string)refused["code"]!);
        Assert.Equal("bad_corners", (string)refused["reason"]!);
        Assert.Equal(1, (int)refused["step"]!);
        Assert.Equal("Step 1 (transform_layer) failed: Would rasterize.", (string)refused["message"]!);
        var plain = AgentAPI.APIError.StepFailedBody(0, "crop", "No such layer.", null);
        Assert.True((string)plain["code"]! == "step_failed" && plain["reason"] is null);
        var guide = AgentAPI.Guide(9001, "1", new FakeCatalog());
        Assert.Contains("`error.reason` says which (`would_rasterize_text`", guide);
        Assert.Contains("`group_has_mask` is no longer returned", guide);
    }

    [Fact]
    public void OperationsAndGuideComeFromTheAssistantsList()
    {
        var catalog = new FakeCatalog();
        var ops = AgentAPI.Operations(catalog);
        Assert.Equal(catalog.Operations.Count, ops.Count(o => o["call"] is null));
        Assert.Contains(ops, o => (string?)o["name"] == "generate_layer" && (bool?)o["paid"] == true);
        Assert.Contains(ops, o => (string?)o["name"] == "auto_enhance" && (bool?)o["paid"] == false);
        var guide = AgentAPI.Guide(9007, "0.2.0", catalog);
        Assert.Contains("http://127.0.0.1:9007", guide);
        Assert.Contains("X-Glimmer-Agent: 1", guide);
        Assert.Contains("Glimmer 0.2.0 is a native Windows photo editor", guide);
        // Quick start comes before the endpoint reference.
        Assert.True(guide.IndexOf("## Quick start", StringComparison.Ordinal) < guide.IndexOf("## Endpoints", StringComparison.Ordinal));
        foreach (var op in catalog.Operations.Where(o => o.Name != "develop_raw" || AgentAPI.HasRAW).Where(o => o.Name != "heal_selection" || AgentAPI.HasHeal))
            Assert.Contains($"### `{op.Name}`", guide);
        // Tokens are all filled in, and the generated sections are all there.
        Assert.DoesNotContain("@@", guide);
        Assert.Contains("## What Glimmer may need installed", guide);
        Assert.Contains("## Operations", guide);
        Assert.Contains("## Good to know", guide);
        Assert.Contains("### `generate_layer` (paid)", guide);
        Assert.Contains("No params: `{\"op\": \"auto_enhance\"}`", guide);
        Assert.Contains("| `aspect` | string | Optional. The shape, like 16:9 \\| 4:3. |", guide);
        Assert.Contains("| `type` | string | Required. The kind. One of: `black_white`, `exposure` |", guide);
        Assert.Contains("| `layer.new` | New Layer |", guide);
        Assert.Contains("Step 0: crop has no parameter widht (did you mean width?). No step of this request was run.", guide);
    }

    [Fact]
    public void TheGuidesLineContinuationAndEscapesComeOutAsInTheOriginal()
    {
        var guide = AgentAPI.Guide(9001, "1", new FakeCatalog());
        // `\\` in the Swift literal is one backslash; a trailing `\` joined its line to the next.
        Assert.Contains("format=png\\|jpeg", guide);
        Assert.Contains("-H 'Content-Type: image/jpeg' \\\n     --data-binary @photo.jpg $BASE/api/v1/documents", guide);
        Assert.Contains("\"async\": true}'       $BASE/api/v1/documents/$ID/steps | python3 -c", guide);
        Assert.DoesNotContain("\\\\", guide);
    }

    [Fact]
    public void TheGuideDescribesOnlyTheFeaturesAskedFor()
    {
        var catalog = new FakeCatalog();
        var without = AgentAPI.Guide(9001, "1", catalog, heal: false, psd: false, raw: false);
        Assert.DoesNotContain("### `heal_selection`", without);
        Assert.DoesNotContain("### `develop_raw`", without);
        Assert.DoesNotContain("Photoshop file (PSD or PSB bytes)", without);
        Assert.Contains("Removing an object from a photo has no free operation", without);
#if GLIMMER_HEAL && GLIMMER_PSD && GLIMMER_RAW
        var with = AgentAPI.Guide(9001, "1", catalog, heal: true, psd: true, raw: true);
        Assert.Contains("### `heal_selection`", with);
        Assert.Contains("### `develop_raw`", with);
        Assert.Contains("Photoshop file (PSD or PSB bytes)", with);
        Assert.Contains("A camera's RAW file", with);
        Assert.Contains("`heal_selection` fills the area", with);
#endif
    }

    [Fact]
    public void SiblingHeaderIsShownByName()
    {
        static AgentAPI.Request Req(string? value) => new("POST", "/", headers: value is null ? new() : new Dictionary<string, string> { ["x-sibling"] = value });
        Assert.Null(AgentAPI.Sibling(Req(null)));
        Assert.Equal("Flimmer", AgentAPI.Sibling(Req("flimmer")));
        Assert.Equal("Jämmer", AgentAPI.Sibling(Req(" Jammer ")));
        Assert.Equal("Other-app", AgentAPI.Sibling(Req("other-app")));
        Assert.Equal("Script", AgentAPI.Sibling(Req("<script>")));
        Assert.Null(AgentAPI.Sibling(Req("!!!")));
        Assert.Equal(32, AgentAPI.Sibling(Req(new string('a', 100)))!.Length);
    }

    [Fact]
    public void PortsAndWordsForErrors()
    {
        Assert.Equal(9001, AgentAPI.PreferredPort);
        Assert.Equal(20, AgentAPI.CandidatePorts.Count);
        Assert.Equal(new ushort[] { 9001, 9101, 9102 }, AgentAPI.CandidatePorts.Take(3));
        Assert.Equal(9119, AgentAPI.CandidatePorts[^1]);
        // Glimmer's own errors are told as they are written; the system's only by type and code.
        Assert.Equal("Add your OpenRouter API key in Settings → AI.", AgentAPI.Words(OpenRouterError.MissingAPIKey()));
        var system = AgentAPI.Words(new IOException("Der Pfad wurde nicht gefunden.", 5));
        Assert.StartsWith("The system reported an error (IOException ", system);
        Assert.DoesNotContain("Pfad", system);
    }
}

public class SiblingProtocolTests
{
    private static readonly FakeCatalog Catalog = new();

    [Fact]
    public void OperationsHaveTheSeriesShape()
    {
        foreach (var op in AgentAPI.Operations(Catalog))
        {
            var name = (string)op["name"]!;
            Assert.NotNull((string?)op["description"]);
            Assert.True(op["paid"] is JsonValue && op["undoable"] is JsonValue && op["startsJob"] is JsonValue, name);
            // "Okänt pris": a paid operation says whether its price is known, and null when it isn't; a free one carries no price.
            if ((bool)op["paid"]!)
            {
                Assert.Equal(PaidPrice.KnownPrice(name, "openrouter") is not null, (bool)op["priceKnown"]!);
                Assert.True((bool)op["priceKnown"]! || (op.ContainsKey("price") && op["price"] is null), name);
            }
            else
            {
                Assert.False(op.ContainsKey("priceKnown") || op.ContainsKey("price"), name);
            }
            var parameters = op["params"]!.AsObject();
            Assert.Equal("object", (string)parameters["type"]!);
            Assert.True(parameters["properties"] is JsonObject && parameters["required"] is JsonArray);
            // Every operation offered as a tool accepts `rationale` and says so, and its schema is closed (SIBLINGS.md §3, 2026-11-16).
            Assert.True(parameters["properties"]!["rationale"] is not null && (bool)parameters["additionalProperties"]! == false, name);
            if (op["call"] is JsonObject call)
            {
                Assert.Contains((string)call["method"]!, new[] { "GET", "POST" });
                Assert.StartsWith("/api/v1/documents/{id}/", (string)call["path"]!);
                var id = parameters["properties"]!["id"]!;
                Assert.Equal("path", (string)id["x-in"]!);
                Assert.Contains("id", parameters["required"]!.AsArray().Select(r => (string)r!));
            }
        }
        Assert.Equal(new[] { "add_image", "export", "image", "redo", "undo" },
                     AgentAPI.Operations(Catalog).Where(o => o["call"] is not null).Select(o => (string)o["name"]!).OrderBy(n => n, StringComparer.Ordinal));
        // Every op has a tier; the core set is small and every core name exists.
        Assert.All(AgentAPI.Operations(Catalog), o => Assert.Contains((string)o["tier"]!, new[] { "core", "advanced" }));
        Assert.Equal(15, AgentAPI.CoreOps.Count);
        var core = AgentAPI.Operations(Catalog).Where(o => (string)o["tier"]! == "core").Select(o => (string)o["name"]!).ToHashSet();
        Assert.True(core.IsSubsetOf(AgentAPI.CoreOps));
        Assert.Contains("add_adjustment", core);
        // It's plain JSON, and paid steps are the ones that start jobs.
        var export = AgentAPI.Operations(Catalog).First(o => (string)o["name"]! == "export");
        var exportProps = export["params"]!["properties"]!;
        Assert.Equal("podcast-cover", (string)exportProps["preset"]!["enum"]![0]!);
        Assert.Equal(new[] { 3000, 1400 }, exportProps["size"]!["enum"]!.AsArray().Select(n => (int)n!));
        var image = AgentAPI.Operations(Catalog).First(o => (string)o["name"]! == "image");
        Assert.Equal("query", (string)image["params"]!["properties"]!["maxSide"]!["x-in"]!);
        Assert.Equal("query", (string)image["params"]!["properties"]!["rationale"]!["x-in"]!);   // a GET: `rationale` goes in the query too
        Assert.Null(export["params"]!["properties"]!["rationale"]!["x-in"]);
        var gen = AgentAPI.Operations(Catalog).First(o => (string)o["name"]! == "generate_layer");
        Assert.True((bool)gen["paid"]! && (bool)gen["startsJob"]!);
        var adj = AgentAPI.Operations(Catalog).First(o => (string)o["name"]! == "add_adjustment");
        Assert.True(!(bool)adj["paid"]! && !(bool)adj["startsJob"]!);
        Assert.IsType<JsonArray>(adj["params"]!["properties"]!["type"]!["enum"]);
        var addImage = AgentAPI.Operations(Catalog).First(o => (string)o["name"]! == "add_image");
        Assert.Equal(new[] { "contain", "cover", "none" }, addImage["params"]!["properties"]!["fit"]!["enum"]!.AsArray().Select(n => (string)n!));
        Assert.True((bool)addImage["undoable"]!);
        var undo = AgentAPI.Operations(Catalog).First(o => (string)o["name"]! == "undo");
        Assert.False((bool)undo["undoable"]!);
    }

    [Fact]
    public void TheOperationsDontShareNodesWithTheCatalog()
    {
        var first = AgentAPI.Operations(Catalog);
        var second = AgentAPI.Operations(Catalog);
        Assert.Equal(first.Count, second.Count);
        Assert.NotSame(first[0]["params"], second[0]["params"]);
        Assert.Equal(first[0].ToJsonString(), second[0].ToJsonString());
    }
}
