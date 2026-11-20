// Ported from the AI pane of App/SettingsView.swift: what it asks of AIController's statics, AIDefaults, ProviderKeys and
// the services' model lists, as one object the Windows Settings window binds to.
using Glimmer.AI;

namespace Glimmer.Editor;

/// <summary>Settings › AI's model (IAISettingsService). Keys go through the app's ProviderKeys and are never logged.</summary>
public sealed class AISettingsService(EditorEnvironment environment) : IAISettingsService
{
    private readonly AISetup setup = new(environment);

    public string? Key(string slug) => environment.Keys.Key(slug);

    public void SaveKey(string slug, string key)
    {
        var store = environment.Keys.StoreFor(slug);
        var trimmed = key.Trim();
        if (trimmed.Length == 0) store.Delete();
        else store.Write(trimmed);
    }

    /// <summary>OpenRouter's GET /key for <paramref name="key"/>: the credit left, or null for a key without a limit. Throws
    /// OpenRouterError (an invalid key, no connection) with words for the person.</summary>
    public async Task<decimal?> TestOpenRouterKeyAsync(string key, CancellationToken cancellation)
    {
        var client = new OpenRouterClient(key.Trim(), AIProvider.OpenRouter.ChatConfiguration(setup.Endpoint), environment.Http);
        var info = await client.KeyInfoAsync(cancellation).ConfigureAwait(false);
        return info.LimitRemaining is { } left ? (decimal)left : null;
    }

    public string DefaultEndpoint => AIDefaults.DefaultEndpoint;

    /// <summary>The same rule as the endpoint the requests use: an absolute http(s) address with a host.</summary>
    public bool IsUsableEndpoint(string text) =>
        Uri.TryCreate(text.Trim(' ', '\t'), UriKind.Absolute, out var url)
        && (url.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        && url.Host.Length > 0;

    public string DefaultImageModel => AIDefaults.ImageModel;
    public string DefaultTextModel => AIDefaults.TextModel;
    public string? Retirement(string model) => AIDefaults.Retirement(model);

    public async Task<IReadOnlyList<ProviderModel>> TextModelsAsync(AIProvider provider, string? key, CancellationToken cancellation) =>
        await setup.TextModelListAsync(provider, key, cancellation).ConfigureAwait(false);

    /// <summary>The models a local server (Ollama) has installed; null when nothing answers at its address.</summary>
    public async Task<IReadOnlyList<LocalModel>?> LocalModelsAsync(AIProvider provider, CancellationToken cancellation)
    {
        if (provider.Slug != "ollama") return null;
        var address = Uri.TryCreate(environment.Settings.Get("ai.address.ollama", ""), UriKind.Absolute, out var a) ? a : new Uri(provider.BaseUrl);
        try
        {
            var installed = await OllamaTextModel.InstalledAsync(address, environment.Http, cancellation).ConfigureAwait(false);
            return [.. installed.Select(m => new LocalModel(m.Name, m.Tools, m.Vision, m.Size))];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    public string TextNotice(AIProvider provider) => AISetup.TextNotice(provider, sendsPictures: setup.ChosenTextModel(provider).Vision != false);
    public string ImageNotice(AIProvider provider) => AISetup.ImageNotice(provider);
    public string? PriceText(ProviderModel? model) => AISetup.PriceText(model);
    public (string Line, DateTimeOffset Read)? ListedTextPrice(AIProvider provider) => setup.ListedTextPrice(provider);
    public void RememberTextPrice(string slug, string model, string? line) => setup.RememberTextPrice(slug, model, line);
    public bool OnDeviceRouterAvailable => OnDeviceRouter.IsAvailable;
}
