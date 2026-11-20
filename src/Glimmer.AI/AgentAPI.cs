// Ported from Packages/GlimmerKit/Sources/GlimmerAI/AgentAPI.swift
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Glimmer.AI;

/// One parameter of an operation, as the agent guide's tables list it.
public sealed record AgentOperationParam(string Name, string Type, string Description, IReadOnlyList<string>? Options = null);

/// One operation of the Assistant's list (Swift: an element of `AgentPlanner.ops`): what the
/// agent API offers as a `steps` operation. The planner is ported with the Agent; until then
/// anything that implements `IAgentOperationCatalog` can feed the API and the guide.
public sealed record AgentOperation(string Name, string Description, JsonObject ParamsSchema, IReadOnlyList<AgentOperationParam> Params,
                                    bool IsAIJob = false, bool StartsJob = false);

/// A command an agent may run with `run_command` (Swift: `AgentPlanner.runnableCommands`).
public sealed record RunnableCommand(string Id, string Title);

/// What AgentAPI needs from the Assistant's side: the operations and a few facts that live in
/// `AgentPlanner`, `StepParameters` and `CanvasSteps` in the original. The Agent port implements it.
public interface IAgentOperationCatalog
{
    /// `AgentPlanner.ops`.
    IReadOnlyList<AgentOperation> Operations { get; }
    /// `AgentPlanner.runnableCommands`.
    IReadOnlyList<RunnableCommand> RunnableCommands { get; }
    /// `StepParameters.rationaleSchema`.
    JsonObject RationaleSchema { get; }
    /// `CanvasSteps.Fit.allCases` raw values.
    IReadOnlyList<string> FitValues { get; }
    /// What `StepParameters.stray(in:)` says for `crop` with `widht` (the guide's example of a refused name).
    string StrayExample { get; }
    /// The same for `add_adjustment` with `brightness` inside `params`.
    string NestedStrayExample { get; }
}

/// The local HTTP API that lets other agents drive Glimmer: request parsing,
/// the security rules, the error format, the operation list and the agent
/// guide. Pure, so it's tested without a socket; the app owns the listener.
public static class AgentAPI
{
    public const string Version = "v1";
    /// Header every state-changing request must carry (a web page can't add it
    /// without a CORS preflight, which the server never grants).
    public const string AgentHeader = "X-Glimmer-Agent";
    public const ushort PreferredPort = 9001;
    /// 9001 first, then Glimmer's own fallback range (Flimmer uses 9002 and 9201–9219,
    /// Jämmer 9003 and 9301–9319), so which app gets which port never depends on launch order.
    public static readonly IReadOnlyList<ushort> CandidatePorts =
        new ushort[] { 9001 }.Concat(Enumerable.Range(9101, 19).Select(p => (ushort)p)).ToArray();

    /// Steps that call OpenRouter and cost money. Everything else runs on this machine.
    public static ISet<string> PaidOps(IAgentOperationCatalog catalog) =>
        new HashSet<string>(catalog.Operations.Where(o => o.IsAIJob).Select(o => o.Name));

    // MARK: Siblings

    /// Header a sibling app (Flimmer, Jämmer) adds with its slug. It grants nothing;
    /// Glimmer logs it and shows who asked.
    public const string SiblingHeader = "X-Sibling";
    internal static readonly IReadOnlyDictionary<string, string> SiblingNames =
        new Dictionary<string, string> { ["glimmer"] = "Glimmer", ["flimmer"] = "Flimmer", ["jammer"] = "Jämmer" };

    /// The app named by `X-Sibling`, for display: the known siblings by name, anything
    /// else reduced to at most 32 letters, digits and hyphens. Null without the header.
    public static string? Sibling(Request r)
    {
        if (!r.Headers.TryGetValue(SiblingHeader.ToLowerInvariant(), out var raw)) return null;
        var kept = new StringBuilder();
        foreach (var c in raw.Trim(' ', '\t').ToLowerInvariant())
        {
            if (c < 128 && (char.IsLetter(c) || char.IsDigit(c) || c == '-')) kept.Append(c);
        }
        var slug = kept.Length > 32 ? kept.ToString(0, 32) : kept.ToString();
        if (slug.Length == 0) return null;
        return SiblingNames.TryGetValue(slug, out var name) ? name : char.ToUpperInvariant(slug[0]) + slug[1..];
    }

    // MARK: HTTP

    public sealed class Request : IEquatable<Request>
    {
        public string Method { get; set; }
        public string Path { get; set; }
        public IReadOnlyDictionary<string, string> Query { get; set; }
        /// Header names lowercased.
        public IReadOnlyDictionary<string, string> Headers { get; set; }
        public byte[] Body { get; set; }

        public Request(string method, string path, IReadOnlyDictionary<string, string>? query = null,
                       IReadOnlyDictionary<string, string>? headers = null, byte[]? body = null)
        {
            Method = method;
            Path = path;
            Query = query ?? new Dictionary<string, string>();
            Headers = headers ?? new Dictionary<string, string>();
            Body = body ?? Array.Empty<byte>();
        }

        /// Path components after the leading slash ("/api/v1/documents/x" → ["api","v1","documents","x"]).
        public IReadOnlyList<string> Segments => Path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        public bool Equals(Request? other) =>
            other is not null && Method == other.Method && Path == other.Path
            && Query.Count == other.Query.Count && Query.All(p => other.Query.TryGetValue(p.Key, out var v) && v == p.Value)
            && Headers.Count == other.Headers.Count && Headers.All(p => other.Headers.TryGetValue(p.Key, out var v) && v == p.Value)
            && Body.AsSpan().SequenceEqual(other.Body);

        public override bool Equals(object? obj) => Equals(obj as Request);
        public override int GetHashCode() => HashCode.Combine(Method, Path, Body.Length);
    }

    public abstract record ParseResult
    {
        private ParseResult() { }

        /// More bytes are needed.
        public sealed record Incomplete : ParseResult;
        public sealed record Parsed(Request Request) : ParseResult;
        public sealed record Invalid(string Message) : ParseResult;
    }

    /// Largest accepted body (an uncompressed 8K image fits).
    public const int MaxBody = 256 * 1024 * 1024;

    private static readonly byte[] HeaderEnd = { 13, 10, 13, 10 };

    /// Parses one HTTP/1.1 request from the bytes received so far.
    public static ParseResult Parse(byte[] data)
    {
        var headerEnd = data.AsSpan().IndexOf(HeaderEnd);
        if (headerEnd < 0)
            return data.Length > 64 * 1024 ? new ParseResult.Invalid("Headers too large.") : new ParseResult.Incomplete();
        string head;
        try { head = new UTF8Encoding(false, true).GetString(data, 0, headerEnd); }
        catch (DecoderFallbackException) { return new ParseResult.Invalid("Headers are not UTF-8."); }
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2) return new ParseResult.Invalid("Malformed request line.");
        var headers = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            headers[line[..colon].Trim(' ', '\t').ToLowerInvariant()] = line[(colon + 1)..].Trim(' ', '\t');
        }
        long length = long.TryParse(headers.GetValueOrDefault("content-length", "0"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
        if (length < 0) return new ParseResult.Invalid("Bad Content-Length.");
        if (length > MaxBody) return new ParseResult.Invalid("Body too large.");
        if (headers.TryGetValue("transfer-encoding", out var encoding) && encoding.ToLowerInvariant().Contains("chunked"))
            return new ParseResult.Invalid("Chunked bodies aren't supported; send Content-Length.");
        var bodyStart = headerEnd + HeaderEnd.Length;
        if (data.Length - bodyStart < length) return new ParseResult.Incomplete();
        var body = data.AsSpan(bodyStart, (int)length).ToArray();
        var target = requestLine[1];
        var parts = SwiftSplit(target, '?', 1);
        var query = new Dictionary<string, string>();
        if (parts.Count > 1)
        {
            foreach (var pair in SwiftSplit(parts[1], '&'))
            {
                var kv = SwiftSplit(pair, '=', 1).Select(Unescape).ToList();
                if (kv.Count == 0) continue;
                query[kv[0]] = kv.Count > 1 ? kv[1] : "";
            }
        }
        var path = Unescape(parts.Count > 0 ? parts[0] : "/");
        return new ParseResult.Parsed(new Request(requestLine[0].ToUpperInvariant(), path.Length == 0 ? "/" : path, query, headers, body));
    }

    /// `removingPercentEncoding ?? original`.
    private static string Unescape(string text)
    {
        try { return Uri.UnescapeDataString(text); } catch (UriFormatException) { return text; }
    }

    /// Swift's `split(separator:maxSplits:)`: empty pieces are left out, and after `maxSplits` splits the rest is one piece.
    internal static List<string> SwiftSplit(string text, char separator, int maxSplits = int.MaxValue)
    {
        var result = new List<string>();
        var start = 0;
        var splits = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != separator || splits >= maxSplits) continue;
            if (i > start)
            {
                result.Add(text[start..i]);
                splits++;
            }
            start = i + 1;
        }
        if (start < text.Length) result.Add(text[start..]);
        return result;
    }

    public sealed class Response
    {
        public int Status { get; set; }
        public string ContentType { get; set; }
        public byte[] Body { get; set; }
        public IReadOnlyDictionary<string, string> ExtraHeaders { get; set; }

        public Response(int status, string contentType, byte[] body, IReadOnlyDictionary<string, string>? extraHeaders = null)
        {
            Status = status;
            ContentType = contentType;
            Body = body;
            ExtraHeaders = extraHeaders ?? new Dictionary<string, string>();
        }

        public static Response FromJson(JsonNode obj, int status = 200)
        {
            var data = Encoding.UTF8.GetBytes(Glimmer.AI.Json.SerializeSortedIndented(obj) + "\n");
            return new Response(status, "application/json; charset=utf-8", data);
        }

        public static Response Text(string text, string contentType = "text/markdown; charset=utf-8", int status = 200) =>
            new(status, contentType, Encoding.UTF8.GetBytes(text));

        public static Response Error(APIError error)
        {
            var body = new JsonObject { ["code"] = error.Code, ["message"] = error.Message };
            if (error.Details is { } details)
            {
                var d = new JsonObject();
                foreach (var pair in details) d[pair.Key] = pair.Value;
                body["details"] = d;
            }
            if (error.Reason is { } reason) body["reason"] = reason;
            if (error.Requirements is { Count: > 0 } ids)
            {
                body["requirement"] = ids[0];
                body["requirements"] = new JsonArray(ids.Select(i => (JsonNode?)i).ToArray());
            }
            if (error.Step is { } step) body["step"] = step;
            return FromJson(new JsonObject { ["error"] = body }, error.Status);
        }

        /// The bytes to send (one request per connection).
        public byte[] Serialized
        {
            get
            {
                var head = $"HTTP/1.1 {Status} {ReasonPhrase(Status)}\r\n";
                head += $"Content-Type: {ContentType}\r\nContent-Length: {Body.Length}\r\nConnection: close\r\n";
                head += "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n";
                foreach (var pair in ExtraHeaders.OrderBy(p => p.Key, StringComparer.Ordinal)) head += $"{pair.Key}: {pair.Value}\r\n";
                return Encoding.UTF8.GetBytes(head + "\r\n").Concat(Body).ToArray();
            }
        }

        internal static string ReasonPhrase(int status) => status switch
        {
            200 => "OK", 201 => "Created", 400 => "Bad Request", 402 => "Payment Required", 403 => "Forbidden",
            404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict", 413 => "Payload Too Large",
            422 => "Unprocessable Content", 500 => "Internal Server Error", 503 => "Service Unavailable",
            _ => "Status",
        };
    }

    public sealed class APIError : Exception
    {
        public int Status { get; set; }
        public string Code { get; set; }
        private readonly string message;
        public IReadOnlyDictionary<string, string>? Details { get; set; }
        /// For `unprocessable`: which precondition wasn't met, as a short machine-readable word.
        public string? Reason { get; set; }
        /// For `model_missing`: the register ids of what is missing (the first is `requirement`), and the step.
        public IReadOnlyList<string>? Requirements { get; set; }
        public int? Step { get; set; }

        public APIError(int status, string code, string message, IReadOnlyDictionary<string, string>? details = null, string? reason = null)
            : base(message)
        {
            Status = status;
            Code = code;
            this.message = message;
            Details = details;
            Reason = reason;
        }

        public override string Message => message;

        /// 422 for a request (not a step batch) whose precondition isn't met, such as nothing to undo.
        public static APIError Unprocessable(string reason, string m) => new(422, "unprocessable", m, reason: reason);

        /// The error body of a failed step in a step batch (or job). Always `step_failed`, as in
        /// SIBLINGS.md; a step that refused for a known reason adds `reason`
        /// (`would_rasterize_text`, `bad_corners`…).
        public static JsonObject StepFailedBody(int step, string op, string message, string? reason)
        {
            var e = new JsonObject { ["code"] = "step_failed", ["step"] = step, ["message"] = $"Step {step} ({op}) failed: {message}" };
            if (reason is not null) e["reason"] = reason;
            return e;
        }

        public static APIError BadRequest(string m) => new(400, "bad_request", m);
        public static APIError NotFound(string m) => new(404, "not_found", m);
        public static APIError Forbidden(string m) => new(403, "forbidden", m);
        public static APIError NotAvailable(string m) => new(409, "not_available", m);
        public static APIError InternalError(string m) => new(500, "internal", m);

        /// 422 `model_missing`: step `step` needs these register entries, which aren't on this machine.
        public static APIError ModelMissing(int step, string op, IReadOnlyList<string> ids)
        {
            var names = string.Join(" and ", ids.Select(id => Glimmer.AI.Requirement.Named(id)?.Name).Where(n => n is not null));
            var them = ids.Count == 1 ? "it" : "them";
            return new APIError(422, "model_missing", $"Step {step} ({op}) needs {names}, not downloaded on this computer. "
                + $"The person downloads {them} in Glimmer › Settings › Models (POST /api/v1/settings/open with {{\"pane\": \"models\"}} opens it); "
                + "this API never starts a download. GET /api/v1/requirements has the source, license and size. No step of this request was run.")
            {
                Requirements = ids,
                Step = step,
            };
        }
    }

    /// The security rules, checked before routing:
    /// - the Host must be this machine (stops DNS rebinding),
    /// - requests from web pages (an Origin header) are refused,
    /// - anything that changes state needs the agent header.
    public static APIError? Check(Request request, ushort port)
    {
        var host = request.Headers.GetValueOrDefault("host", "").ToLowerInvariant();
        var allowed = new[] { "127.0.0.1", "localhost", "[::1]" }.SelectMany(h => new[] { $"{h}:{port}", h });
        if (!allowed.Contains(host))
            return APIError.Forbidden($"Host must be 127.0.0.1:{port} or localhost:{port}.");
        if (request.Headers.ContainsKey("origin"))
            return APIError.Forbidden("Requests from web pages are not accepted. Call the API from a local process (curl, a script, an agent).");
        if (request.Method != "GET" && request.Method != "HEAD"
            && request.Headers.GetValueOrDefault(AgentHeader.ToLowerInvariant()) != "1")
            return APIError.Forbidden($"Add the header {AgentHeader}: 1 to every POST request.");
        return null;
    }

    /// An error in the words an agent is told: English whatever language the app shows.
    /// Glimmer's own errors are written in English and translated only where the window shows them.
    /// The system's errors come in the machine's language, so of those only the type and code are passed on.
    public static string Words(Exception error)
    {
        var type = error.GetType();
        var own = type.Namespace?.StartsWith("Glimmer", StringComparison.Ordinal) == true;
        if (own) return error.Message;
        var system = error is IOException or System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException
            or UnauthorizedAccessException or TimeoutException or System.ComponentModel.Win32Exception;
        if (system) return $"The system reported an error ({type.Name} {error.HResult}).";
        return $"{type.Name}: {error.Message}";
    }

    // MARK: Operations

    /// The operations `POST /steps` accepts, from the same list the built-in
    /// assistant uses, so the API, the guide and the executor can't drift apart.
    /// The shape is the series' sibling protocol: `params` is a JSON Schema, and every
    /// step is undoable (a whole `steps` request is one undo step).
    /// Requests that aren't steps carry `call` (method and path; `{id}` is the document).
    public static IReadOnlyList<JsonObject> Operations(IAgentOperationCatalog catalog)
    {
        var paid = PaidOps(catalog);
        var all = new List<JsonObject>();
        foreach (var op in catalog.Operations)
        {
            var entry = new JsonObject
            {
                ["name"] = op.Name,
                ["description"] = op.Description,
                ["params"] = op.ParamsSchema.DeepClone(),
                ["paid"] = paid.Contains(op.Name),
                ["undoable"] = true,
                ["startsJob"] = op.StartsJob,
            };
            // A paid operation says what it costs, or that Glimmer doesn't know (then the call needs `allowUnknownPrice`).
            if (paid.Contains(op.Name))
            {
                entry["price"] = null;
                entry["priceKnown"] = false;
            }
            all.Add(entry);
        }
        all.AddRange(EndpointOperations(catalog));
        foreach (var op in all) op["tier"] = CoreOps.Contains(Glimmer.AI.Json.Str(op["name"]) ?? "") ? "core" : "advanced";
        return all;
    }

    /// The operations a sibling's assistant takes first when it can offer only a few tools
    /// (SIBLINGS.md §3): the everyday edits, a cover's parts, selection and export.
    public static readonly ISet<string> CoreOps = new HashSet<string>
    {
        "add_adjustment", "add_text", "add_waveform", "auto_enhance",
        "select_subject", "remove_background", "generate_layer", "image_edit_composite", "export", "image", "undo",
        "crop", "resize_image", "set_layer", "add_image",
    };

    internal static readonly string[] Formats = { "png", "jpeg", "heic", "tiff", "webp" };

    private static JsonObject WithKey(JsonObject schema, string key, JsonNode value)
    {
        var copy = (JsonObject)schema.DeepClone();
        copy[key] = value;
        return copy;
    }

    /// The requests besides `steps` that a sibling's assistant may call as tools. Params
    /// marked `"x-in": "path"` or `"query"` go there; the rest go in the JSON body.
    internal static IReadOnlyList<JsonObject> EndpointOperations(IAgentOperationCatalog catalog)
    {
        JsonObject Op(string name, string description, string method, string path, JsonObject? given = null, bool undoable = false)
        {
            var properties = given ?? new JsonObject();
            properties["id"] = new JsonObject { ["type"] = "string", ["x-in"] = "path", ["description"] = "The document id (GET /api/v1/documents)." };
            // `rationale` goes where the operation's other values go: in the query when the body is not JSON.
            var inQuery = method == "GET" || properties.Any(p => Glimmer.AI.Json.Str(Glimmer.AI.Json.Obj(p.Value)?["x-in"]) == "query");
            properties["rationale"] = inQuery ? WithKey(catalog.RationaleSchema, "x-in", "query") : catalog.RationaleSchema.DeepClone();
            return new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["params"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = new JsonArray("id"),
                    ["additionalProperties"] = false,
                },
                ["paid"] = false,
                ["undoable"] = undoable,
                ["startsJob"] = false,
                ["call"] = new JsonObject { ["method"] = method, ["path"] = $"/api/{Version}/documents/{{id}}/{path}" },
            };
        }

        var format = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(Formats.Select(f => (JsonNode?)f).ToArray()),
            ["description"] = "Optional. Default png.",
        };
        var maxSide = new JsonObject { ["type"] = "integer", ["minimum"] = 16, ["description"] = "Optional. Scale so the longer side fits, in pixels." };
        return new[]
        {
            Op("export", "Render the document to an image file and return its bytes (JSON body). With preset podcast-cover: the center square at exactly 3000 or 1400 pixels, JPEG or PNG, under maxBytes.", "POST", "export", new JsonObject
            {
                ["preset"] = new JsonObject
                {
                    ["type"] = "string", ["enum"] = new JsonArray("podcast-cover"),
                    ["description"] = "Optional. podcast-cover: square cover art in podcast sizes (format jpeg by default, or png).",
                },
                ["size"] = new JsonObject
                {
                    ["type"] = "integer", ["enum"] = new JsonArray(3000, 1400),
                    ["description"] = "Optional, with preset. Side in pixels. Default 3000, or 1400 when 3000 can't meet maxBytes.",
                },
                ["format"] = format.DeepClone(),
                ["quality"] = new JsonObject { ["type"] = "number", ["minimum"] = 0.05, ["maximum"] = 1, ["description"] = "Optional. JPEG, HEIC and WebP quality." },
                ["maxSide"] = maxSide.DeepClone(),
                ["maxBytes"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1000, ["description"] = "Optional. Largest file size in bytes (JPEG, HEIC and WebP)." },
            }),
            Op("image", "The flattened image as bytes.", "GET", "image", new JsonObject
            {
                ["format"] = WithKey(format, "x-in", "query"),
                ["maxSide"] = WithKey(maxSide, "x-in", "query"),
            }),
            Op("add_image", "Add an image as a new layer in an open document, to combine two images. Two ways to give the image, both open to any caller: send its bytes as the request body (PNG, JPEG, HEIC, TIFF or WebP; Content-Type image/… or application/octet-stream), or, when you have no bytes to send (an assistant calling this as a tool), give fromDocument: another open document, whose picture becomes the layer. The other options go in the query. The new layer lands above the active layer and becomes the active one. One undo step. Answers 201 with {layer: {id, name, bounds}}.", "POST", "layers", new JsonObject
            {
                ["fromDocument"] = new JsonObject { ["type"] = "string", ["description"] = "The id of another open document; its flattened picture becomes the new layer. Leave out when the body is image bytes." },
                ["name"] = new JsonObject { ["type"] = "string", ["x-in"] = "query", ["description"] = "Optional. The layer's name (default: the source document's name, or Image)." },
                ["fit"] = new JsonObject
                {
                    ["type"] = "string", ["enum"] = new JsonArray(catalog.FitValues.Select(f => (JsonNode?)f).ToArray()), ["x-in"] = "query",
                    ["description"] = "Optional. contain (default): scaled down to fit the canvas, never up. cover: scaled to cover the whole canvas. none: at its own size.",
                },
                ["x"] = new JsonObject { ["type"] = "number", ["x-in"] = "query", ["description"] = "Optional. Left edge in document pixels (default: centered)." },
                ["y"] = new JsonObject { ["type"] = "number", ["x-in"] = "query", ["description"] = "Optional. Top edge in document pixels (default: centered)." },
                ["smartObject"] = new JsonObject
                {
                    ["type"] = "boolean", ["x-in"] = "query",
                    ["description"] = "Optional. true (default) keeps the image's own pixels, so later scaling doesn't soften it; false makes a plain pixel layer.",
                },
            }, undoable: true),
            Op("undo", "Undo the last change (a whole steps request is one).", "POST", "undo"),
            Op("redo", "Redo the last undone change.", "POST", "redo"),
        };
    }

    // MARK: Guide

    /// What the guide says about taking an object out of a photo; depends on whether this
    /// build has Heal Selection (Directory.Build.props: GlimmerHeal).
    /// A build without the feature has no text about it at all, whatever `heal` says.
    internal static string ObjectRemovalNote(bool heal)
    {
        const string patch = "Free steps can cut out a subject (`remove_background`), cover an area with a color (`fill`) or patch it with a copy of another part (`select_rect` with a `feather`, then `run_command` `layer.viaCopy`, which copies the selected, feathered part of the active layer to a new layer, then `transform_layer` with `moveX`/`moveY` to put the copy over the object).";
#if GLIMMER_HEAL
        if (heal)
            return "To take something out of a photo: select it (`select_rect`, or `select_subject` when it is the main subject), then `heal_selection` fills the area from what surrounds it, on this computer and free. It suits dust, scratches, wires and things up to about 120 px across on grass, sky, walls and the like; it copies existing image, it does not invent, so a large object or one in front of a complicated scene needs `inpaint_selection` (paid). An area over about 120 px across is refused with `area_too_large` unless `allowLarge` is true, and over 250 px or too much work always (`hard` is then true). " + patch;
#endif
        return "Removing an object from a photo has no free operation: `inpaint_selection` (paid) is the only one. " + patch;
    }

    /// Whether this build opens Photoshop files with their layers (Directory.Build.props: GlimmerPsd).
    public static bool HasPSD =>
#if GLIMMER_PSD
        true;
#else
        false;
#endif

    /// The guide's line about Photoshop files; a build without the import has no text about it.
    internal static string PhotoshopNote(bool psd)
    {
#if GLIMMER_PSD
        if (psd)
            return "- A Photoshop file (PSD or PSB bytes) sent to `POST /api/v1/documents` opens with its layers. The reply's `document.importReport` says what came through: `layersInFile`, `layersKept`, `flattened` (true when only the file's own flattened picture could be used), `notes` (one per thing that did not come through: `layer`, `what`, `changesLook`), `looksTheSame`, and `differenceFromFile` (Glimmer's picture against the one saved in the file: `mean` on a scale of 0 to 255 and `shareVisiblyDifferent` from 0 to 1). Read it before you rely on the layers: text, smart objects, adjustment layers, fill layers and effects are not editable or are left out, and the notes say which. The same report is shown to the person in the window.\n- `POST /api/v1/documents/{id}/export` with `{\"format\": \"psd\"}` returns a Photoshop file with the layers (`\"depth\"`: 8 or 16; `\"flatten\": true` merges everything into one layer). The response header `X-Glimmer-Export-Report` holds JSON: `layersWritten`, `notes` (what is written as pixels or left out, each with `layer`, `kind`, `what`, `changesLook`), and for a document that blends in linear light `layersBlendedInGamma` (how different the layers will look in other programs). Glimmer reads these files back itself; the layers have not been tried in Photoshop.\n";
#endif
        return "";
    }

    /// Whether this build has Heal Selection (Directory.Build.props: GlimmerHeal).
    public static bool HasHeal =>
#if GLIMMER_HEAL
        true;
#else
        false;
#endif

    /// Whether this build develops RAW files (Directory.Build.props: GlimmerRaw).
    public static bool HasRAW =>
#if GLIMMER_RAW
        true;
#else
        false;
#endif

    internal static string RawNote(bool raw)
    {
#if GLIMMER_RAW
        if (raw)
            return "- A camera's RAW file (DNG, CR3, RAF, ORF, RW2 and others; the bytes sent to `POST /api/v1/documents`) opens as a layer that keeps the file: the layer has `raw` (`fileName`, `byteCount`, `settings`, `process`, `pixelsAsDeveloped`), and `develop_raw` develops it again with other settings, without loss. `GET /api/v1/documents/{id}/layers/{layer}/raw/info` says what the file allows. If the RAW decoder gives no picture for the file, the answer is 422 `raw_not_developed` with the reason; another recording mode of the same camera may work. To get the developed picture out, export as usual: a sibling app that has a RAW file sends it here, sets what it wants with `develop_raw`, and takes the picture back with `export`.\n";
#endif
        return "";
    }

    /// The guide an agent fetches first: what Glimmer is and exactly how to drive it.
    /// `heal`, `psd`, `raw`: whether the guide describes those features. The app passes nothing (what it was
    /// built with); the site's copy is written for what a release has.
    public static string Guide(ushort port, string appVersion, IAgentOperationCatalog catalog, bool? heal = null, bool? psd = null, bool? raw = null)
    {
        var healOn = heal ?? HasHeal;
        var psdOn = psd ?? HasPSD;
        var rawOn = raw ?? HasRAW;
        var baseUrl = $"http://127.0.0.1:{port}";
        var paid = PaidOps(catalog);
        var s = AgentGuideText.Head
            .Replace("@@VERSION@@", appVersion)
            .Replace("@@BASE@@", baseUrl)
            .Replace("@@AGENT@@", AgentHeader)
            .Replace("@@PS@@", PhotoshopNote(psdOn))
            .Replace("@@RAW@@", RawNote(rawOn))
            .Replace("@@STRAY@@", catalog.StrayExample)
            .Replace("@@NESTED@@", catalog.NestedStrayExample)
            .Replace("@@REQ@@", Glimmer.AI.Requirement.GuideSection());
        var sb = new StringBuilder(s);
        foreach (var op in catalog.Operations.Where(o => (healOn || o.Name != "heal_selection") && (rawOn || o.Name != "develop_raw")))
        {
            sb.Append($"\n### `{op.Name}`{(paid.Contains(op.Name) ? " (paid)" : "")}\n\n{op.Description}\n");
            if (op.Params.Count == 0)
            {
                sb.Append($"\nNo params: `{{\"op\": \"{op.Name}\"}}`\n");
                continue;
            }
            sb.Append("\n| Param | Type | Description |\n|---|---|---|\n");
            foreach (var p in op.Params)
            {
                var description = p.Description.Replace("|", "\\|");
                if (!description.Contains("optional", StringComparison.OrdinalIgnoreCase)) description = "Required. " + description;
                if (p.Options is { } options)
                    description += options.Count > 40 ? " One of the ids in the table below." : " One of: `" + string.Join("`, `", options) + "`";
                sb.Append($"| `{p.Name}` | {p.Type} | {description} |\n");
            }
            if (op.Name == "run_command")
            {
                sb.Append("\n| Command id | What it does |\n|---|---|\n");
                foreach (var d in catalog.RunnableCommands)
                    sb.Append($"| `{d.Id}` | {d.Title.Replace("…", "")} |\n");
            }
        }
        sb.Append(AgentGuideText.Tail.Replace("@@HEAL@@", ObjectRemovalNote(healOn)));
        return sb.ToString();
    }
}
