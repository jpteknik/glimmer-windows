// Windows document semantics, which the Mac takes from NSDocument and its menus (App/AppCommands.swift's Save group,
// GlimmerDocument.swift's writer): explicit Save, Save As and Save a Copy for Mac, the question when a tab with unsaved
// changes closes, and crash recovery (Recovery.cs). Linked documents keep the Mac's own flow (EditorSession+Linked).
using Glimmer.Core;
using Glimmer.Formats;

namespace Glimmer.Editor;

public sealed partial class EditorSession
{
    private readonly SaveFailures saveFailures = new();
    private RepeatingTimer? recoveryTimer;
    private long? recoveryWrittenAt;
    private bool recoveryWriting;
    private bool closed;

    /// <summary>This session's folder name under the recovery folder.</summary>
    public string RecoveryID { get; private set; } = Guid.NewGuid().ToString("N");

    /// <summary>The document came from a recovery copy: it keeps that copy (now this run's) until it is saved or closed,
    /// so a second stop before the next copy loses nothing.</summary>
    internal void AdoptRecovery(string id)
    {
        RecoveryID = id;
        try { RecoveryStore.Claim(Environment.RecoveryFolder, id); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        recoveryWrittenAt = Document.ChangeCount;
    }

    /// <summary>File › Save (Ctrl+S): back to the linked file while linked, an explanation while the link is broken, the
    /// document's own file otherwise (Save As when it has none).</summary>
    public void SaveCommand()
    {
        if (linkedImage is not null) SaveLinkedImage();
        else if (brokenLink is not null) ExplainBrokenLink();
        else SaveDocument();
    }

    /// <summary>File › Save As… (Ctrl+Shift+S); a linked document's is the layered copy (Save a Glimmer Copy…).</summary>
    public void SaveAsCommand()
    {
        if (linkedImage is not null || brokenLink is not null) SaveGlimmerCopy();
        else SaveDocumentAs();
    }

    /// <summary>Saves to the document's file, or asks where first. <paramref name="finished"/> gets true when it was saved.</summary>
    public void SaveDocument(Action<bool>? finished = null)
    {
        CommitLiveEdit();
        if (Document.FilePath is not { } path)
        {
            SaveDocumentAs(finished);
            return;
        }
        SaveTo(path, finished);
    }

    public void SaveDocumentAs(Action<bool>? finished = null)
    {
        CommitLiveEdit();
        var path = Environment.Dialogs.ChooseSaveLocation(SuggestedFileName, ["glimmer"]);
        if (path is null)
        {
            finished?.Invoke(false);
            return;
        }
        SaveTo(path, finished);
    }

    /// <summary>The name a save dialog suggests: the document's name with .glimmer.</summary>
    private string SuggestedFileName =>
        (Document.FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : documentName) + ".glimmer";

    /// <summary>Writes the document to <paramref name="path"/> (off the UI thread) and says what went wrong if it did.</summary>
    public void SaveTo(string path, Action<bool>? finished = null)
    {
        var name = Path.GetFileName(path);
        Document.Save(path, error =>
        {
            if (error is null)
            {
                saveFailures.Saved(name);
                finished?.Invoke(true);
                return;
            }
            Environment.Log?.Invoke($"couldn't save {name}: {error.Message}");
            ErrorMessage = saveFailures.Failed(error.Message, (error as DocumentSaveException)?.RecoverySuggestion, name);
            finished?.Invoke(false);
        });
    }

    /// <summary>After a save that worked: the window takes the file's name, the recovery copy goes.</summary>
    private void DocumentWasSaved(string path)
    {
        DocumentName = Path.GetFileNameWithoutExtension(path);
        Environment.Host.NoteRecentDocument(path);
        // A document the person saved as a .glimmer file of their own is theirs, not the link's.
        RecoveryStore.Remove(Environment.RecoveryFolder, RecoveryID);
        recoveryWrittenAt = null;
    }

    /// <summary>File › Save a Copy for Mac…: the same content as a package folder (name.glimmer as a directory, which a Mac
    /// opens as a document). The document keeps its own file and stays as saved or unsaved as it was.</summary>
    public void SaveCopyForMac()
    {
        CommitLiveEdit();
        var path = Environment.Dialogs.ChooseSaveLocation(SuggestedFileName, ["glimmer"], "A Mac opens this folder as a Glimmer document.");
        if (path is null) return;
        var name = Path.GetFileName(path);
        Document.SaveCopyAsPackageFolder(path, error =>
        {
            if (error is null) ShowToast(new UiText("Saved {0}", name));
            else ErrorMessage = saveFailures.Failed(error.Message, (error as DocumentSaveException)?.RecoverySuggestion, name);
        });
    }

    /// <summary>Closing the tab (IEditorSession.TryClose).</summary>
    public bool TryClose()
    {
        if (closed) return true;
        CommitLiveEdit();
        if (JournalID is not null)
        {
            if (!CloseLinkedWindow()) return false;
        }
        else if (Document.HasUnsavedChanges)
        {
            switch (Environment.Host.AskToSaveChanges(this))
            {
                case SaveChangesChoice.Cancel:
                    return false;
                case SaveChangesChoice.Save:
                    var path = Document.FilePath ?? Environment.Dialogs.ChooseSaveLocation(SuggestedFileName, ["glimmer"]);
                    if (path is null || !SaveNow(path)) return false;
                    break;
            }
        }
        Closing();
        return true;
    }

    /// <summary>Saves on the UI thread (the tab is closing, so there's nothing else to do meanwhile); false when it failed
    /// (the person has been told why).</summary>
    private bool SaveNow(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            // The document takes the path and says it was saved (DocumentWasSaved runs from its Saved event).
            Document.SaveNow(path);
            saveFailures.Saved(name);
            return true;
        }
        catch (Exception error)
        {
            ErrorMessage = saveFailures.Failed(error.Message, (error as DocumentSaveException)?.RecoverySuggestion, name);
            return false;
        }
    }

    /// <summary>The session's window is going: timers stop, its recovery copy and its undo steps on disk are removed.</summary>
    private void Closing()
    {
        if (closed) return;
        closed = true;
        recoveryTimer?.Dispose();
        recoveryTimer = null;
        linkWatch?.Dispose();
        linkWatch = null;
        toastTask?.Dispose();
        thumbnailTask?.Dispose();
        navigatorTask?.Dispose();
        liveEditCommit?.Dispose();
        RecoveryStore.Remove(Environment.RecoveryFolder, RecoveryID);
        Document.Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after the session let go of its document (TryClose said yes).</summary>
    public event EventHandler? Closed;

    // MARK: Recovery

    private void StartRecovery()
    {
        recoveryTimer = new RepeatingTimer(Scheduler, Environment.RecoveryInterval, WriteRecoveryIfNeeded);
    }

    /// <summary>Copies the document for recovery when it has unsaved changes it didn't have at the last copy. Linked
    /// documents have the link journal instead.</summary>
    public void WriteRecoveryIfNeeded()
    {
        if (closed || recoveryWriting) return;
        if (!Document.HasUnsavedChanges || JournalID is not null)
        {
            if (recoveryWrittenAt is not null)
            {
                RecoveryStore.Remove(Environment.RecoveryFolder, RecoveryID);
                recoveryWrittenAt = null;
            }
            return;
        }
        long change = Document.ChangeCount;
        if (recoveryWrittenAt == change) return;
        var snapshot = State;
        var name = documentName;
        var original = Document.FilePath;
        var folder = Environment.RecoveryFolder;
        var id = RecoveryID;
        recoveryWriting = true;
        Scheduler.Background(() =>
        {
            RecoveryStore.Write(folder, id, snapshot, name, original);
            return true;
        }, _ =>
        {
            recoveryWriting = false;
            // Saved or closed meanwhile: the copy is not wanted.
            if (closed || !Document.HasUnsavedChanges) RecoveryStore.Remove(folder, id);
            else recoveryWrittenAt = change;
        }, failed: error =>
        {
            recoveryWriting = false;
            Environment.Log?.Invoke($"couldn't keep a recovery copy of {name}: {error.Message}");
            if (saveFailures.Failed(error.Message, (error as DocumentSaveException)?.RecoverySuggestion, name + " (recovery)", always: false) is { } said)
                ErrorMessage = said;
        });
    }
}
