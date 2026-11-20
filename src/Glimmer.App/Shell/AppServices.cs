// The app's side of the platform services (Contract/PlatformServices.cs), and the preferences the views keep.
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glimmer.Editor;

namespace Glimmer.App;

/// <summary>Where the views find the app-wide services. Set once at startup (App.OnStartup).</summary>
public static class AppServices
{
    public static IAppSettings Settings { get; set; } = new JsonAppSettings(null);
    public static IDocumentHost? Documents { get; set; }
    public static DocumentOpener? Opener { get; set; }
    public static IClipboardService Clipboard { get; set; } = new Platform.ClipboardService();
    public static IFileDialogs FileDialogs { get; set; } = new Platform.FileDialogs();
    /// <summary>Set by the editor's agent server when it runs (Help › Agent Access, the launch splash); null until then.</summary>
    public static IAgentServer? AgentServer { get; set; }
    public static ILinkedFilesWaiting? LinkedFiles { get; set; }
    public static IModelLibrary? Models { get; set; }
    /// <summary>A new BatchRunner for each Batch Process sheet (the Mac's sheet owns its runner); null until the editor has
    /// one, and Batch Process then does nothing.</summary>
    public static Func<IBatchRunner>? BatchRunners { get; set; }
    public static IAISettingsService? AISettings { get; set; }
    /// <summary>File › Export › Photoshop File… for a document: Glimmer.Formats' PSDExport over the session's state.</summary>
    public static Func<IEditorSession, IPhotoshopDocument> PhotoshopDocuments { get; set; } = session => new Views.Sheets.PhotoshopExport(session);
    /// <summary>Layer › Develop… for a document: the editor's develop model for the RAW file in its active layer, or null
    /// when that layer has none (the command then says so).</summary>
    public static Func<IEditorSession, IRawDevelopModel?> RawDevelopModels { get; set; } =
        session => (session as EditorSession)?.MakeRawDevelopModel();
}

/// <summary>UserDefaults for Windows: one JSON file under %APPDATA%\Glimmer, written after each change.</summary>
public sealed class JsonAppSettings : IAppSettings
{
    private readonly string? path;
    private readonly JsonObject values;
    private readonly object gate = new();

    public JsonAppSettings(string? path)
    {
        this.path = path;
        values = new JsonObject();
        if (path is not null && File.Exists(path))
        {
            try { values = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
            catch (JsonException) { values = new JsonObject(); }   // a damaged file starts over rather than failing to start
        }
    }

    public static JsonAppSettings ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glimmer", "settings.json"));

    public event EventHandler<string>? Changed;

    public bool Contains(string key) { lock (gate) return values.ContainsKey(key); }

    public T Get<T>(string key, T fallback)
    {
        lock (gate)
        {
            if (!values.TryGetPropertyValue(key, out var node) || node is null) return fallback;
            try { return node.Deserialize<T>() ?? fallback; }
            catch (JsonException) { return fallback; }
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (gate)
        {
            values[key] = JsonSerializer.SerializeToNode(value);
            if (path is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        Changed?.Invoke(this, key);
    }
}

/// <summary>Keys of preferences the views read (Swift @AppStorage); the editor's own are in Glimmer.Editor.Preferences.</summary>
public static class AppPreferences
{
    public const string ShowOmnibarKey = "ui.showOmnibar";
    public const string ShowSelectionBarKey = "canvas.showSelectionBar";
    public const string CanvasBackgroundKey = "canvas.background";
    public const string CheckerSizeKey = "canvas.checkerSize";
    public const string SwatchesKey = "swatches.saved";
    public const string PaletteRecentsKey = "palette.recents";
    public const string PaletteUsageKey = "palette.usage";
    public const string SettingsTabKey = "settings.tab";
    public const string WelcomeShownKey = "onboarding.welcomeShown";
    /// <summary>GlimmerDocument.historyLimitKey / historyBudgetKey (Settings › Performance).</summary>
    public const string HistoryLimitKey = "history.limit";
    public const string HistoryBudgetKey = "history.memoryBudgetMB";
}
