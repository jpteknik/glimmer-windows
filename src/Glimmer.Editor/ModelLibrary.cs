// Ported from the Models pane of App/SettingsView.swift and App/ModelDownloadSheet.swift: what they ask of
// ModelStore.shared, as one object the Windows views can bind to.
using Glimmer.AI;

namespace Glimmer.Editor;

/// <summary>The on-device models (Settings › Models, the download question): installed or not, checked or not, downloaded
/// on the person's click and removed. Keeps <see cref="ModelStore.Shared"/>, which the features load their models from,
/// pointed at the same folder and the same source.</summary>
public sealed class ModelLibrary : ObservableObject, IModelLibrary
{
    private readonly EditorEnvironment environment;
    private readonly HttpMessageHandler? handler;
    private ModelStore store;

    /// <param name="folder">Where models live: %LOCALAPPDATA%\Glimmer\Models unless given (tests give their own).</param>
    public ModelLibrary(EditorEnvironment environment, string? folder = null)
    {
        this.environment = environment;
        handler = environment.Http;
        Folder = folder ?? Path.Combine(environment.DataFolder, "Models");
        store = MakeStore();
        ModelStore.Shared = store;
    }

    private ModelStore MakeStore() => new(Folder, ModelStore.SourceFrom(Source), handler: handler);

    public string Folder { get; }

    /// <summary>"models.source": empty or not an http(s) address means Hugging Face. Setting it points the store at it.</summary>
    public string Source
    {
        get => environment.Settings.Get(ModelStore.SourceKey, "");
        set
        {
            var text = value.Trim();
            if (text == Source) return;
            environment.Settings.Set(ModelStore.SourceKey, text);
            store = MakeStore();
            ModelStore.Shared = store;
            Raise();
        }
    }

    /// <summary>Where downloads come from now (the setting as the store reads it).</summary>
    public Uri SourceAddress => ModelStore.SourceFrom(Source);

    public bool IsInstalled(OnDeviceModel model) => store.IsInstalled(model);
    public bool NeedsDownload(OnDeviceModel model) => store.NeedsDownload(model);
    public ModelVerification Verification(OnDeviceModel model) => store.Verification(model);

    public async Task DownloadAsync(OnDeviceModel model, IProgress<double> progress, CancellationToken cancellation)
    {
        try
        {
            await store.DownloadAsync(model, progress.Report, cancellation).ConfigureAwait(false);
        }
        finally
        {
            // Installed, or half-way and gone again: the rows read their state anew.
            environment.Scheduler.Post(() => Raise(string.Empty));
        }
    }

    public void Remove(OnDeviceModel model)
    {
        store.Remove(model);
        Raise(string.Empty);
    }
}
