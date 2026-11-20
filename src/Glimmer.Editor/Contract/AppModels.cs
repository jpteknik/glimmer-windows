// View contract, part 2: what Settings, the sheets and the app-level windows need beyond one document's session. Same
// rules as ViewContractTypes.cs. Each interface names the Swift type it stands for.
using System.ComponentModel;
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;

namespace Glimmer.Editor;

/// <summary>AgentServer.shared: Glimmer's local API for agents, as the splash and Settings › General show it.</summary>
public interface IAgentServer : INotifyPropertyChanged
{
    /// <summary>The line to hand to an agent (it names the guide's address); null while starting or after a failure.</summary>
    string? AgentPrompt { get; }
    /// <summary>Why the server could not start, in the system's words; null otherwise.</summary>
    string? Failure { get; }
}

/// <summary>File › Linked Files Waiting… (DocumentOpener.waitingEntries, LinkJournal.broken/discard).</summary>
public interface ILinkedFilesWaiting
{
    IReadOnlyList<WaitingLink> Entries();
    /// <summary>Kept edits that can't be read (folder names); they stay where they are.</summary>
    IReadOnlyList<string> Broken { get; }
    /// <summary>Opens the kept edits in a window; false when they couldn't be read.</summary>
    bool Open(WaitingLink entry);
    /// <summary>Moves the layered copy to the Recycle Bin; false when that failed (the edits are kept).</summary>
    bool Discard(WaitingLink entry);
}

/// <summary>One kept edit of a file from another app. UnreturnedSteps: null means one.</summary>
public sealed record WaitingLink(string Id, string FileName, string Requester, DateTimeOffset WrittenAt, long Bytes, bool Unreturned, int? UnreturnedSteps);

/// <summary>ModelStore.shared as Settings › Models and the download sheet use it.</summary>
public interface IModelLibrary : INotifyPropertyChanged
{
    bool IsInstalled(OnDeviceModel model);
    bool NeedsDownload(OnDeviceModel model);
    ModelVerification Verification(OnDeviceModel model);
    /// <summary>Downloads and checks every file; progress 0…1. Throws on failure (its message is for the person).</summary>
    Task DownloadAsync(OnDeviceModel model, IProgress<double> progress, CancellationToken cancellation);
    void Remove(OnDeviceModel model);
    /// <summary>Where the models are kept (Show in File Explorer).</summary>
    string Folder { get; }
    /// <summary>"models.source": the address models are fetched from (Hugging Face, or a mirror).</summary>
    string Source { get; set; }
}

/// <summary>File › Batch Process… (BatchRunner): runs a recipe over many files, one at a time off the UI thread, and
/// rates photos a few at a time. Paths are absolute.</summary>
public interface IBatchRunner : INotifyPropertyChanged
{
    bool IsRunning { get; }
    int Done { get; }
    int Total { get; }
    IReadOnlyList<BatchOutcome> Outcomes { get; }
    /// <summary>The outcomes that failed, as they come, for the list under the progress bar.</summary>
    IReadOnlyList<BatchOutcome> Failures { get; }
    /// <summary>Whether a processed file succeeded; null while it hasn't been processed.</summary>
    bool? Succeeded(string path);
    void Start(IReadOnlyList<string> files, BatchRecipe recipe, ExportOptions options, string destination, string suffix);
    void Cancel();

    bool IsRating { get; }
    int Rated { get; }
    PhotoRating? Rating(string path);
    /// <summary>Rates the photos on this computer (aesthetics and sharpness), a few at a time.</summary>
    void Rate(IReadOnlyList<string> files);
}

public sealed record BatchOutcome(string Source, string? Output, string? Error);

/// <summary>What a batch does to every image.</summary>
public abstract record BatchRecipe
{
    public sealed record ConvertOnly : BatchRecipe;
    public sealed record AutoEnhance : BatchRecipe;
    public sealed record RemoveBackground : BatchRecipe;
    public sealed record Preset(string Id) : BatchRecipe;
    public sealed record Action(Guid Id) : BatchRecipe;
}

/// <summary>PhotoQuality.Rating: Stars 0…5; Score orders photos (higher is better).</summary>
public sealed record PhotoRating(int Stars, bool IsBlurry, bool IsUtility, double Aesthetics, double Sharpness, double Score);

/// <summary>What Settings › AI needs from the AI library (AIController's statics, AIDefaults, ProviderKeys, the model lists).
/// Keys are read and written through ProviderKeys with the app's Credential Manager stores; never logged.</summary>
public interface IAISettingsService
{
    /// <summary>The saved key for a service, or null (OpenRouter's is moved from its old item the first time).</summary>
    string? Key(string slug);
    /// <summary>Saves the key; an empty one removes it. Throws SecretStoreException with words for the person.</summary>
    void SaveKey(string slug, string key);
    /// <summary>OpenRouter's key info: the credit left in US dollars, or null when the key has no limit.</summary>
    Task<decimal?> TestOpenRouterKeyAsync(string key, CancellationToken cancellation);
    /// <summary>AIDefaults: "https://openrouter.ai/api/v1", and whether a typed endpoint is a usable http(s) address.</summary>
    string DefaultEndpoint { get; }
    bool IsUsableEndpoint(string text);
    string DefaultImageModel { get; }
    string DefaultTextModel { get; }
    /// <summary>AIDefaults.retirement(of:): a catalogue key saying the model is retired or retiring, or null.</summary>
    string? Retirement(string model);
    /// <summary>A service's text models from its own list (AIController.textModelList).</summary>
    Task<IReadOnlyList<ProviderModel>> TextModelsAsync(AIProvider provider, string? key, CancellationToken cancellation);
    /// <summary>The models a local server has installed; null when it isn't running.</summary>
    Task<IReadOnlyList<LocalModel>?> LocalModelsAsync(AIProvider provider, CancellationToken cancellation);
    /// <summary>"What is sent to …" for text and for pictures (AIController.textNotice / imageNotice).</summary>
    string TextNotice(AIProvider provider);
    string ImageNotice(AIProvider provider);
    /// <summary>The price line of a listed model ("$0.25 in, $0.50 out per million tokens"), or null.</summary>
    string? PriceText(ProviderModel? model);
    /// <summary>The price read for the service's chosen text model, with when it was read; null when none was read.</summary>
    (string Line, DateTimeOffset Read)? ListedTextPrice(AIProvider provider);
    void RememberTextPrice(string slug, string model, string? line);
    /// <summary>OnDeviceRouter.isAvailable: simple requests understood on this computer.</summary>
    bool OnDeviceRouterAvailable { get; }
}

/// <summary>A model installed in a local server (OllamaTextModel.Installed).</summary>
public sealed record LocalModel(string Name, bool Tools, bool Vision, long? Size);

/// <summary>Layer › Develop… (RawDevelopModel and the sheet's develop/cancel): the RAW file in the active layer developed
/// again with other settings. Drafts render off the UI thread, latest settings only; Picture follows Settings.</summary>
public interface IRawDevelopModel : INotifyPropertyChanged
{
    string FileName { get; }
    long FileBytes { get; }
    RawInfo Info { get; }
    /// <summary>Something was painted or pasted on the layer since it was developed.</summary>
    bool PixelsChanged { get; }
    RawSettings Settings { get; set; }
    /// <summary>Settings.Temperature or, when unset, the camera's own.</summary>
    double Temperature { get; set; }
    double Tint { get; set; }
    /// <summary>The latest draft; null until the first is ready.</summary>
    RgbaImage? Picture { get; }
    /// <summary>The picture on screen does not show the controls' settings yet.</summary>
    bool IsBehind { get; }
    /// <summary>A draft after the first took over a tenth of a second (said once seen, not guessed).</summary>
    bool FollowsBehind { get; }
    string? Problem { get; }
    bool IsDeveloping { get; }
    bool IsPickingNeutral { get; set; }
    /// <summary>The camera's white balance and the decoder's own values; the process stays.</summary>
    void ResetToAsShot();
    /// <summary>White balance from a point that should be grey; x and y are 0…1 from the picture's top left.</summary>
    void PickNeutral(double x, double y);
    /// <summary>Develops at full size and replaces the layer as one undo step ("Develop RAW"). True when done; false when it
    /// failed or was given up after the time limit (Problem says why, and nothing changed).</summary>
    Task<bool> DevelopAsync();
    /// <summary>Abandons a development that is running; the document stays as it was.</summary>
    void Cancel();
}

/// <summary>RawDevelopment.info: the file's size and the decoder's own values and abilities.</summary>
public sealed record RawInfo(int Width, int Height, double Temperature, double Tint, double Contrast, double Sharpness, double LuminanceNoise, double ColorNoise,
                             bool LensCorrection, bool SupportsContrast, bool SupportsSharpness, bool SupportsLuminanceNoise, bool SupportsColorNoise,
                             bool SupportsHighlightRecovery, bool SupportsLensCorrection);

/// <summary>The document parts of File › Export › Photoshop File… (PSDExport.data(for:depth:flattened:)). The Import Report
/// is the session's (IEditorSession.ImportReport).</summary>
public interface IPhotoshopDocument
{
    /// <summary>The document blends in linear light (state.blendSpace == .linear): its layers look different elsewhere.</summary>
    bool BlendsInLinearLight { get; }
    /// <summary>Encodes the document as a Photoshop file off the UI thread, with the report of what is written how.</summary>
    Task<(byte[] Data, PSDExportReport Report)> PreparePSDExportAsync(int depth, bool flattened, CancellationToken cancellation);
}
