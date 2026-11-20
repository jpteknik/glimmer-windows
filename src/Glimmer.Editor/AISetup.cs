// Ported from App/AIController.swift (MARK: Setup, Text, Unknown price: the statics that choose a service and a model)
// and App/AIController+TextGate.swift (the notices and the price of a text request). The Mac reads UserDefaults and the
// Keychain from static members; here they are read from the app's settings and keys, which an instance is given.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glimmer.AI;

namespace Glimmer.Editor;

/// <summary>A text request couldn't be set up (no key, no model). Its words are English, as an agent is told them; the
/// window says them in the person's language (PersonMessage).</summary>
public sealed class TextSetupError(string message) : Exception(message);

/// <summary>Which AI service and model a request goes to, the keys, and what the person has been told and agreed to.</summary>
public sealed class AISetup(EditorEnvironment environment)
{
    private IAppSettings Settings => environment.Settings;
    private ProviderKeys Keys => environment.Keys;
    private HttpMessageHandler? Http => environment.Http;

    /// <summary>Where finished picture results are kept, so an identical request isn't paid for twice.</summary>
    public string ResultCache => Path.Combine(environment.DataFolder, "Cache", "AIResults");

    // MARK: Keys

    /// <summary>The OpenRouter key. Debug builds also accept the OPENROUTER_API_KEY environment variable, so test runs
    /// never store a key.</summary>
    public string? ApiKey()
    {
        if (Keys.Key("openrouter") is { Length: > 0 } key) return key;
#if DEBUG
        if (System.Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") is { Length: > 0 } fromEnvironment) return fromEnvironment;
#endif
        return null;
    }

    public bool HasAPIKey => ApiKey() is not null;

    /// <summary>Whether the picture service chosen in Settings has a key saved (OpenRouter unless another is chosen).</summary>
    public bool HasImageKey
    {
        get
        {
            var p = ImageProvider;
            return p.Slug == "openrouter" ? ApiKey() is not null : Keys.Key(p.Slug) is not null;
        }
    }

    // MARK: Text: which service and model (the register is AIProvider)

    public const string TextProviderKey = "ai.textProvider";
    public static string TextModelKey(string slug) => slug == "openrouter" ? AIDefaults.TextModelKey : $"ai.textModel.{slug}";

    /// <summary>The service chosen for text in Settings, if it is offered; otherwise OpenRouter.</summary>
    public AIProvider TextProvider =>
        AIProvider.Named(Settings.Get(TextProviderKey, "openrouter")) is { } p && p.Offers(AIProvider.Capability.Text) ? p : AIProvider.OpenRouter;

    /// <summary>The model chosen for <paramref name="provider"/>, and what its service's list said it can do (null: the
    /// list didn't say).</summary>
    public (string Id, bool? Tools, bool? Vision) ChosenTextModel(AIProvider provider)
    {
        var key = TextModelKey(provider.Slug);
        var id = Settings.Get<string?>(key, null) ?? (provider.Slug == "openrouter" ? AIDefaults.TextModel : "");
        bool? Flag(string name) => Settings.Contains(key + name) ? Settings.Get(key + name, false) : null;
        return (id, Flag(".tools"), Flag(".vision"));
    }

    /// <summary>The model every text request goes to (the Assistant, suggestions, Find Distractions).</summary>
    public ITextModel TextModel() => TextModel(TextProvider);

    /// <summary>The model chosen in Settings for <paramref name="provider"/>, with its key read for this one use.</summary>
    public ITextModel TextModel(AIProvider provider)
    {
        if (provider.Slug == "ollama")
        {
            var chosen = ChosenTextModel(provider).Id;
            if (chosen.Length == 0) throw new TextSetupError($"Choose a {provider.Name} model in Settings › AI.");
            var address = Uri.TryCreate(Settings.Get("ai.address.ollama", ""), UriKind.Absolute, out var a) ? a : new Uri(provider.BaseUrl);
            return provider.TextModel("", chosen, address, Http) ?? throw new TextSetupError($"Glimmer can't use {provider.Name} for text yet.");
        }
        string? key = provider.Slug == "openrouter" ? ApiKey() : provider.NeedsKey ? Keys.Key(provider.Slug) : "";
        if (key is null)
        {
            if (provider.Slug == "openrouter") throw OpenRouterError.MissingAPIKey();
            throw new TextSetupError(provider.MissingKeyMessage);
        }
        var model = ChosenTextModel(provider).Id;
        if (model.Length == 0) throw new TextSetupError($"Choose a {provider.Name} model in Settings › AI.");
        // OpenRouter keeps its endpoint setting; the others go to their own address.
        var baseUrl = provider.Slug == "openrouter" ? Endpoint : null;
        return provider.TextModel(key, model, baseUrl, Http) ?? throw new TextSetupError($"Glimmer can't use {provider.Name} for text yet.");
    }

    /// <summary>Whether a text request can be made at all (a key where one is needed, and a model).</summary>
    public bool HasTextModel
    {
        get
        {
            try { TextModel(); return true; }
            catch (Exception e) when (e is TextSetupError or OpenRouterError) { return false; }
        }
    }

    /// <summary>The configured OpenRouter-compatible endpoint ("ai.endpoint"), or OpenRouter.</summary>
    public Uri Endpoint => AIDefaults.EndpointFrom(Settings.Get(AIDefaults.EndpointKey, ""));

    // MARK: Pictures

    public const string ImageProviderKey = "ai.imageProvider";
    public static string ImageModelKey(string slug) => $"ai.imageModel.{slug}";
    public static string ImageEditModelKey(string slug) => $"ai.imageEditModel.{slug}";
    /// <summary>Set when the person has agreed to what the picture features send to this service.</summary>
    public static string ImageToldKey(string slug) => $"ai.imageNoticeShown.{slug}";

    /// <summary>The service the picture features go to.</summary>
    public AIProvider ImageProvider =>
        Settings.Get<string?>(ImageProviderKey, null) is { } slug && AIProvider.Named(slug) is { } p && p.Offers(AIProvider.Capability.Image) ? p : AIProvider.OpenRouter;

    /// <summary>What the picture features send to <paramref name="provider"/>, said before it is used (the-apps/PROVIDERS.md,
    /// rule 7).</summary>
    public static string ImageNotice(AIProvider provider)
    {
        var parts = new List<string>
        {
            $"Generate, Edit with Prompt, Generative Fill and the other picture features send your prompt and the pictures they work on (the document or the selected part of it, and any reference pictures) to {provider.Name} ({provider.Host}).",
        };
        if (provider.TermsNote is { } note) parts.Add(note);
        parts.Add($"{provider.Name} bills your key for each picture.");
        return string.Join(" ", parts);
    }

    // MARK: Unknown price (the-apps/SIBLINGS.md, "Okänt pris")

    /// <summary>Set when the person has ticked, once for this service, that it states no price in advance.</summary>
    public static string UnknownPriceKey(string slug) => $"ai.unknownPriceAgreed.{slug}";

    /// <summary>Whether a paid picture job may start from the window: the price is known, or the person has made the tick.</summary>
    public bool UnknownPriceAgreed
    {
        get
        {
            var p = ImageProvider;
            return PaidPrice.KnownInAdvance(p.Slug) || Settings.Get(UnknownPriceKey(p.Slug), false);
        }
    }

    /// <summary>The picture model a paid job goes to. <paramref name="agentAllowsUnknownPrice"/>: the agent API's own word
    /// for this call, never remembered.</summary>
    public IImageModel ImageModel(bool agentAllowsUnknownPrice)
    {
        // The last stop before a paid picture job; the buttons that lead here are grey until the tick is made.
        if (!UnknownPriceAgreed && !agentAllowsUnknownPrice)
            throw new TextSetupError($"{ImageProvider.Name} does not state a price in advance. Tick that you know in Settings › AI, and run it again.");
        if (Settings.Get<string?>(ImageProviderKey, null) is { } slug && slug != "openrouter" && AIProvider.Named(slug) is { } provider
            && provider.Offers(AIProvider.Capability.Image))
        {
            // What goes to the service was said, and agreed to, when it was chosen in Settings.
            if (!Settings.Get(ImageToldKey(slug), false))
                throw new TextSetupError($"Choose {provider.Name} again in Settings › AI and confirm what is sent to it.");
            var key = Keys.Key(slug) ?? throw new TextSetupError(provider.MissingKeyMessage);
            if (Settings.Get(ImageModelKey(slug), "") is not { Length: > 0 } id
                || provider.ImageModel(key, id, Settings.Get<string?>(ImageEditModelKey(slug), null), Http) is not { } made)
                throw new TextSetupError($"Choose a {provider.Name} model in Settings › AI.");
            return made;
        }
        var apiKey = ApiKey() ?? throw OpenRouterError.MissingAPIKey();
        var model = Settings.Get<string?>(AIDefaults.ImageModelKey, null) ?? AIDefaults.ImageModel;
        // The documented Image API is switched on by a setting ("ai.openRouterImageRoute" = "images") until a real call
        // through it has been made.
        var route = Settings.Get("ai.openRouterImageRoute", "") == "images" ? OpenRouterImageModel.RouteKind.Images : OpenRouterImageModel.RouteKind.Chat;
        var client = new OpenRouterClient(apiKey, AIProvider.OpenRouter.ChatConfiguration(Endpoint), Http);
        return new OpenRouterImageModel(client, model, route, cacheDirectory: ResultCache);
    }

    // MARK: What a text request sends, and the first-use notice (AIController+TextGate.swift)

    public static string ToldKey(string slug) => $"ai.textNoticeShown.{slug}";
    /// <summary>Set when the notice that was agreed to said that a picture of the document is sent.</summary>
    public static string ToldPicturesKey(string slug) => $"ai.textNoticeShown.{slug}.pictures";

    /// <summary>Whether the person has been told what goes to <paramref name="provider"/>. A local server needs no
    /// telling. Someone who agreed while a model without picture input was chosen was told that no picture is sent, so
    /// the first request that does send one asks again.</summary>
    public bool HasBeenTold(AIProvider provider, bool sendsPictures)
    {
        if (provider.Kind != AIProvider.KindOf.Cloud) return true;
        return Settings.Get(ToldKey(provider.Slug), false) && (!sendsPictures || Settings.Get(ToldPicturesKey(provider.Slug), false));
    }

    public void MarkTold(AIProvider provider, bool sendsPictures)
    {
        Settings.Set(ToldKey(provider.Slug), true);
        if (sendsPictures) Settings.Set(ToldPicturesKey(provider.Slug), true);
    }

    /// <summary>What a text request sends to <paramref name="provider"/>, said before the first one (the-apps/PROVIDERS.md,
    /// rule 7) and shown again in Settings. Only what is known: where it is processed is the service's own statement.</summary>
    public static string TextNotice(AIProvider provider, bool sendsPictures) => TextNoticeText(provider, sendsPictures).ToString();

    /// <summary>The same as a text the app translates sentence by sentence.</summary>
    public static UiText TextNoticeText(AIProvider provider, bool sendsPictures)
    {
        var parts = new List<UiText>
        {
            sendsPictures
                ? new UiText("The Assistant sends what you typed, the list of Glimmer's operations, a description of your document (its size and layers) and a small picture of it to {0} ({1}).", provider.Name, provider.Host)
                : new UiText("The Assistant sends what you typed, the list of Glimmer's operations and a description of your document (its size and layers) to {0} ({1}). The chosen model takes no pictures, so none is sent.", provider.Name, provider.Host),
            sendsPictures
                ? "Suggest Edits and Find Distractions send a picture of the document too."
                : "Suggest Edits and Find Distractions need to see the document, so they don't run with this model.",
        };
        if (provider.Kind == AIProvider.KindOf.Cloud)
        {
            if (provider.DataNote is { } place) parts.Add(new UiText("{0} says it processes this in {1}.", provider.Name, place));
            parts.Add(new UiText("{0} bills your key for each request.", provider.Name));
        }
        else
        {
            // The Mac says "this Mac".
            parts.Add("Nothing leaves this computer and nothing is billed.");
        }
        return Sentences(parts);
    }

    /// <summary>Sentences one after another, each translated on its own.</summary>
    public static UiText Sentences(IReadOnlyList<UiText> parts) =>
        new(string.Join(" ", parts.Select((_, i) => $"{{{i}}}")), parts.Cast<object?>().ToArray());

    // MARK: The price of a text request

    public static string TextPriceKey(string slug) => $"ai.textPrice.{slug}";
    /// <summary>Set when the person has said, once for this service, that they know it states no price in advance.</summary>
    public static string TextUnknownPriceKey(string slug) => $"ai.unknownPriceAgreed.text.{slug}";

    /// <summary>The service's model list, as Settings reads it. A debug build takes a list given in the settings instead
    /// ("ai.modelListJSON.&lt;slug&gt;", the answer of GET /models), so a test copy that makes no connection can show a
    /// listed price.</summary>
    public async Task<IReadOnlyList<ProviderModel>> TextModelListAsync(AIProvider provider, string? key, CancellationToken cancellation = default)
    {
#if DEBUG
        if (Settings.Get<string?>($"ai.modelListJSON.{provider.Slug}", null) is { } given)
        {
            byte[] data;
            try { data = Convert.FromBase64String(given); }
            catch (FormatException) { data = System.Text.Encoding.UTF8.GetBytes(given); }
            return ModelCatalog.TextModels(provider.Slug, data);
        }
#endif
        return await ModelCatalog.FetchTextModelsAsync(provider, provider.Slug == "openrouter" ? Endpoint : null, key, Http, cancellation).ConfigureAwait(false);
    }

    /// <summary>A model's listed price ("€0.25 in, €0.50 out per million tokens"), or null when the list has none.</summary>
    public static string? PriceText(ProviderModel? model)
    {
        if (model is not { InputPrice: { } i, OutputPrice: { } o }) return null;
        var sign = model.Currency == "EUR" ? "€" : model.Currency == "USD" ? "$" : model.Currency is { } c ? c + " " : "";
        string Amount(double v) => sign + v.ToString("0.00", CultureInfo.InvariantCulture);
        return $"{Amount(i)} in, {Amount(o)} out per million tokens";
    }

    /// <summary>Called when Settings has read the service's model list: keeps the chosen model's listed price with the
    /// time, or forgets it when the list has none.</summary>
    public void RememberTextPrice(string slug, string model, string? line, DateTimeOffset? now = null)
    {
        if (line is null)
        {
            Settings.Set<string?>(TextPriceKey(slug), null);
            return;
        }
        var kept = new JsonObject { ["model"] = model, ["line"] = line, ["read"] = (now ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture) };
        Settings.Set(TextPriceKey(slug), kept.ToJsonString());
    }

    /// <summary>The listed price of the model now chosen for <paramref name="provider"/>, if the list had one when it was
    /// last read.</summary>
    public (string Line, DateTimeOffset Read)? ListedTextPrice(AIProvider provider)
    {
        if (Settings.Get<string?>(TextPriceKey(provider.Slug), null) is not { Length: > 0 } text) return null;
        try
        {
            var kept = JsonNode.Parse(text) as JsonObject;
            if (kept?["model"]?.GetValue<string>() != ChosenTextModel(provider).Id) return null;
            if (kept["line"]?.GetValue<string>() is not { } line
                || !DateTimeOffset.TryParse(kept["read"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var read)) return null;
            return (line, read);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Whether a text request to <paramref name="provider"/> may go without asking about the price: nothing is
    /// billed (a local server), the price is listed, or the person has said they know it is not.</summary>
    public bool TextPriceSettled(AIProvider provider) =>
        provider.Kind != AIProvider.KindOf.Cloud || ListedTextPrice(provider) is not null || Settings.Get(TextUnknownPriceKey(provider.Slug), false);

    /// <summary>The line the Assistant shows about what a request costs; null when nothing is billed.</summary>
    public UiText? TextPriceLine(AIProvider provider)
    {
        if (provider.Kind != AIProvider.KindOf.Cloud) return null;
        if (ListedTextPrice(provider) is { } listed)
            return new UiText("{0}'s list, read {1}: {2}.", provider.Name, listed.Read.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), listed.Line);
        return "Price not known in advance";
    }
}
