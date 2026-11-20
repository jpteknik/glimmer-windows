// Ported from App/GlimmerDocument.swift
using Glimmer.Core;
using Glimmer.Formats;
using SkiaSharp;

namespace Glimmer.Editor;

/// <summary>A <c>.glimmer</c> document. Images are imported into new documents by <see cref="DocumentOpener"/>, never
/// opened in place (a save would overwrite them).</summary>
/// <remarks>
/// Every edit goes through <see cref="Edit"/>. The Mac's SwiftUI uses the undo stack to detect unsaved changes, so
/// autosave depends on it; here the same count of steps drives <see cref="HasUnsavedChanges"/>, explicit Save and Save
/// As write the file, and the session keeps a recovery copy of a document with unsaved changes.
///
/// The undo manager is the document's own (the Mac's is the window's). Registrations made while handling one event
/// make one undo step, as AppKit groups them by event: the first registration opens a group, and the group is closed
/// when the UI thread is next idle (a post to <see cref="IUiScheduler"/>), as AppKit closes it at the end of the event.
/// </remarks>
public sealed class GlimmerDocument : ObservableObject, IEditorDocument, IDisposable
{
    private readonly EditorEnvironment environment;
    private DocumentState state;

    public GlimmerDocument(EditorEnvironment environment, DocumentState? state = null, string? filePath = null)
    {
        this.environment = environment;
        this.state = state ?? DocumentState.New(1920, 1080, DocumentState.Background.White);
        this.filePath = filePath;
        spillDirectory = Path.Combine(environment.TemporaryFolder, UndoSpillStore.FolderName());
        UndoManager.LevelsOfUndo = HistoryLimit;
    }

    public DocumentState State
    {
        get => state;
        set
        {
            if (ReferenceEquals(state, value)) return;
            state = value;
            Raise();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised on the UI thread after every change of <see cref="State"/> (Swift's observation of document.state).</summary>
    public event EventHandler? StateChanged;

    public UndoManager UndoManager { get; } = new();

    /// <summary>Saving re-encodes only the layers that changed. The cache may use an eighth of the memory, so a big
    /// document's unchanged layers stay encoded between saves.</summary>
    public EncodedLayerCache EncodedLayers { get; } = new(byteLimit: DocumentLimits.PhysicalMemory / 8);

    // MARK: History

    private readonly List<UiText> history = [];
    private int historyIndex;

    /// <summary>Names of the undoable steps since the document was opened (History panel).</summary>
    public IReadOnlyList<UiText> History => history;

    /// <summary>How many of <see cref="History"/> are applied (the rest can be redone).</summary>
    public int HistoryIndex => historyIndex;

    public bool CanUndo => UndoManager.CanUndo;
    public bool CanRedo => UndoManager.CanRedo;

    /// <summary>Settings › General › History states ("history.limit", default 100).</summary>
    public int HistoryLimit
    {
        get
        {
            int v = environment.Settings.Get(Preferences.HistoryLimitKey, 0);
            return v > 0 ? v : 100;
        }
    }

    /// <summary>Memory the undo history may hold on to, on top of the step limit ("history.memoryBudgetMB", default 2 GB).</summary>
    public long HistoryBudgetBytes
    {
        get
        {
#if DEBUG
            if (System.Environment.GetEnvironmentVariable("GLIMMER_HISTORY_BUDGET_MB") is { } mb && long.TryParse(mb, out var m)) return m * 1_048_576;
#endif
            long v = environment.Settings.Get(Preferences.HistoryBudgetKey, 0L);
            return (v > 0 ? v : 2048) * 1_048_576;
        }
    }

    /// <summary>Bytes each applied step keeps alive: the tiles it replaced (index-aligned with history).</summary>
    private readonly List<long> stepCosts = [];
    public long HistoryBytes => stepCosts.Take(historyIndex).Sum();

    /// <summary>The snapshot each step restores when undone (index-aligned with history). Older ones move to disk when
    /// the history outgrows its memory budget.</summary>
    private readonly List<UndoSnapshot> snapshots = [];
    private readonly HashSet<UndoSnapshot> spilling = new(ReferenceEqualityComparer.Instance);
    private readonly EncodedLayerCache spillCache = new();
    private readonly string spillDirectory;

    /// <summary>The folder this document's undo steps are spilled to.</summary>
    public string SpillDirectory => spillDirectory;

    private int stepsOnDisk;
    /// <summary>How many undo steps currently live on disk (shown in the History panel).</summary>
    public int StepsOnDisk
    {
        get => stepsOnDisk;
        private set => Set(ref stepsOnDisk, value);
    }

    private UiText? diskNotice;
    /// <summary>Set when the disk was too full to keep the whole undo history, or the document outgrew the memory: what
    /// happened, for the person. The window shows it once and clears it.</summary>
    public UiText? DiskNotice
    {
        get => diskNotice;
        set => Set(ref diskNotice, value);
    }

    private bool saidDiskIsLow, saidDocumentIsLarge;

    /// <summary>Says once, without stopping anything, that the document has outgrown this computer's memory.</summary>
    public void NoteSize()
    {
        if (saidDocumentIsLarge || DocumentLimits.ComfortNotice(state.TextureTileIDs.Count, state.KeptRawBytes) is not { } notice) return;
        saidDocumentIsLarge = true;
        DiskNotice = PersonMessage.Text(notice);
    }

    /// <summary>Called after every step (an edit, an undo, a redo): a linked document writes its journal entry then.</summary>
    public event Action? Step;

    /// <summary>The file of a sibling app this document is an edit of (the link journal's record; a document read from
    /// disk may carry one).</summary>
    public DocumentLink? LinkRecord { get; set; }

    /// <summary>Called when a document that was read carries a link.</summary>
    public event Action? LinkRead;

    // MARK: Transactions

    /// <summary>While set, edits don't register their own undo steps; EndTransaction registers one step for all of them
    /// (an assistant plan undoes as a whole).</summary>
    private (UiText Name, DocumentState Before, bool Changed)? transaction;

    public void BeginTransaction(UiText actionName)
    {
        if (transaction is not null) return;
        transaction = (actionName, state, false);
    }

    /// <summary>Transactions (agent steps, assistant plans, saved actions) end outside a user event, so each is an undo
    /// step of its own; otherwise quick requests merged into one.</summary>
    public void EndTransaction()
    {
        if (transaction is not { } t) return;
        transaction = null;
        if (!t.Changed) return;
        UndoManager.SeparateStep(() => RegisterUndo(t.Name, t.Before));
    }

    public bool IsInTransaction => transaction is not null;

    // MARK: Editing

    /// <summary>Applies an undoable change. Snapshots are cheap (tiles are shared), so undo simply restores the previous
    /// state. <paramref name="separateStep"/>: for changes made outside a user event (an AI job finishing, the agent API),
    /// which must be undo steps of their own (UndoManager.SeparateStep).</summary>
    public void Edit(UiText actionName, Func<DocumentState, DocumentState> change, bool separateStep = false)
    {
        var before = state;
        var after = change(state);
        State = after;
        if (separateStep) UndoManager.SeparateStep(() => RegisterUndo(actionName, before));
        else RegisterUndo(actionName, before);
    }

    /// <summary>Records an undo step for a change that was applied interactively (e.g. a slider drag), restoring
    /// <paramref name="before"/>.</summary>
    public void CommitInteractiveEdit(UiText actionName, DocumentState before) => RegisterUndo(actionName, before);

    /// <summary>Net count of steps since the document was opened (+1 per step, −1 per undo, +1 per redo), and its value
    /// when the document was last saved (NSDocument's change count).</summary>
    private long changeCount, savedChangeCount;
    private bool closePosted;

    public bool HasUnsavedChanges => changeCount != savedChangeCount;

    private void RegisterUndo(UiText actionName, DocumentState snapshot)
    {
        var undoManager = UndoManager;
        if (transaction is not null && !undoManager.IsUndoing && !undoManager.IsRedoing)
        {
            transaction = transaction.Value with { Changed = true };
            return;
        }
        bool wasDirty = HasUnsavedChanges;
        // Snapshots share tiles, so a step costs only the tiles it changed.
        var box = new UndoSnapshot(snapshot);
        if (undoManager.IsUndoing)
        {
            // `box` is the redo snapshot; the undo stack below it is unchanged.
            historyIndex = Math.Max(historyIndex - 1, 0);
            changeCount--;
        }
        else if (undoManager.IsRedoing)
        {
            if (historyIndex < snapshots.Count)
            {
                snapshots[historyIndex].Dispose();
                snapshots[historyIndex] = box;
                stepCosts[historyIndex] = Cost(snapshot, state);
            }
            historyIndex = Math.Min(historyIndex + 1, history.Count);
            changeCount++;
        }
        else
        {
            RemoveSteps(historyIndex, history.Count - historyIndex);
            history.Add(actionName);
            stepCosts.Add(Cost(snapshot, state));
            snapshots.Add(box);
            int keep = HistoryLimit;
            if (undoManager.LevelsOfUndo != keep) undoManager.LevelsOfUndo = keep;
            if (history.Count > keep) RemoveSteps(0, history.Count - keep);
            historyIndex = history.Count;
            changeCount++;
        }
        SpillIfNeeded();
        NoteSize();
        var name = actionName;
        undoManager.RegisterUndo(this, document =>
        {
            var current = document.state;
            // A snapshot that can't be read back leaves the document as it is.
            DocumentState? restored;
            try { restored = box.State(); }
            catch { restored = null; }
            if (restored is not null) document.State = restored;
            else AppSound.Beep(document.environment);
            document.RegisterUndo(name, current);
        });
        undoManager.SetActionName(actionName.ToString());
        // AppKit closes the group of an event at the end of the event: here when the UI thread is next idle.
        if (undoManager.GroupingLevel > 0 && !undoManager.IsUndoing && !undoManager.IsRedoing && !closePosted)
        {
            closePosted = true;
            environment.Scheduler.Post(() =>
            {
                closePosted = false;
                UndoManager.CloseEventGroup();
                Raise(nameof(CanUndo), nameof(CanRedo));
            });
        }
        Raise(nameof(History), nameof(HistoryIndex), nameof(CanUndo), nameof(CanRedo));
        if (wasDirty != HasUnsavedChanges) Raise(nameof(HasUnsavedChanges));
        Step?.Invoke();
    }

    /// <summary>Drops steps (and lets go of their snapshots and their files on disk).</summary>
    private void RemoveSteps(int start, int count)
    {
        if (count <= 0) return;
        for (int i = start; i < start + count && i < snapshots.Count; i++) snapshots[i].Dispose();
        history.RemoveRange(start, Math.Min(count, history.Count - start));
        if (start < stepCosts.Count) stepCosts.RemoveRange(start, Math.Min(count, stepCosts.Count - start));
        if (start < snapshots.Count) snapshots.RemoveRange(start, Math.Min(count, snapshots.Count - start));
    }

    /// <summary>Moves the oldest in-memory snapshots to disk until the rest fit the memory budget.</summary>
    private void SpillIfNeeded()
    {
        long inMemory = 0;
        for (int i = 0; i < snapshots.Count; i++)
            if (!snapshots[i].IsOnDisk && !spilling.Contains(snapshots[i])) inMemory += stepCosts[i];
        int index = 0;
        // Space already promised to snapshots that are being written.
        long promised = snapshots.Where(spilling.Contains).Sum(s => s.DiskBytesUpperBound);
        long budget = HistoryBudgetBytes;
        // The newest step always stays in memory, whatever it costs.
        while (inMemory > budget && index < snapshots.Count - 1)
        {
            var box = snapshots[index];
            if (!box.IsOnDisk && !spilling.Contains(box))
            {
                // Never fill the disk with undo steps: when there isn't room, forget the oldest instead.
                long bytes = box.DiskBytesUpperBound;
                if (!UndoSpillPolicy.CanSpill(bytes + promised, UndoSpillPolicy.FreeBytes(FreeSpaceProbe())))
                {
                    ForgetOldestSteps();
                    break;
                }
                promised += bytes;
                inMemory -= stepCosts[index];
                spilling.Add(box);
                var directory = spillDirectory;
                var cache = spillCache;
                bool failed = false;
                environment.Scheduler.RunInBackground(() =>
                {
                    try { box.MoveToDisk(directory, cache); }
                    catch { failed = true; }
                }, () =>
                {
                    spilling.Remove(box);
                    StepsOnDisk = snapshots.Count(s => s.IsOnDisk);
                    // The write failed (the disk filled up meanwhile): the step is still in memory.
                    if (failed) ForgetOldestSteps();
                });
            }
            index++;
        }
        StepsOnDisk = snapshots.Count(s => s.IsOnDisk);
    }

    /// <summary>A path on the spill folder's volume that exists (the folder is made on the first spill).</summary>
    private string FreeSpaceProbe() => Directory.Exists(spillDirectory) ? spillDirectory : environment.TemporaryFolder;

    /// <summary>The disk has no room for older undo steps: drops the oldest ones until what is kept in memory fits the
    /// budget, and says so once. The document itself is untouched.</summary>
    private void ForgetOldestSteps()
    {
        if (UndoManager.IsUndoing || UndoManager.IsRedoing) return;
        int applied = Math.Min(historyIndex, snapshots.Count);
        var costs = Enumerable.Range(0, applied).Select(i => snapshots[i].IsOnDisk || spilling.Contains(snapshots[i]) ? 0 : stepCosts[i]).ToList();
        int forget = UndoSpillPolicy.StepsToForget(costs, HistoryBudgetBytes);
        if (forget <= 0) return;
        RemoveSteps(0, forget);
        historyIndex -= forget;
        // The undo manager drops its oldest steps when its limit goes below what it holds.
        UndoManager.LevelsOfUndo = Math.Max(historyIndex, 1);
        StepsOnDisk = snapshots.Count(s => s.IsOnDisk);
        Raise(nameof(History), nameof(HistoryIndex), nameof(CanUndo));
        if (!saidDiskIsLow)
        {
            saidDiskIsLow = true;
            DiskNotice = "Your disk is almost full, so Glimmer keeps fewer undo steps for this document. The document itself is safe. Free up space to get the full history back for new changes.";
        }
    }

    /// <summary>Memory an undo snapshot holds beyond the current state: the texture tiles only it references.</summary>
    public static long Cost(DocumentState snapshot, DocumentState current)
    {
        var now = current.TextureTileIDs;
        return snapshot.TextureTileIDs.Count(id => !now.Contains(id)) * (long)TileGeometry.ByteCount;
    }

    /// <summary>Steps back or forward to <paramref name="index"/> applied steps (History panel clicks).</summary>
    public void JumpToHistory(int index)
    {
        while (historyIndex > index && UndoManager.CanUndo) Undo();
        while (historyIndex < index && UndoManager.CanRedo) Redo();
    }

    public void Undo()
    {
        if (!UndoManager.CanUndo) return;
        UndoManager.Undo();
        Raise(nameof(CanUndo), nameof(CanRedo));
    }

    public void Redo()
    {
        if (!UndoManager.CanRedo) return;
        UndoManager.Redo();
        Raise(nameof(CanUndo), nameof(CanRedo));
    }

    // MARK: Reading and writing (Windows: explicit Save and Save As; the Mac's NSDocument autosaves in place)

    private string? filePath;
    /// <summary>The .glimmer file this document was opened from or saved to; null while untitled.</summary>
    public string? FilePath
    {
        get => filePath;
        private set => Set(ref filePath, value);
    }

    /// <summary>Opens a .glimmer document (a ZIP made on Windows, a package folder from a Mac, or a Finder-compressed one).</summary>
    public static GlimmerDocument Open(EditorEnvironment environment, string path)
    {
        var read = GlimmerPackage.Load(path);
        var document = new GlimmerDocument(environment, read with { Link = null }, path) { LinkRecord = read.Link };
        document.NoteSize();
        return document;
    }

    /// <summary>A document read from disk carried its link: tell whoever restores the link (the session).</summary>
    internal void AnnounceLink()
    {
        if (LinkRecord is not null) LinkRead?.Invoke();
    }

    /// <summary>What a save writes: the package's files, with the previews the Mac's Finder and QuickLook read.</summary>
    internal PackageContents Contents(DocumentState snapshot) => GlimmerPackage.Contents(snapshot, EncodedLayers, QuickLookImages(snapshot));

    /// <summary>Writes the document to <paramref name="path"/> (a .glimmer file, written atomically: a failed save leaves
    /// nothing half-written behind and an earlier version as it was). Off the UI thread; <paramref name="finished"/> gets
    /// null when it worked, or the error, on the UI thread.</summary>
    /// <remarks>Writes the package itself so that a full disk is noticed before anything is written, nothing
    /// half-written is left behind, and the person is told: a save that fails silently looks like a save that worked.</remarks>
    public void Save(string path, Action<Exception?>? finished = null)
    {
        var snapshot = state;
        long count = changeCount;
        Exception? error = null;
        environment.Scheduler.RunInBackground(() =>
        {
            try { DocumentSaver.Write(Contents(snapshot), path); }
            catch (Exception e) { error = e; }
        }, () =>
        {
            if (error is null)
            {
                bool wasDirty = HasUnsavedChanges;
                FilePath = path;
                savedChangeCount = count;
                if (wasDirty != HasUnsavedChanges) Raise(nameof(HasUnsavedChanges));
                Saved?.Invoke(path);
            }
            finished?.Invoke(error);
        });
    }

    /// <summary>Writes the document now, on this thread (a tab that is closing); throws when it can't.</summary>
    internal void SaveNow(string path)
    {
        DocumentSaver.Write(Contents(state), path);
        bool wasDirty = HasUnsavedChanges;
        FilePath = path;
        savedChangeCount = changeCount;
        if (wasDirty != HasUnsavedChanges) Raise(nameof(HasUnsavedChanges));
        Saved?.Invoke(path);
    }

    /// <summary>Raised on the UI thread after a save worked (the path saved to).</summary>
    public event Action<string>? Saved;

    /// <summary>"Save a Copy for Mac…": the same content as a package folder (<c>name.glimmer</c> as a directory, which a
    /// Mac opens as a document). The document keeps its own file and its unsaved state.</summary>
    public void SaveCopyAsPackageFolder(string path, Action<Exception?>? finished = null)
    {
        var snapshot = state;
        Exception? error = null;
        environment.Scheduler.RunInBackground(() =>
        {
            try { WritePackageFolder(Contents(snapshot), path); }
            catch (Exception e) { error = e; }
        }, () => finished?.Invoke(error));
    }

    /// <summary>Writes a package folder beside <paramref name="destination"/> and moves it into place, so a failure never
    /// leaves half a document (the Mac's FileWrapper.write(to:options: .atomic)).</summary>
    internal static void WritePackageFolder(PackageContents contents, string destination)
    {
        destination = Path.GetFullPath(destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var folder = Path.GetDirectoryName(destination) ?? ".";
        long needed = contents.ByteCount + 2 * 1_048_576;
        if (UndoSpillPolicy.FreeBytes(folder) is { } free && free < needed)
            throw new DocumentSaveException(needed, free, Path.GetPathRoot(folder) is { Length: > 0 } root ? root : "the disk");
        var partial = Path.Combine(folder, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.saving");
        try
        {
            contents.WriteDirectory(partial);
            string? aside = null;
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                aside = $"{destination}.{Guid.NewGuid():N}.previous";
                if (Directory.Exists(destination)) Directory.Move(destination, aside); else File.Move(destination, aside);
            }
            try
            {
                Directory.Move(partial, destination);
            }
            catch
            {
                if (aside is not null) { if (Directory.Exists(aside)) Directory.Move(aside, destination); else File.Move(aside, destination); }
                throw;
            }
            if (aside is not null)
            {
                try { if (Directory.Exists(aside)) Directory.Delete(aside, true); else File.Delete(aside); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch
        {
            try { if (Directory.Exists(partial)) Directory.Delete(partial, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>PNG thumbnail (512 px) and preview (1600 px) for the Mac's Finder and QuickLook.</summary>
    public static (byte[] Thumbnail, byte[] Preview)? QuickLookImages(DocumentState state)
    {
        try
        {
            using var full = ImageExporter.FlattenedSRGB(state);
            using var preview = Downscaled(full, 1600);
            using var thumb = Downscaled(preview, 512);
            static byte[] Png(SKBitmap image)
            {
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                return data.ToArray();
            }
            return (Png(thumb), Png(preview));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A copy no larger than <paramref name="maxSide"/> on its longer side (EditorSession.downscaled).</summary>
    public static SKBitmap Downscaled(SKBitmap image, int maxSide)
    {
        double scale = maxSide / (double)Math.Max(image.Width, image.Height);
        if (scale >= 1) return image.Copy();
        int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        return image.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? image.Copy();
    }

    /// <summary>The document matches what is kept for it elsewhere (a linked document's journal): no unsaved changes.</summary>
    internal void MarkSaved()
    {
        bool wasDirty = HasUnsavedChanges;
        savedChangeCount = changeCount;
        if (wasDirty != HasUnsavedChanges) Raise(nameof(HasUnsavedChanges));
    }

    /// <summary>The document counts as changed (a recovered copy: what it holds isn't in any file).</summary>
    internal void MarkUnsaved()
    {
        bool wasDirty = HasUnsavedChanges;
        savedChangeCount = changeCount - 1;
        if (wasDirty != HasUnsavedChanges) Raise(nameof(HasUnsavedChanges));
    }

    /// <summary>A counter that moves with every step (the recovery copy is taken again only when it moved).</summary>
    internal long ChangeCount => changeCount;

    // MARK: Closing

    private bool disposed;

    /// <summary>Lets go of the undo history and its files on disk (the Mac's deinit).</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        UndoManager.RemoveAllActions();
        foreach (var s in snapshots) s.Dispose();
        snapshots.Clear();
        try { if (Directory.Exists(spillDirectory)) Directory.Delete(spillDirectory, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
