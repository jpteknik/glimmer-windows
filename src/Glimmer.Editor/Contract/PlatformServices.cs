// View contract: Windows services the editor library needs and the app provides (PORTING.md: "anything that needs
// Windows is an interface in Glimmer.Editor with its implementation in Glimmer.App"). These are the ones the views and
// the session share; the AI and file-format ports may add their own (key storage, OCR).
using Glimmer.Core;

namespace Glimmer.Editor;

/// <summary>App preferences, the counterpart of UserDefaults / @AppStorage. Keys are the original's
/// ("ui.showOmnibar", "canvas.showSelectionBar", "swatches.saved", "palette.recents", "ui.classicLayout", ...), so
/// the same names can be found on both sides. The app stores them as JSON under %APPDATA%\Glimmer.</summary>
public interface IAppSettings
{
    T Get<T>(string key, T fallback);
    void Set<T>(string key, T value);
    bool Contains(string key);
    /// <summary>Raised on the UI thread after any key changes (Swift: UserDefaults.didChangeNotification).</summary>
    event EventHandler<string>? Changed;
}

/// <summary>The Windows clipboard, for copy, cut and paste of pixels (NSPasteboard on the Mac).</summary>
public interface IClipboardService
{
    /// <summary>Changes whenever anything is put on the clipboard (GetClipboardSequenceNumber; NSPasteboard.changeCount).</summary>
    uint SequenceNumber { get; }
    bool ContainsImage { get; }
    /// <summary>The clipboard's image encoded as PNG (from PNG, DIBV5 or DIB data), or null.</summary>
    byte[]? GetImagePng();
    /// <summary>Image files on the clipboard (copied in Explorer).</summary>
    IReadOnlyList<string> GetImageFiles();
    /// <summary>Puts an image on the clipboard as PNG and as a DIB, for other apps.</summary>
    void SetImage(RgbaImage image);
    void SetText(string text);
}

/// <summary>Open and save dialogs (NSOpenPanel / NSSavePanel).</summary>
public interface IFileDialogs
{
    /// <summary>Image files to open or place; empty when cancelled. title/prompt are catalogue keys (Windows' dialog has
    /// no prompt button text, so prompt only names the purpose).</summary>
    IReadOnlyList<string> ChooseImages(string title, string prompt, bool multiple);
    /// <summary>File › Open…: .glimmer documents, images, Photoshop documents and web projects; empty when cancelled.</summary>
    IReadOnlyList<string> ChooseDocuments(bool multiple = true);
    /// <summary>A path to save to, or null when cancelled. extensions: "png", "jpg", ... (the first is the default).
    /// message is a catalogue key or composed text shown in the dialog.</summary>
    string? ChooseSaveLocation(string suggestedName, IReadOnlyList<string> extensions, UiText? message = null);
    /// <summary>A folder (Batch Process's source and destination, a .glimmer package folder from a Mac), or null.
    /// title is a catalogue key.</summary>
    string? ChooseFolder(string title, string? initial = null);
}

/// <summary>Opens files as documents (DocumentOpener.open): .glimmer, images, PSD, .aips, each in its own tab. Called for
/// files given on the command line, handed over by a second copy of the app, dropped on the window, or chosen in
/// File › Open….</summary>
public interface IDocumentOpener
{
    void Open(IReadOnlyList<string> paths);
}

// Text in pictures and faces (Vision on the Mac): Glimmer.AI defines what the features need from the system,
// Glimmer.AI.IImageTextRecognizer (Windows.Media.Ocr) and Glimmer.AI.IFaceDetector (Windows.Media.FaceAnalysis). The app
// implements both and sets Glimmer.AI.OnDeviceServices.TextRecognizer and .FaceDetector at start-up; null means the
// computer has none, and the commands that read text say so (TextRecognitionUnavailableError).

/// <summary>What the person chose when a tab with unsaved changes closes.</summary>
public enum SaveChangesChoice { Save, DontSave, Cancel }

/// <summary>What the person chose when a linked tab with edits not saved back closes (EditorSession.closeLinkedWindow).</summary>
public enum LinkedCloseChoice { SaveBack, KeepForLater, Discard, Cancel }

/// <summary>Windows and documents (DocumentOpener): what the menus, the start card and the welcome window ask for, and
/// what the editor asks of the app's windows.</summary>
public interface IDocumentHost
{
    void ShowNewDocumentWindow();
    // File › Open… is DocumentOpener.ShowOpenPanel (IFileDialogs.ChooseDocuments, then Open), as on the Mac.
    void ShowWelcomeWindow();
    void ShowAgentSplash();
    void ShowLinkedWaiting();
    /// <summary>Settings at a pane: "general", "ai", "models", "performance", "tablet".</summary>
    void OpenSettings(string pane);

    /// <summary>A new tab for a document the editor opened or made (the Mac's new document window).</summary>
    void ShowDocument(IEditorSession session);
    /// <summary>Brings a document's tab (and the app) to the front.</summary>
    void BringForward(IEditorSession session);
    /// <summary>The session may close and asks for its tab to go (File › Close, after the questions of TryClose, or a
    /// linked tab once its file is saved back).</summary>
    void CloseDocument(IEditorSession session);
    /// <summary>A tab with unsaved changes is closing: Save, Don't Save or Cancel. Asked by EditorSession.TryClose.</summary>
    SaveChangesChoice AskToSaveChanges(IEditorSession session);
    /// <summary>A linked tab with steps not saved back is closing (EditorSession.closeLinkedWindow's alert):
    /// message and detail are the alert's texts; canSaveBack false offers "Save a Glimmer Copy…" in place of "Save Back".</summary>
    LinkedCloseChoice AskToCloseLinked(IEditorSession session, UiText message, UiText detail, bool canSaveBack);
    /// <summary>An error with no document window to show it in (NSApp.presentError).</summary>
    void PresentError(UiText message);
    /// <summary>A file was opened or saved: for the recent documents list (noteNewRecentDocumentURL).</summary>
    void NoteRecentDocument(string path);
    /// <summary>A small window while something slow runs (developing a RAW file); disposing it closes the window.</summary>
    IDisposable ShowProgress(UiText title, UiText message);
}

/// <summary>The app's own sounds (AppSound.swift). The editor checks the "quiet" setting before it calls.</summary>
public interface IAppSound
{
    void Beep();
}
