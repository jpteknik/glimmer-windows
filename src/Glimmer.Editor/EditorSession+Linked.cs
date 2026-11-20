// Ported from App/EditorSession+Linked.swift
//
// Windows has no sandbox, so a link is a path (no security-scoped bookmark), and no system document machinery to take
// the document out of: "leaving the system's saving" means the document stops counting as unsaved for the app's own
// close question, because the link journal keeps its work. The close button's own question is EditorSession.TryClose.
using Glimmer.Core;
using Glimmer.Formats;

namespace Glimmer.Editor;

/// <summary>"Edit in Glimmer" from a sibling app (Flimmer, Jämmer): the handed-over image is an untitled Glimmer document,
/// and Save writes it back, flattened, into the same file in the same format.</summary>
public sealed partial class EditorSession
{
    private LinkedImage? linkedImage;
    /// <summary>An image a sibling app handed over to edit: Save writes it back. Set through Link(to:), so the link is
    /// saved with the document too.</summary>
    public LinkedImage? LinkedImage { get => linkedImage; private set => Set(ref linkedImage, value); }

    private BrokenLink? brokenLink;
    /// <summary>The document was linked, and after a restart its file can't be reached or written: what it was linked to
    /// and why not. Save explains instead of writing anywhere.</summary>
    public BrokenLink? BrokenLink { get => brokenLink; private set => Set(ref brokenLink, value); }

    /// <summary>This document's entry in the link journal while it is linked, and when it was linked.</summary>
    public string? JournalID { get; internal set; }
    private DateTimeOffset? linkedAt;
    private int? savedBackGeneration;

    private bool hasUnreturnedEdits;
    /// <summary>Steps were made that are not saved back into the linked file.</summary>
    public bool HasUnreturnedEdits { get => hasUnreturnedEdits; private set => Set(ref hasUnreturnedEdits, value); }
    /// <summary>How many steps wait to be saved back.</summary>
    public int UnreturnedSteps { get; private set; }
    private bool keptFromLastTime;
    /// <summary>With Recovered: left knowingly (Quit, or Keep for Later) rather than by a stop. "Kept from last time", not
    /// "Brought back".</summary>
    public bool KeptFromLastTime { get => keptFromLastTime; private set => Set(ref keptFromLastTime, value); }
    private string? journalRoomTrouble;
    /// <summary>The journal could not keep the last steps (no room on the disk): what the window says about it, until a
    /// write fits again.</summary>
    public string? JournalRoomTrouble { get => journalRoomTrouble; private set => Set(ref journalRoomTrouble, value); }
    private bool recovered;
    /// <summary>Brought back from the link journal with edits that were not saved back when Glimmer stopped: said in the
    /// window and in linkedFile.recovered until they are saved back or declined.</summary>
    public bool Recovered { get => recovered; private set => Set(ref recovered, value); }
    /// <summary>Looks at the linked file while the window is open (WatchLinkedFile).</summary>
    private RepeatingTimer? linkWatch;

    /// <summary>The app's link journal (DocumentOpener's), set when the session is registered with it.</summary>
    internal LinkJournal? Journal { get; set; }

    public void SetLinkedImage(LinkedImage image) => LinkedImage = image;

    /// <summary>Links the document to <paramref name="image"/>; the link is saved with the document from now on.</summary>
    public void Link(LinkedImage image)
    {
        SetLinkedImage(image);
        Document.LinkRecord = new DocumentLink(null, image.Path, image.Requester);
        AdoptJournal(null);
        WatchLinkedFile();
    }

    // MARK: The link journal (the-apps/SIBLINGS.md §6.6)

    /// <summary>Starts this document's journal entry, or takes over the one it was opened from: the journal is the one way
    /// a linked window comes back.</summary>
    public void AdoptJournal(LinkJournal.Entry? entry)
    {
        JournalID = entry?.Id ?? JournalID ?? Guid.NewGuid().ToString().ToUpperInvariant();
        linkedAt = entry?.Record.LinkedAt ?? linkedAt ?? DateTimeOffset.UtcNow;
        if (entry is not null)
        {
            HasUnreturnedEdits = entry.Record.Unreturned;
            UnreturnedSteps = entry.Record.UnreturnedSteps ?? (entry.Record.Unreturned ? 1 : 0);
            Recovered = entry.Record.Unreturned;
            KeptFromLastTime = entry.Record.Unreturned && entry.Record.KeptKnowingly == true;
            if (recovered)
            {
                // The whole sentence, once: what happened, that nothing is in the file yet, and that the history starts here.
                var name = entry.Record.FileName;
                var requester = entry.Record.Link.Requester;
                ShowToast(keptFromLastTime
                    ? new UiText("Kept from last time: the edits of {0} are not saved back to {1} yet. Undo starts from here.", name, requester)
                    : new UiText("Brought back after Glimmer stopped: the edits of {0} are not saved back to {1} yet. Undo starts from here.", name, requester));
            }
        }
        // An entry that was just read is on disk as it is; a new link writes its first state now.
        WriteJournal(withState: entry is null);
        LeaveSystemSaving();
    }

    /// <summary>After every step of the document (GlimmerDocument.Step).</summary>
    private void AfterStep()
    {
        if (JournalID is not null) StepWasMade();
    }

    /// <summary>About how large the document is when saved: a guess from its pixels (an eighth of them at 8 bytes a pixel;
    /// pictures compress to about that, noise does not).</summary>
    public long EstimatedEncodedBytes
    {
        get
        {
            if (Document.FilePath is { } path && File.Exists(path)) return new FileInfo(path).Length;
            long rasters = State.AllLayers.Count(l => l.IsRaster);
            return rasters * State.Width * State.Height;
        }
    }

    /// <summary>The journal's word on room for this document's entry (LinkJournal.OnRoom).</summary>
    public void JournalRoom((long Needed, long Free)? trouble)
    {
        if (trouble is not { } t)
        {
            JournalRoomTrouble = null;
            return;
        }
        var name = linkedImage?.FileName ?? brokenLink?.Link.FileName ?? documentName;
        // The Mac says "on this Mac".
        var message = new UiText("There is no room on this computer to keep your latest steps in {0} safe: {1} is needed and {2} is free. What was kept before is still kept. The steps since then are only in this window until you save them back or free up space.",
                                 name, EditorHelpers.FileSize(t.Needed), EditorHelpers.FileSize(Math.Max(t.Free, 0)));
        if (journalRoomTrouble is null) ErrorMessage = message;   // said once, then it stands in the bar
        JournalRoomTrouble = message.ToString();
    }

    /// <summary>After every step: the entry is written, so a crash costs at most the step in progress.</summary>
    private void StepWasMade()
    {
        if (JournalID is null) return;
        HasUnreturnedEdits = true;
        UnreturnedSteps++;
        WriteJournal(withState: true);
        Scheduler.Post(LeaveSystemSaving);
    }

    /// <summary><paramref name="keptKnowingly"/>: at Quit and at Keep for Later, so the next launch can say "Kept from last
    /// time" rather than "Brought back".</summary>
    public void WriteJournal(bool withState, bool keptKnowingly = false)
    {
        if (JournalID is not { } id || (Document.LinkRecord ?? brokenLink?.Link) is not { } link || Journal is not { } journal) return;
        var record = new LinkJournalRecord(id, link, link.FileName, hasUnreturnedEdits, UnreturnedSteps, keptKnowingly ? true : null,
                                           linkedAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        journal.Write(record, withState ? State : null);
    }

    /// <summary>This document's journal entry as it is on disk, if it has one.</summary>
    public LinkJournal.Entry? JournalEntry => JournalID is { } id ? Journal?.Entries().FirstOrDefault(e => e.Id == id) : null;

    /// <summary>The document no longer has a journal entry (saved back and closed, kept for later, or declined).</summary>
    public void LeaveJournal(bool removingEntry)
    {
        if (JournalID is not { } id) return;
        if (removingEntry) Journal?.Remove(id);
        JournalID = null;
        JournalRoomTrouble = null;
    }

    /// <summary>Closing a linked window. Nothing unreturned: it closes, and its entry is removed. With edits that are not
    /// saved back the person chooses: Save Back, Keep for Later (the entry stays and comes back at the next launch and
    /// under File › Linked Files Waiting…) or Discard (the work copy goes to the Recycle Bin). True when the window may
    /// close now; a Save Back closes it when the file is written.</summary>
    public bool CloseLinkedWindow()
    {
        if (JournalID is null) return true;
        if (!hasUnreturnedEdits)
        {
            LeaveSystemSaving();
            LeaveJournal(removingEntry: true);
            return true;
        }
        var name = linkedImage?.FileName ?? brokenLink?.Link.FileName ?? documentName;
        var requester = linkedImage?.Requester ?? brokenLink?.Link.Requester ?? "";
        var message = UnreturnedSteps == 1
            ? new UiText("{0} has 1 step not saved back to {1}.", name, requester)
            : new UiText("{0} has {1} steps not saved back to {2}.", name, UnreturnedSteps, requester);
        // The Mac's Trash is the Recycle Bin here.
        UiText detail = "Keep for Later closes the window and keeps your edits. They come back the next time Glimmer starts, and you find them in the File menu under Linked Files Waiting. Discard moves them to the Recycle Bin.";
        // The same four choices when the file can't be reached, with the layered copy in place of saving back.
        bool canSaveBack = linkedImage is not null;
        switch (Environment.Host.AskToCloseLinked(this, message, detail, canSaveBack))
        {
            case LinkedCloseChoice.SaveBack when !canSaveBack:
                SaveGlimmerCopy();   // the window stays: the copy is the person's own file, the entry waits as it was
                return false;
            case LinkedCloseChoice.SaveBack:
                SaveLinkedImage(() =>
                {
                    LeaveSystemSaving();
                    LeaveJournal(removingEntry: true);
                    Closing();
                    Environment.Host.CloseDocument(this);
                });
                return false;
            case LinkedCloseChoice.KeepForLater:
                WriteJournal(withState: true, keptKnowingly: true);
                LeaveSystemSaving();
                LeaveJournal(removingEntry: false);
                return true;
            case LinkedCloseChoice.Discard:
                {
                    var entry = JournalEntry;
                    LeaveSystemSaving();
                    LeaveJournal(removingEntry: false);
                    if (entry is not null && Journal?.Discard(entry) == false)
                    {
                        ErrorMessage = "Glimmer couldn't move the edits to the Recycle Bin, so they are kept. You find them in the File menu under Linked Files Waiting.";
                        return false;
                    }
                    return true;
                }
            default:
                return false;
        }
    }

    /// <summary>Why the linked file can't be saved back right now, or null when it can. Looked at on disk each time: the
    /// sibling may remove, restore or replace its file while Glimmer runs.</summary>
    public static UiText? LinkTrouble(string path, string name, string requester)
    {
        if (!File.Exists(path)) return new UiText("{0} is no longer where it was, so Glimmer can't save it back to {1}.", name, requester);
        if (!CanWrite(path)) return new UiText("Glimmer is no longer allowed to write {0}, so it can't save it back to {1}.", name, requester);
        return null;
    }

    /// <summary>The same, in the words an agent is told (linkedFile.reason): English whatever language the window shows.</summary>
    public static string LinkTroubleForAgent(string path, string name, string requester)
    {
        if (!File.Exists(path)) return $"{name} is no longer where it was, so Glimmer can't save it back to {requester}.";
        if (!CanWrite(path)) return $"Glimmer is no longer allowed to write {name}, so it can't save it back to {requester}.";
        return $"Glimmer can't save {name} back to {requester} as it is.";
    }

    private static bool CanWrite(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) return false;
            using var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Brings the link's state in line with the file as it is now. A file that has gone makes the link a broken
    /// one; a file that is back, or has been replaced by a new one at the same place, makes it whole again. Returns true
    /// when the file can be saved to.</summary>
    public bool RefreshLinkedFile()
    {
        if (linkedImage is { } link)
        {
            if (LinkTrouble(link.Path, link.FileName, link.Requester) is not { } trouble) return true;
            var record = Document.LinkRecord ?? new DocumentLink(null, link.Path, link.Requester);
            Environment.Log?.Invoke($"the linked file went away while open: {link.FileName}");
            LinkedImage = null;
            BrokenLink = new BrokenLink(record, trouble.ToString());
            return false;
        }
        if (brokenLink is { } broken)
        {
            var path = broken.Link.Path;
            if (LinkTrouble(path, broken.Link.FileName, broken.Link.Requester) is not null
                || Glimmer.Formats.LinkedImage.Create(path, broken.Link.Requester) is not { } image) return false;
            Environment.Log?.Invoke($"the linked file is back: {image.FileName}");
            BrokenLink = null;
            SetLinkedImage(image);
            return true;
        }
        return false;
    }

    /// <summary>Looks at the linked file once a second while the document is linked (or was, and may be again).</summary>
    public void WatchLinkedFile()
    {
        if (linkWatch is not null) return;
        linkWatch = new RepeatingTimer(Scheduler, TimeSpan.FromSeconds(1), () =>
        {
            if (linkedImage is null && brokenLink is null)
            {
                linkWatch?.Dispose();
                linkWatch = null;
                return;
            }
            RefreshLinkedFile();
            // A linked document never counts as changed for the app's own question, whatever changed it.
            if (JournalID is not null) LeaveSystemSaving();
        });
    }

    /// <summary>After a restart: the document came back from disk with a link. Finds the file again, or says why it
    /// can't; either way the window stays the linked one.</summary>
    public void RestoreLink(LinkJournal.Entry? entry = null)
    {
        if (linkedImage is not null || brokenLink is not null || Document.LinkRecord is not { } record) return;
        // A document the person saved as a .glimmer file of their own is theirs, not the link's.
        if (Document.FilePath is not null)
        {
            Document.LinkRecord = null;
            return;
        }
        var name = record.FileName;
        void Broken(UiText reason)
        {
            Environment.Log?.Invoke($"linked file not restored: {reason}");
            BrokenLink = new BrokenLink(record, reason.ToString());
            SetDocumentName(name);
            AdoptJournal(entry);   // never held back: the edits are shown, and Save a Glimmer Copy… is the way on
            WatchLinkedFile();     // the file may come back while the window is open
        }
        if (!File.Exists(record.Path))
        {
            Broken(new UiText("{0} is no longer where it was, so Glimmer can't save it back to {1}.", name, record.Requester));
            return;
        }
        if (!CanWrite(record.Path))
        {
            Broken(new UiText("Glimmer is no longer allowed to write {0}, so it can't save it back to {1}.", name, record.Requester));
            return;
        }
        if (Glimmer.Formats.LinkedImage.Create(record.Path, record.Requester) is not { } image)
        {
            Broken(new UiText("Glimmer can't write {0} in its format.", name));
            return;
        }
        SetLinkedImage(image);
        WatchLinkedFile();
        SetDocumentName(image.FileName);
        AdoptJournal(entry);
        if (!Equals(Document.LinkRecord, entry?.Record.Link)) WriteJournal(withState: false);   // a new path
        Environment.Log?.Invoke($"linked file restored: {name}");
    }

    /// <summary>Takes the document out of the app's own unsaved-changes question: what is not saved back yet is in the link
    /// journal. Only for a document of no file of its own.</summary>
    public void LeaveSystemSaving()
    {
        if (Document.FilePath is not null) return;
        Document.MarkSaved();
    }

    /// <summary>Save on a document whose link is broken: explain, and offer the layered copy. Never Save As by itself.</summary>
    public void ExplainBrokenLink()
    {
        if (brokenLink is not { } broken) return;
        ErrorMessage = new UiText("{0} {1}", UiText.Verbatim(broken.Reason),
                                  new UiText("Your edit is still here. File › Save a Glimmer Copy… keeps it with its layers; then send the picture to {0} again from there.", broken.Link.Requester));
    }

    /// <summary>File › Save while linked: flatten and write back in place.</summary>
    public void SaveLinkedImage(Action? done = null)
    {
        // The file is looked at now, not as it was a second ago: a file that has gone gets the explanation and the way on
        // (Save a Glimmer Copy…), never a bare write error.
        if (!RefreshLinkedFile())
        {
            ExplainBrokenLink();
            return;
        }
        if (linkedImage is not { } link) return;
        CommitLiveEdit();
        var snapshot = State;
        Scheduler.Background(() =>
        {
            using var flat = ImageExporter.Flatten(snapshot);
            link.Write(flat);
            return true;
        }, _ =>
        {
            MarkSavedWithoutPackage();
            ShowToast(new UiText("Saved {0} for {1}", link.FileName, link.Requester));
            done?.Invoke();
        }, failed: error => ErrorMessage = new UiText("Couldn't save {0}: {1}", link.FileName, UiText.Verbatim(error.Message)));
    }

    /// <summary>The window's document has no unsaved changes now (they're in the linked file), so closing it doesn't ask
    /// to save a .glimmer package.</summary>
    private void MarkSavedWithoutPackage()
    {
        // Everything is in the file now. The entry stays while the window is open (the link survives Quit), and says that
        // nothing waits to be saved back.
        savedBackGeneration = StateGeneration;
        HasUnreturnedEdits = false;
        UnreturnedSteps = 0;
        Recovered = false;
        KeptFromLastTime = false;
        WriteJournal(withState: true);
        LeaveSystemSaving();
    }

    /// <summary>File › Save a Glimmer Copy…: the layered document as a .glimmer next to the image, if the user wants one.</summary>
    public void SaveGlimmerCopy()
    {
        string source;
        if (linkedImage is { } link) source = link.Path;
        else if (brokenLink is { } broken) source = broken.Link.Path;
        else return;
        UiText message = linkedImage is not null
            ? new UiText("The layers stay editable in the copy; {0} is still what Save writes.", Path.GetFileName(source))
            : "The layers stay editable in the copy.";
        var path = Environment.Dialogs.ChooseSaveLocation(Path.GetFileNameWithoutExtension(source) + ".glimmer", ["glimmer"], message);
        if (path is null) return;
        var snapshot = State;
        Scheduler.Background(() =>
        {
            GlimmerPackage.Save(snapshot, path);
            return true;
        }, _ => ShowToast(new UiText("Saved {0}", Path.GetFileName(path))), failed: error => ErrorMessage = PersonMessage.Text(error));
    }
}
