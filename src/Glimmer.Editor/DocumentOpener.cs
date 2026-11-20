// Ported from App/DocumentOpener.swift (DocumentOpener, and the launch work of its AppDelegate)
//
// The Mac's windows come from SwiftUI's DocumentGroup and NSDocumentController, and a window "claims" its link or its
// import report when it appears. Here the opener makes the document and its session itself, settles what the window
// would have claimed, and hands the session to the app (IDocumentHost.ShowDocument) for a new tab.
using Glimmer.Core;
using Glimmer.Formats;

namespace Glimmer.Editor;

/// <summary>Creates new documents and opens files.</summary>
/// <remarks>Images are never opened as documents: a save would overwrite the original with a .glimmer package. Instead
/// an image becomes a new, untitled document with the image as its first layer.</remarks>
public sealed class DocumentOpener : IDocumentOpener
{
    private readonly List<EditorSession> sessions = [];

    public DocumentOpener(EditorEnvironment environment)
    {
        Environment = environment;
        Journal = new LinkJournal(environment);
        // A step that does not fit on the disk is said in the window it belongs to.
        Journal.OnRoom = (id, trouble) => sessions.FirstOrDefault(s => s.JournalID == id)?.JournalRoom(trouble);
        LinkedFilesWaiting = new WaitingLinks(this);
        environment.Opener = this;
    }

    public EditorEnvironment Environment { get; }
    public LinkJournal Journal { get; }

    /// <summary>File › Linked Files Waiting… (LinkedWaitingView's model).</summary>
    public ILinkedFilesWaiting LinkedFilesWaiting { get; }

    /// <summary>The open documents' sessions, oldest first (the Mac's AgentRegistry.shared.sessions).</summary>
    public IReadOnlyList<EditorSession> Sessions => sessions;

    /// <summary>The session in the front tab (AgentRegistry.markFront): the app says which tab is active.</summary>
    public EditorSession? FrontSession { get; private set; }

    public void MarkFront(EditorSession session)
    {
        if (sessions.Contains(session)) FrontSession = session;
    }

    // MARK: Launch (AppDelegate.applicationWillFinishLaunching / applicationDidFinishLaunching)

    /// <summary>At launch: undo steps a crashed or killed Glimmer left on disk are only wasted space; linked documents
    /// with edits that were not saved back come back. Returns the documents a stop left unsaved, for the app to offer.</summary>
    public IReadOnlyList<RecoveryStore.Entry> Launch(bool keepWindows = false)
    {
        int orphans = UndoSpillStore.RemoveOrphans(Environment.TemporaryFolder);
        if (orphans > 0) Environment.Log?.Invoke($"removed {orphans} leftover undo folder(s)");
        OpenJournalAtLaunch(keepWindows);
        return RecoverableDocuments();
    }

    /// <summary>Documents that had unsaved changes when Glimmer stopped without saving or closing them, newest first.</summary>
    public IReadOnlyList<RecoveryStore.Entry> RecoverableDocuments() => RecoveryStore.Entries(Environment.RecoveryFolder);

    /// <summary>Opens a recovery copy as a document with unsaved changes; Save writes the file it was opened from, when
    /// it had one. The copy stays until the document is saved or closed.</summary>
    public EditorSession OpenRecovered(RecoveryStore.Entry entry)
    {
        var state = RecoveryStore.Read(entry);
        var document = new GlimmerDocument(Environment, state with { Link = null }, entry.Info.OriginalPath);
        var session = Register(document);
        session.AdoptRecovery(entry.Id);
        document.MarkUnsaved();
        session.SetDocumentName(entry.Info.Name);
        Environment.Host.ShowDocument(session);
        return session;
    }

    /// <summary>The person declined a recovery copy.</summary>
    public void DiscardRecovered(RecoveryStore.Entry entry) => RecoveryStore.Remove(Environment.RecoveryFolder, entry.Id);

    // MARK: Documents

    /// <summary>File › New…: the app's New Document window.</summary>
    public void ShowNewDocumentWindow() => Environment.Host.ShowNewDocumentWindow();

    /// <summary>Opens a new untitled document window with <paramref name="state"/>.</summary>
    public EditorSession CreateDocument(DocumentState state) => CreateDocument(state, link: null, journal: null, showImportReport: false);

    private EditorSession CreateDocument(DocumentState state, LinkedImage? link, LinkJournal.Entry? journal, bool showImportReport)
    {
        var document = new GlimmerDocument(Environment, state);
        document.NoteSize();
        var session = Register(document);
        Environment.Host.ShowDocument(session);
        // What the Mac's window claims when it appears (claimLink, claimImportReport).
        if (journal is not null)
        {
            // From the link journal: the record holds the link, the document is the work copy.
            session.JournalID = journal.Id;
            document.LinkRecord = journal.Record.Link;
            session.RestoreLink(journal);
        }
        else if (link is not null)
        {
            session.Link(link);
            session.SetDocumentName(link.FileName);
            BringForward(session);
        }
#if GLIMMER_PSD
        if (showImportReport && session.ImportReport is not null) session.IsImportReportPresented = true;
#endif
        return session;
    }

    /// <summary>Makes a session for <paramref name="document"/> and keeps it in Sessions until it closes.</summary>
    private EditorSession Register(GlimmerDocument document)
    {
        var session = new EditorSession(document, Environment) { Journal = Journal };
        sessions.Add(session);
        session.Closed += (_, _) =>
        {
            sessions.Remove(session);
            if (FrontSession == session) FrontSession = null;
        };
        return session;
    }

    /// <summary>The app that handed an image over expects Glimmer in front with that document.</summary>
    private void BringForward(EditorSession session)
    {
        MarkFront(session);
        Environment.Host.BringForward(session);
    }

    /// <summary>Opens files: .glimmer documents, projects of Glimmer for the web, and images (as new documents).
    /// <paramref name="requester"/> is the sibling app that handed the files over (Flimmer, Jämmer): its PNG or JPEG is
    /// then edited in linked mode, where Save writes back into the same file.</summary>
    void IDocumentOpener.Open(IReadOnlyList<string> paths) => Open(paths);

    public void Open(IEnumerable<string> paths, string? requester = null)
    {
        foreach (var path in paths)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".glimmer")
            {
                OpenGlimmer(path);
            }
            else if (ext == ".aips")
            {
                OpenAips(path);
            }
            else if (requester is not null && sessions.FirstOrDefault(s => Same(s.LinkedImage?.Path, path) || Same(s.BrokenLink?.Link.Path, path)) is { } existing)
            {
                // The file is already being edited here (perhaps in a window restored after a restart, or one whose file
                // had gone and is back): show that window with its work, rather than a second one of the same file.
                existing.RefreshLinkedFile();
                BringForward(existing);
            }
            else if (requester is not null && Journal.EntryFor(path) is { } entry)
            {
                if (entry.Record.Unreturned)
                {
                    // Glimmer stopped, or the window was closed with Keep for Later, while this file had edits that were
                    // not saved back: open that work, with its layers, instead of the file as it is on disk.
                    Environment.Log?.Invoke($"reopening the journal entry for {Path.GetFileName(path)}");
                    if (!Open(entry)) OpenImage(path, Glimmer.Formats.LinkedImage.Create(path, requester));
                }
                else
                {
                    // Nothing was waiting: the file as its owner has it now is what is edited.
                    Journal.Remove(entry.Id);
                    OpenImage(path, Glimmer.Formats.LinkedImage.Create(path, requester));
                }
            }
            else
            {
                OpenImage(path, requester is null ? null : Glimmer.Formats.LinkedImage.Create(path, requester));
            }
        }
    }

    private static bool Same(string? a, string b) => a is not null && LinkJournal.Canonical(a) == LinkJournal.Canonical(b);

    /// <summary>A .glimmer document opens as itself (its own file); an open one is brought forward.</summary>
    private void OpenGlimmer(string path)
    {
        if (sessions.FirstOrDefault(s => Same(s.Document.FilePath, path)) is { } open)
        {
            BringForward(open);
            return;
        }
        try
        {
            var document = GlimmerDocument.Open(Environment, path);
            var session = Register(document);
            session.DocumentName = Path.GetFileNameWithoutExtension(path);
            Environment.Host.ShowDocument(session);
            // A document read from disk may carry its link.
            document.AnnounceLink();
            Environment.Host.NoteRecentDocument(path);
        }
        catch (Exception error)
        {
            Environment.Host.PresentError(PersonMessage.Text(error));
        }
    }

    // MARK: The link journal at launch and on request

    private bool IsOpen(LinkJournal.Entry entry) => sessions.Any(s => s.JournalID == entry.Id);

    /// <summary>Entries that are not open in a window now (File › Linked Files Waiting…).</summary>
    public IReadOnlyList<LinkJournal.Entry> WaitingEntries() => [.. Journal.Entries().Where(e => !IsOpen(e))];

    /// <summary>Opens an entry as a linked document, from its work copy. False when the work copy can't be read: the entry
    /// is then left where it is and reported, never removed.</summary>
    public bool Open(LinkJournal.Entry entry)
    {
        if (IsOpen(entry)) return true;
        try
        {
            var state = LinkJournal.StateOf(entry);
            if (state.Layers.IsEmpty) throw new InvalidDataException("The work copy has no layers.");
            CreateDocument(state, null, entry, false);
            return true;
        }
        catch (Exception error)
        {
            Environment.Log?.Invoke($"a journal entry's work copy can't be read; it is left where it is: {entry.Id}: {error.Message}");
            Journal.NoteUnreadable(entry.Id);
            return false;
        }
    }

    /// <summary>At launch (SIBLINGS.md §6.6): every entry with edits that were not saved back is opened, linked and with
    /// its edits. An entry with nothing waiting is opened when windows are kept at Quit, removed when not.</summary>
    public void OpenJournalAtLaunch(bool keepWindows = false)
    {
        foreach (var entry in Journal.Entries())
        {
            if (IsOpen(entry)) continue;
            if (entry.Record.Unreturned || keepWindows) Open(entry);
            else Journal.Remove(entry.Id);
        }
    }

    /// <summary>Quit never asks about a linked document (SIBLINGS.md §6.6): its edits are in the link journal and come
    /// back at the next launch. Left knowingly, so the next launch says "Kept from last time". True when the journal has
    /// finished writing (the app waits up to ten seconds for it, calling again).</summary>
    public bool PrepareToQuit()
    {
        foreach (var session in sessions.Where(s => s.JournalID is not null && s.HasUnreturnedEdits))
            session.WriteJournal(withState: false, keptKnowingly: true);
        return Journal.IsIdle;
    }

    // MARK: Photoshop files

#if GLIMMER_PSD
    /// <summary>Whether these bytes are a Photoshop file (by its signature, not its name).</summary>
    public static bool IsPhotoshopFile(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual("8BPS"u8);

    /// <summary>The document for a Photoshop file, with its report in it: what came through, and how far Glimmer's picture
    /// of the layers is from the picture the file carries. The window that gets this document shows the report.</summary>
    public static DocumentState ImportPhotoshopFile(byte[] data)
    {
        var (state, report) = PSDImport.Document(data);
        if (state.Layers.IsEmpty) throw new PSDFile.Problem("This Photoshop file has nothing in it that Glimmer can show.");
        if (!report.Flattened && PSDAppearance.Difference(state, data) is { } difference)
            report = report with { MeanDifference = difference.Mean, ShareVisiblyDifferent = difference.ShareVisible };
        return state with { ImportReport = report };
    }

    /// <summary>A Photoshop file opens as a new, untitled document with its layers.</summary>
    private void OpenPhotoshopFile(byte[] data, string path)
    {
        CreateDocument(ImportPhotoshopFile(data), null, null, showImportReport: true);
        Environment.Host.NoteRecentDocument(path);
    }
#endif

    // MARK: RAW files

#if GLIMMER_RAW
    /// <summary>The extensions of cameras' RAW files (the Mac asks UTType whether the type conforms to .rawImage).</summary>
    public static readonly IReadOnlySet<string> RawExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".3fr", ".ari", ".arw", ".bay", ".cr2", ".cr3", ".crw", ".dcr", ".dcs", ".dng", ".drf", ".eip", ".erf", ".fff", ".iiq", ".k25",
        ".kdc", ".mdc", ".mef", ".mos", ".mrw", ".nef", ".nrw", ".orf", ".pef", ".ptx", ".pxn", ".r3d", ".raf", ".raw", ".rw2", ".rwl",
        ".rwz", ".sr2", ".srf", ".srw", ".x3f",
    };

    /// <summary>Whether the file's name says it is a camera's RAW file (the decoder decides whether it can be developed).</summary>
    public static bool IsRawFile(string path) => RawExtensions.Contains(Path.GetExtension(path));

    /// <summary>A RAW file opens as a new document with one layer that keeps the file, so it can be developed again.
    /// Developing takes seconds, so it runs away from the UI thread while a small window says what is happening.
    /// <paramref name="finished"/> gets null when the document is open, or why it is not.</summary>
    public void OpenRawFile(byte[] data, string fileName, Action<Exception?> finished)
    {
        var progress = Environment.Host.ShowProgress("Develop RAW", new UiText("Developing {0}…", fileName));
        Environment.Scheduler.Background(() => RawLayer.Make(data, fileName), made =>
        {
            progress.Dispose();
            CreateDocument(new DocumentState(made.Width, made.Height, layers: [made.Layer]));
            finished(null);
        }, failed: error =>
        {
            progress.Dispose();
            finished(error);
        });
    }
#endif

    // MARK: Images and projects

    private void OpenImage(string path, LinkedImage? link = null, bool asPlainPicture = false)
    {
        try
        {
#if GLIMMER_PSD
            // A Photoshop file comes in with its layers. (A file handed over by a sibling to edit and save back stays the
            // picture it is: SIBLINGS.md §6 has no PSD.)
            if (link is null && StartsWithPhotoshopSignature(path))
            {
                OpenPhotoshopFile(File.ReadAllBytes(path), path);
                return;
            }
#endif
#if GLIMMER_RAW
            // A camera's RAW file keeps its RAW in the layer (Layer › Develop…). A file handed over by a sibling stays a
            // plain picture, as for Photoshop files.
            if (link is null && !asPlainPicture && IsRawFile(path))
            {
                OpenRawFile(File.ReadAllBytes(path), Path.GetFileName(path), error =>
                {
                    if (error is null)
                    {
                        Environment.Host.NoteRecentDocument(path);
                        return;
                    }
                    // The decoder gives no picture: the general reader may still have one (a preview stored in the
                    // file), which opens as an ordinary picture. If not, the RAW reason is the one to show.
                    bool readable;
                    try { _ = ImageImporter.Image(path); readable = true; }
                    catch { readable = false; }
                    if (error is RawDevelopment.Problem && readable) OpenImage(path, asPlainPicture: true);
                    else
                    {
                        Environment.Log?.Invoke($"couldn't develop {Path.GetFileName(path)}: {error.Message}");
                        Environment.Host.PresentError(PersonMessage.Text(error));
                    }
                });
                return;
            }
#endif
            var image = ImageImporter.Image(path);
            var name = Path.GetFileNameWithoutExtension(path);
            // An empty, untouched document (the one Glimmer opens at launch) becomes the image, instead of leaving a
            // blank window behind.
            if (sessions.FirstOrDefault(s => s.IsUntouchedBlank && !s.MadeByAPI && s.Document.FilePath is null) is { } blank)
            {
                blank.Place(image, name);
                if (link is not null)
                {
                    blank.Link(link);
                    blank.SetDocumentName(link.FileName);
                    BringForward(blank);
                }
                Environment.Host.NoteRecentDocument(path);
                return;
            }
            var tiles = ImageImporter.Tiles(image, (0, 0));
            var layer = new Layer(name, new RasterContent(tiles));
            CreateDocument(new DocumentState(image.Width, image.Height, layers: [layer]), link, null, false);
            Environment.Host.NoteRecentDocument(path);
        }
        catch (Exception error)
        {
            Environment.Log?.Invoke($"couldn't open {Path.GetFileName(path)}: {error.Message}");
            Environment.Host.PresentError(PersonMessage.Text(error));
        }
    }

    private static bool StartsWithPhotoshopSignature(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> start = stackalloc byte[4];
            return file.Read(start) == 4 && start.SequenceEqual("8BPS"u8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Projects from Glimmer for the web open as new, untitled documents.</summary>
    private void OpenAips(string path)
    {
        try
        {
            CreateDocument(AipsImporter.ImportProject(File.ReadAllBytes(path)));
            Environment.Host.NoteRecentDocument(path);
        }
        catch (Exception error)
        {
            Environment.Host.PresentError(PersonMessage.Text(error));
        }
    }

    /// <summary>File › Open…: .glimmer documents, images, Photoshop documents and web projects, several at a time.</summary>
    public void ShowOpenPanel()
    {
        var paths = Environment.Dialogs.ChooseDocuments(multiple: true);
        if (paths.Count > 0) Open(paths);
    }

    /// <summary>File › Open Package Folder… (Windows only): a Glimmer document from a Mac is a folder named
    /// <c>name.glimmer</c>, which the file dialog can't pick as a file. Its first save writes it as one file.</summary>
    public void OpenPackageFolder()
    {
        if (Environment.Dialogs.ChooseFolder("Open Package Folder") is not { } folder) return;
        if (!Path.GetExtension(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Equals(".glimmer", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Host.PresentError(new UiText("{0} is not a Glimmer document. Choose a folder whose name ends in .glimmer (a document from a Mac).",
                                                     Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
            return;
        }
        OpenGlimmer(folder);
    }

    /// <summary>File › Linked Files Waiting…: the kept edits that are not open in a window.</summary>
    private sealed class WaitingLinks(DocumentOpener opener) : ILinkedFilesWaiting
    {
        public IReadOnlyList<WaitingLink> Entries() =>
        [
            .. opener.WaitingEntries().Select(e => new WaitingLink(e.Id, e.Record.FileName, e.Record.Link.Requester, e.Record.WrittenAt,
                                                                  LinkJournalWriter.Bytes(e.Folder), e.Record.Unreturned, e.Record.UnreturnedSteps)),
        ];

        public IReadOnlyList<string> Broken => opener.Journal.Broken;

        public bool Open(WaitingLink entry) => Find(entry) is { } found && opener.Open(found);

        public bool Discard(WaitingLink entry) => Find(entry) is { } found && opener.Journal.Discard(found);

        private LinkJournal.Entry? Find(WaitingLink entry) => opener.Journal.Entries().FirstOrDefault(e => e.Id == entry.Id);
    }
}
