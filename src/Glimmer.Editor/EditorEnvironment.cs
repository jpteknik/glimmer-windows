// What the Mac reads from singletons and the system (UserDefaults.standard, NSPasteboard.general, NSOpenPanel,
// DocumentOpener.shared, FileManager's folders), handed to the editor once instead. No Swift file of its own.
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Glimmer.Editor;

/// <summary>The app-wide services and folders every document and session of one app run shares.</summary>
public sealed class EditorEnvironment
{
    public EditorEnvironment(IAppSettings settings, IUiScheduler scheduler, IDocumentHost host, IFileDialogs dialogs,
                             IClipboardService clipboard, IAppSound? sound = null, string? temporaryFolder = null,
                             string? dataFolder = null)
    {
        Settings = settings;
        Scheduler = scheduler;
        Host = host;
        Dialogs = dialogs;
        Clipboard = clipboard;
        Sound = sound;
        TemporaryFolder = temporaryFolder ?? Path.GetTempPath();
        DataFolder = dataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glimmer");
    }

    public IAppSettings Settings { get; }
    public IUiScheduler Scheduler { get; }
    public IDocumentHost Host { get; }
    public IFileDialogs Dialogs { get; }
    public IClipboardService Clipboard { get; }
    public IAppSound? Sound { get; }

    /// <summary>Where undo steps are spilled (FileManager.default.temporaryDirectory on the Mac).</summary>
    public string TemporaryFolder { get; }

    /// <summary>%LOCALAPPDATA%\Glimmer: the link journal and the recovery copies live under it (Application Support on the Mac).</summary>
    public string DataFolder { get; }

    /// <summary>Crash recovery copies of documents with unsaved changes.</summary>
    public string RecoveryFolder => Path.Combine(DataFolder, "Recovery");

    /// <summary>How often a document with unsaved changes is copied to <see cref="RecoveryFolder"/>.</summary>
    public TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Called on the UI thread with what the editor writes to its log (Swift's Logger).</summary>
    public Action<string>? Log { get; init; }

    /// <summary>The app's translation of a catalogue key, for the few texts the editor stores in the document: the
    /// names it gives new layers ("Kurvor", "Lager 1"), as String(localized:) does on the Mac. Null: English.</summary>
    public Func<string, string>? Localize { get; init; }

    /// <summary>A name Glimmer gives a new layer, in the user's language (EditorSession+Helpers.swift's layerName).</summary>
    public string LayerName(string english) => Localize?.Invoke(english) ?? english;

    /// <summary>Moves a file or folder to the Recycle Bin (FileManager.trashItem on the Mac); false when it couldn't.
    /// The app provides it (Microsoft.VisualBasic.FileIO.FileSystem with RecycleOption.SendToRecycleBin).</summary>
    public Func<string, bool>? MoveToRecycleBin { get; init; }

    /// <summary>The app's opener (DocumentOpener.shared on the Mac). A DocumentOpener sets it when it is made; a session
    /// that opens documents (Open Package Folder…, Upscale's new document) goes through it.</summary>
    public DocumentOpener? Opener { get; internal set; }

    /// <summary>The services' keys (ProviderKeys.keychain on the Mac): the app gives the ones kept in Windows' Credential
    /// Manager; without, keys live only as long as the run.</summary>
    public Glimmer.AI.ProviderKeys Keys { get; init; } = Glimmer.AI.ProviderKeys.InMemory();

    /// <summary>The transport of every request to an AI service; null is the network. Tests give a fake one, so no test
    /// reaches a real service.</summary>
    public HttpMessageHandler? Http { get; init; }
}

/// <summary>INotifyPropertyChanged with Swift's didSet-style setters (@Observable).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Raise(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Sets a field and raises PropertyChanged when the value changed; returns whether it did.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>App-wide preference keys and their readings (SettingsView.swift's Preferences, and keys scattered over the App).</summary>
public static class Preferences
{
    public const string CanvasBackgroundKey = "canvas.background";
    public const string CheckerSizeKey = "canvas.checkerSize";
    public const string ShowOmnibarKey = "ui.showOmnibar";
    public const string ClassicLayoutKey = "ui.classicLayout";
    public const string CropDeletesKey = "crop.deletePixels";
    public const string SkipStartCardKey = "skipStartCard";
    public const string HistoryLimitKey = "history.limit";
    public const string HistoryBudgetKey = "history.memoryBudgetMB";
    public const string QuietKey = "quiet";
    public const string LinkJournalKey = "linkJournal";

    /// <summary>The canvas surround (Preferences.CanvasBackground): grey for the named ones, null for "Match System".</summary>
    public static double? CanvasBackgroundGrey(IAppSettings settings) => settings.Get(CanvasBackgroundKey, "system") switch
    {
        "light" => 0.85,
        "mid" => 0.5,
        "dark" => 0.2,
        "black" => 0,
        _ => null,
    };

    /// <summary>Glimmer's own near-black, #0c0d11 (Theme.backgroundRGB), the surround for "Match System".</summary>
    public static readonly (double R, double G, double B) ThemeBackground = (0x0c / 255.0, 0x0d / 255.0, 0x11 / 255.0);

    public static double CheckerSize(IAppSettings settings)
    {
        double v = settings.Get(CheckerSizeKey, 0.0);
        return v > 0 ? v : 8;
    }
}
