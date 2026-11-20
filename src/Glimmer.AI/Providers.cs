// Ported from Packages/GlimmerKit/Sources/GlimmerAI/Providers.swift
using System.Globalization;
using System.Text.Json.Nodes;

namespace Glimmer.AI;

/// The one list of AI services Glimmer can use (the-apps/PROVIDERS.md). Key fields in
/// Settings, model pickers, "key missing" messages, the agent API's key status and the table
/// in the manual all come from here, so a change of the list is a change in one place.
///
/// `partial`: the picture side (`ImageModel(...)`, which builds OpenAI's, Google's, fal's and
/// Hedra's image models) lives with ImageProviders and is added there.
public sealed partial class AIProvider : IEquatable<AIProvider>
{
    public enum KindOf { Cloud, LocalServer }
    public enum Capability { Text, Image }
    /// How Glimmer talks to it for text.
    public enum TextAdapterKind { ChatCompletions, Responses, AnthropicMessages }

    /// The series' name for it: also the key store's account its key is stored under.
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required KindOf Kind { get; init; }
    public required IReadOnlyList<Capability> Serves { get; init; }
    public required TextAdapterKind? TextAdapter { get; init; }
    /// Where requests go (a local server's address is a setting; this is its default).
    public required string BaseUrl { get; init; }
    /// What its chat completions accept.
    public ChatDialect Dialect { get; init; } = ChatDialect.Plain;
    /// What is offered to the person. Every cloud service that is built is offered, by its name
    /// alone (Anders, 2026-11-16: "låt alla synas", "ta bort märkningen"); before that, a service
    /// was hidden until he had made a paid test of it. Those nobody has called yet say so where
    /// the key is entered and in the manual (`FromDocumentationOnly`). A local server is shown
    /// only once a model has passed Glimmer's own evaluation.
    public IReadOnlySet<Capability> Offered { get; init; } = new HashSet<Capability>();
    /// Built for these (an adapter exists and passes its tests), offered or not.
    public IReadOnlySet<Capability> Built { get; init; } = new HashSet<Capability>();
    /// Shown where the key is entered: only what was read, with a link to the service's own terms.
    public string? TermsNote { get; init; }
    public string? TermsUrl { get; init; }
    public string? KeysUrl { get; init; }
    public string? PricesUrl { get; init; }
    /// One line for the manual: where the data is processed, in the service's own words.
    public string? DataNote { get; init; }
    /// Nobody has made a real call: said in the manual.
    public bool FromDocumentationOnly { get; init; }
    /// For a local server: the models that have passed Glimmer's own evaluation
    /// (`LocalModelEvaluation`, numbers in docs/TESTING-NOTES.md). Others that are installed
    /// are listed as "not evaluated with Glimmer".
    public IReadOnlyList<string> EvaluatedModels { get; init; } = Array.Empty<string>();

    public string Id => Slug;
    public bool NeedsKey => Kind == KindOf.Cloud;

    public bool Offers(Capability capability) => Offered.Contains(capability);

    public string MissingKeyMessage => $"Add your {Name} API key in Settings › AI.";

    /// The host requests go to, for "sent to …".
    public string Host => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var url) && url.Host.Length > 0 ? url.Host : BaseUrl;

    public bool Equals(AIProvider? other) => other is not null && Slug == other.Slug;
    public override bool Equals(object? obj) => Equals(obj as AIProvider);
    public override int GetHashCode() => Slug.GetHashCode();

    private static HashSet<Capability> Caps(params Capability[] capabilities) => new(capabilities);

    // MARK: The list

    public static IReadOnlyList<AIProvider> All { get; } = new List<AIProvider>
    {
        new()
        {
            Slug = "openrouter", Name = "OpenRouter", Kind = KindOf.Cloud, Serves = new[] { Capability.Text, Capability.Image },
            TextAdapter = TextAdapterKind.ChatCompletions, BaseUrl = AIDefaults.DefaultEndpoint,
            Dialect = new ChatDialect(),
            Offered = Caps(Capability.Text, Capability.Image), Built = Caps(Capability.Text, Capability.Image),
            KeysUrl = "https://openrouter.ai/keys", TermsUrl = "https://openrouter.ai/terms", PricesUrl = "https://openrouter.ai/models",
        },
        new()
        {
            Slug = "openai", Name = "OpenAI", Kind = KindOf.Cloud, Serves = new[] { Capability.Text, Capability.Image },
            TextAdapter = TextAdapterKind.Responses, BaseUrl = "https://api.openai.com/v1",
            Built = Caps(Capability.Text, Capability.Image), Offered = Caps(Capability.Text, Capability.Image),
            FromDocumentationOnly = true,
            KeysUrl = "https://platform.openai.com/api-keys", PricesUrl = "https://developers.openai.com/api/docs/pricing",
        },
        new()
        {
            Slug = "anthropic", Name = "Anthropic", Kind = KindOf.Cloud, Serves = new[] { Capability.Text },
            TextAdapter = TextAdapterKind.AnthropicMessages, BaseUrl = "https://api.anthropic.com/v1",
            Built = Caps(Capability.Text), Offered = Caps(Capability.Text),
            FromDocumentationOnly = true,
            KeysUrl = "https://platform.claude.com/settings/keys", PricesUrl = "https://platform.claude.com/docs/en/about-claude/pricing",
            TermsUrl = "https://www.anthropic.com/legal/commercial-terms",
        },
        new()
        {
            Slug = "xai", Name = "xAI", Kind = KindOf.Cloud, Serves = new[] { Capability.Text },
            TextAdapter = TextAdapterKind.Responses, BaseUrl = "https://api.x.ai/v1",
            Built = Caps(Capability.Text), Offered = Caps(Capability.Text),
            FromDocumentationOnly = true,
            KeysUrl = "https://console.x.ai", PricesUrl = "https://docs.x.ai/developers/models",
        },
        new()
        {
            Slug = "google", Name = "Google", Kind = KindOf.Cloud, Serves = new[] { Capability.Text, Capability.Image },
            TextAdapter = TextAdapterKind.ChatCompletions, BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai",
            // Google's OpenAI-compatible route ("still in beta" in its own words). Its page shows tools,
            // tool_choice, pictures and streaming; what it doesn't show is left out.
            Dialect = ChatDialect.Plain with { ParallelToolCalls = false, JsonObject = false },
            Built = Caps(Capability.Text, Capability.Image), Offered = Caps(Capability.Text, Capability.Image),
            FromDocumentationOnly = true,
            KeysUrl = "https://aistudio.google.com/apikey", PricesUrl = "https://ai.google.dev/gemini-api/docs/pricing",
            TermsNote = "For professional or business use, and for people who are 18 or older. Google's rules restrict pictures of recognizable people.",
            TermsUrl = "https://ai.google.dev/gemini-api/terms",
        },
        new()
        {
            Slug = "berget", Name = "Berget", Kind = KindOf.Cloud, Serves = new[] { Capability.Text },
            TextAdapter = TextAdapterKind.ChatCompletions, BaseUrl = "https://api.berget.ai/v1",
            Offered = Caps(Capability.Text), Built = Caps(Capability.Text),
            KeysUrl = "https://console.berget.ai", TermsUrl = "https://berget.ai/en/terms", PricesUrl = "https://berget.ai/en/pricing",
            TermsNote = "Berget AI runs open models in Stockholm. Its terms say it never stores the content of prompts or answers.",
            DataNote = "Stockholm, Sweden",
            // Tried with a real call by Anders, 2026-11-16 (docs/TESTING-NOTES.md).
        },
        new()
        {
            Slug = "grunden", Name = "Grunden", Kind = KindOf.Cloud, Serves = new[] { Capability.Text },
            TextAdapter = TextAdapterKind.ChatCompletions, BaseUrl = "https://api.grunden.ai/v1",
            // Its models reason at full depth unless told otherwise; the Assistant wants a quick plan.
            Dialect = ChatDialect.Plain with { ReasoningEffort = "low" },
            Offered = Caps(Capability.Text), Built = Caps(Capability.Text),
            KeysUrl = "https://grunden.ai/login", TermsUrl = "https://grunden.ai/terms", PricesUrl = "https://grunden.ai/models/",
            TermsNote = "Grunden AI runs open models in Sweden and says all processing happens there.",
            DataNote = "Sweden",
            FromDocumentationOnly = true,
        },
        new()
        {
            Slug = "fal", Name = "fal.ai", Kind = KindOf.Cloud, Serves = new[] { Capability.Image },
            TextAdapter = null, BaseUrl = "https://fal.run",
            Built = Caps(Capability.Image), Offered = Caps(Capability.Image),
            FromDocumentationOnly = true,
            KeysUrl = "https://fal.ai/dashboard/keys", TermsUrl = "https://fal.ai/legal/terms-of-service", PricesUrl = "https://fal.ai/pricing",
            // Only what has been read. Glimmer sends pictures inside the request and asks for the result the same way, so nothing is put on fal's storage.
            TermsNote = "fal keeps what you send in its request history for 90 days by default. Glimmer sends your pictures inside the request and gets the result back the same way, so nothing is placed on fal's file storage, which is public by default.",
        },
        new()
        {
            Slug = "hedra", Name = "Hedra", Kind = KindOf.Cloud, Serves = new[] { Capability.Image },
            TextAdapter = null, BaseUrl = "https://api.hedra.com/v3",
            Built = Caps(Capability.Image), Offered = Caps(Capability.Image),
            FromDocumentationOnly = true,
            KeysUrl = "https://www.hedra.com/develop", PricesUrl = "https://www.hedra.com/pricing",
            TermsNote = "Upload only pictures of yourself: Hedra's rules forbid media of other people. Hedra gets a license to what you make with it. Your pictures are uploaded to Hedra for the job; the upload expires after an hour.",
            TermsUrl = "https://www.hedra.com/terms",
        },
        new()
        {
            Slug = "ollama", Name = "Ollama", Kind = KindOf.LocalServer, Serves = new[] { Capability.Text },
            TextAdapter = TextAdapterKind.ChatCompletions, BaseUrl = "http://127.0.0.1:11434/v1",
            Dialect = ChatDialect.Plain with { ToolChoice = false },
            // Offered since qwen3:8b passed the evaluation (2026-11-16), and only shown while a
            // server is running on this Mac.
            Built = Caps(Capability.Text), Offered = Caps(Capability.Text),
            EvaluatedModels = new[] { "qwen3:8b" },
            TermsNote = "Runs on this computer with Ollama: nothing is sent anywhere and nothing is billed. Slower than a cloud model; an answer can take up to a minute on a busy computer.",
            DataNote = "This computer",
        },
        new()
        {
            Slug = "mlx", Name = "MLX", Kind = KindOf.LocalServer, Serves = new[] { Capability.Text },
            TextAdapter = TextAdapterKind.ChatCompletions, BaseUrl = "http://127.0.0.1:8080/v1",
            DataNote = "This computer",
            // `mlx_lm.server`: an OpenAI-style /v1/chat/completions on port 8080. Built from that
            // one fact (the-apps/research/providers-glimmer.md); what else its server takes was not
            // read, so only the plainest request is sent. Experimental and hidden until it has run
            // Glimmer's evaluation on a Mac that has it (PROVIDERS.md, rule 6).
            Dialect = ChatDialect.Plain with { ToolChoice = false, ParallelToolCalls = false, UsageInclude = false, JsonObject = false },
            Built = Caps(Capability.Text),
            FromDocumentationOnly = true,
        },
    };

    public static AIProvider? Named(string slug) => All.FirstOrDefault(p => p.Slug == slug);

    public static AIProvider OpenRouter => Named("openrouter")!;

    /// The services offered for `capability`, in list order.
    public static IReadOnlyList<AIProvider> Offering(Capability capability) => All.Where(p => p.Offers(capability)).ToList();

    /// The client configuration for this service's chat completions. `baseUrl` overrides the
    /// address (OpenRouter's endpoint setting, a local server's address).
    public OpenRouterConfiguration ChatConfiguration(Uri? baseUrl = null) =>
        new(baseUrl ?? new Uri(BaseUrl))
        {
            Dialect = Dialect,
            NeedsKey = NeedsKey,
            ProviderName = Slug == "openrouter" ? null : Name,
        };

    /// The text model for this service: the adapter its `TextAdapter` names. `baseUrl` overrides
    /// the address (OpenRouter's endpoint setting, a local server's address). The key is passed
    /// in for this one model and kept nowhere else. `handler` is the transport (tests pass a fake one).
    public ITextModel? TextModel(string key, string model, Uri? baseUrl = null, HttpMessageHandler? handler = null)
    {
        if (!Built.Contains(Capability.Text)) return null;
        if (Slug == "ollama") return new OllamaTextModel(model, baseUrl ?? new Uri(BaseUrl), handler: handler);
        switch (TextAdapter)
        {
            case TextAdapterKind.ChatCompletions:
                return new OpenRouterTextModel(new OpenRouterClient(key, ChatConfiguration(baseUrl), handler), model, Name);
            case TextAdapterKind.Responses:
                return new ResponsesTextModel(this, key, model, handler);
            case TextAdapterKind.AnthropicMessages:
                return new AnthropicTextModel(this, key, model, handler);
            default:
                return null;
        }
    }

    /// The manual's table, generated so it can't drift from the list.
    public static string ManualTable()
    {
        var lines = new List<string> { "| Service | Used for | Key | Where your data is processed | Notes |", "|---|---|---|---|---|" };
        foreach (var p in All.Where(p => p.Offered.Count > 0))
        {
            var uses = string.Join(", ", Enum.GetValues<Capability>().Where(p.Offers).Select(c => c == Capability.Text ? "text (the Assistant)" : "images"));
            var key = p.NeedsKey ? "your own, in **Settings › AI**" : "none (shown only while it is running on your computer)";
            var notes = new List<string>();
            if (p.FromDocumentationOnly) notes.Add($"Built from {p.Name}'s documentation; not yet tried with a real call.");
            if (p.TermsNote is { } t) notes.Add(t);
            lines.Add($"| {p.Name} | {uses} | {key} | {p.DataNote ?? "see the service's terms"} | {string.Join(" ", notes)} |");
        }
        return string.Join("\n", lines);
    }
}

// MARK: Keys

/// The API keys, one secret store item per service: the account is the service's slug. A key is
/// read when a request is made and passed into it; it is never kept on a shared object or
/// written to a log. On the Mac the items are in the keychain; on Windows the app passes a
/// factory that makes Credential Manager-backed stores, and tests pass `InMemory()`.
public sealed class ProviderKeys
{
    /// The account OpenRouter's key was stored under before the series agreed on slugs.
    public const string OldOpenRouterAccount = "openrouter-api-key";

    private readonly Func<string, ISecretStore> store;

    public ProviderKeys(Func<string, ISecretStore> store) { this.store = store; }

    /// Keys kept in memory (the Mac's `-ai.noKeychain YES`, and tests).
    public static ProviderKeys InMemory(MemorySecrets? secrets = null)
    {
        secrets ??= new MemorySecrets();
        return new ProviderKeys(account => new MemorySecretStore(secrets, account));
    }

    public ISecretStore StoreFor(string slug) => store(slug);

    /// The key for `slug`, or null. OpenRouter's key is moved from its old item the first time.
    public string? Key(string slug)
    {
        var key = store(slug).Read();
        if (!string.IsNullOrEmpty(key)) return key;
        if (slug != "openrouter") return null;
        return MigrateOpenRouterKey();
    }

    /// Moves the old item to the new account: write the new one, read it back, and only then
    /// remove the old. Stopped anywhere in between, the key is still found the next time
    /// (under the new account if it got that far, otherwise under the old), and the move finishes then.
    internal string? MigrateOpenRouterKey()
    {
        var old = store(OldOpenRouterAccount);
        var key = old.Read();
        if (string.IsNullOrEmpty(key)) return null;
        var @new = store("openrouter");
        @new.Write(key);
        if (@new.Read() != key) return key;   // not safely there: keep the old item
        old.Delete();
        return key;
    }

    /// Finishes a move that was interrupted after the new item was written.
    public void RemoveLeftoverOpenRouterItem()
    {
        try
        {
            var @new = store("openrouter").Read();
            if (string.IsNullOrEmpty(@new)) return;
            var old = store(OldOpenRouterAccount).Read();
            if (old != @new) return;
            store(OldOpenRouterAccount).Delete();
        }
        catch (Exception)
        {
            // `try?`: it is tried again at the next start.
        }
    }

    /// Present or missing per service that needs a key: for Settings and the agent API. Never the key.
    public Dictionary<string, bool> Status()
    {
        var result = new Dictionary<string, bool>();
        foreach (var p in AIProvider.All.Where(p => p.NeedsKey && p.Offered.Count > 0))
        {
            string? key;
            try { key = Key(p.Slug); } catch (Exception) { key = null; }
            result[p.Slug] = key is not null;
        }
        return result;
    }
}

// MARK: Models

/// One model of a service, as its list endpoint describes it.
public sealed record ProviderModel(string Id, bool? Tools = null, bool? Vision = null, double? InputPrice = null, double? OutputPrice = null, string? Currency = null)
{
    /// "€0.25 in, €0.50 out per million tokens", or null when the list has no prices.
    public string? PriceLine
    {
        get
        {
            if (InputPrice is not { } i || OutputPrice is not { } o) return null;
            var sign = Currency == "EUR" ? "€" : Currency == "USD" ? "$" : (Currency is { } c ? c + " " : "");
            static string N(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
            return $"{sign}{N(i)} in, {sign}{N(o)} out per million tokens";
        }
    }
}

/// Reads a service's model list, so a picker never offers an id the service has retired.
public static class ModelCatalog
{
    public sealed class ListError : Exception, IEquatable<ListError>
    {
        public string Provider { get; }
        public string Reason { get; }
        public ListError(string provider, string reason) { Provider = provider; Reason = reason; }
        public override string Message => $"Couldn't read {Provider}'s model list: {Reason}";
        public bool Equals(ListError? other) => other is not null && Provider == other.Provider && Reason == other.Reason;
        public override bool Equals(object? obj) => Equals(obj as ListError);
        public override int GetHashCode() => HashCode.Combine(Provider, Reason);
    }

    /// The text models in a `GET /models` answer. Each service describes capabilities its own
    /// way; an entry that is clearly not a chat model (embeddings, speech) is left out.
    public static List<ProviderModel> TextModels(string slug, byte[] data)
    {
        var name = AIProvider.Named(slug)?.Name ?? slug;
        var list = Json.ParseObject(data) is { } json ? Json.Objects(json["data"]) : null;
        if (list is null) throw new ListError(name, "the answer isn't a model list.");

        static double? Number(JsonNode? v)
        {
            if (Json.Num(v) is { } d) return d;
            if (Json.Str(v) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            if (v is JsonObject o) return Number(o["amount"]);
            return null;
        }

        var models = new List<ProviderModel>();
        foreach (var entry in list)
        {
            if (Json.Str(entry["id"]) is not { } id) continue;
            var m = new ProviderModel(id);
            switch (slug)
            {
                case "berget":
                {
                    // {"model_type": "text", "capabilities": {"function_calling": true, "vision": true}, "pricing": {"input": 0.25, "output": 0.5, "currency": "EUR"}}
                    var kind = Json.Str(entry["model_type"]) ?? Json.Str(entry["type"]);
                    if (!(kind is null || kind == "text")) continue;
                    var caps = Json.Obj(entry["capabilities"]);
                    m = m with { Tools = Json.Bool(caps?["function_calling"]), Vision = Json.Bool(caps?["vision"]) };
                    if (Json.Obj(entry["pricing"]) is { } price)
                        m = m with { InputPrice = Number(price["input"]), OutputPrice = Number(price["output"]), Currency = Json.Str(price["currency"]) };
                    break;
                }
                case "grunden":
                {
                    // {"type": "chat", "capabilities": ["chat", "tools", "vision"], "tool_calling": true, "input_modalities": ["text", "image"],
                    //  "pricing": {"input_per_1m_tokens": {"amount": "0.20", "currency": "EUR"}, …}}
                    if (Json.Str(entry["type"]) != "chat") continue;
                    var caps = Json.Strings(entry["capabilities"]) ?? new List<string>();
                    m = m with
                    {
                        Tools = Json.Bool(entry["tool_calling"]) ?? caps.Contains("tools"),
                        Vision = Json.Strings(entry["input_modalities"])?.Contains("image") ?? caps.Contains("vision"),
                    };
                    if (Json.Obj(entry["pricing"]) is { } price)
                        m = m with
                        {
                            InputPrice = Number(price["input_per_1m_tokens"]),
                            OutputPrice = Number(price["output_per_1m_tokens"]),
                            Currency = Json.Str(Json.Obj(price["input_per_1m_tokens"])?["currency"]),
                        };
                    break;
                }
                case "openrouter":
                {
                    // From OpenRouter's documentation of GET /models (not yet read from a real answer by Glimmer):
                    // {"pricing": {"prompt": "0.000003", "completion": "0.000015"}, "architecture": {"input_modalities": ["text", "image"]},
                    //  "supported_parameters": ["tools", …]}. The prices are US dollars per token, written as text.
                    if (Json.Obj(entry["pricing"]) is { } price && Number(price["prompt"]) is { } i && Number(price["completion"]) is { } o && i >= 0 && o >= 0)
                        m = m with { InputPrice = i * 1_000_000, OutputPrice = o * 1_000_000, Currency = "USD" };
                    if (Json.Strings(entry["supported_parameters"]) is { } parameters) m = m with { Tools = parameters.Contains("tools") };
                    if (Json.Strings(Json.Obj(entry["architecture"])?["input_modalities"]) is { } inputs) m = m with { Vision = inputs.Contains("image") };
                    break;
                }
                default:
                    break;   // ids only (Ollama, MLX)
            }
            models.Add(m);
        }
        return models.OrderBy(m => m.Id, StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true)).ToList();
    }

    /// Fetches the list. Berget's and Grunden's are public; a key is sent when there is one.
    public static async Task<List<ProviderModel>> FetchTextModelsAsync(AIProvider provider, Uri? baseUrl = null, string? key = null,
                                                                       HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        var request = new RequestSpec(Http.Append(baseUrl ?? new Uri(provider.BaseUrl), "models")) { Timeout = TimeSpan.FromSeconds(20) };
        if (!string.IsNullOrEmpty(key))
        {
            if (provider.TextAdapter == AIProvider.TextAdapterKind.AnthropicMessages)
            {
                foreach (var header in AnthropicTextModel.Headers(key).Where(h => h.Key != "Content-Type")) request.SetHeader(header.Key, header.Value);
            }
            else
            {
                request.SetHeader("Authorization", $"Bearer {key}");
            }
        }
        HttpData data;
        try
        {
            data = await Http.DataAsync(KeyStaysHome.CreateClient(handler), request, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (Http.IsConnectionFailure(error, ct))
        {
            throw new ListError(provider.Name, Http.Describe(error));
        }
        if (data.Status is < 200 or >= 300)
            throw new ProviderHttpError(provider.Name, data.Status, ProviderHttpError.MessageFrom(data.Body));
        return TextModels(provider.Slug, data.Body);
    }
}
