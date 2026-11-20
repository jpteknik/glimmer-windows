// Ported from App/LinkJournal.swift
using Glimmer.Core;

namespace Glimmer.Editor;

/// <summary>Glimmer's own record of linked documents (the-apps/SIBLINGS.md §6.6).</summary>
/// <remarks>
/// A linked edit must survive Quit, a crash and a restart whatever happens to windows. So the link and the work are
/// kept here, never only in a window: one folder per linked document in %LOCALAPPDATA%\Glimmer\LinkJournal (Application
/// Support on the Mac), with record.json and the layered work copy work.glimmer. It is written when the document is
/// linked and after every step, so a crash costs at most the step in progress.
///
/// A write after a step is cheap for a large document: the work copy is a package with one file per layer, and only the
/// layers the step changed are encoded and written (each beside its place, then moved in). The small manifest goes last,
/// then the record, so an entry on disk is always a whole one: the state before the step or the state after it.
/// Writing happens off the UI thread, one writer per entry, and when steps come faster than the disk only the newest
/// state is written.
/// </remarks>
public sealed class LinkJournal
{
    public sealed record Entry(LinkJournalRecord Record, string Folder)
    {
        public string Id => Record.Id;
        public string WorkCopy => Path.Combine(Folder, WorkCopyName);
    }

    public const string WorkCopyName = LinkJournalWriter.WorkCopyName;
    public const string RecordName = LinkJournalWriter.RecordName;

    private readonly EditorEnvironment environment;

    /// <summary>Null when the journal is switched off ("linkJournal" = "NONE": test copies that must not open another
    /// run's leftovers).</summary>
    public string? Folder { get; }

    private List<string> broken = [];
    /// <summary>Entry folders that could not be read at the last look. They are left where they are, never deleted by the app.</summary>
    public IReadOnlyList<string> Broken => broken;

    private readonly Dictionary<string, LinkJournalWriter> writers = [];
    private readonly Dictionary<string, (LinkJournalRecord Record, DocumentState? State)> waiting = [];
    private readonly HashSet<string> busy = [];
    private readonly Dictionary<string, LinkJournalWriter.Outcome> lastWrite = [];
    private readonly Dictionary<string, (long Needed, long Free)> noRoom = [];

    /// <summary>The last write of each entry: bytes on disk, bytes written by that step, milliseconds.</summary>
    public IReadOnlyDictionary<string, LinkJournalWriter.Outcome> LastWrite => lastWrite;
    /// <summary>Entries whose last write did not fit on the disk. What was kept before is still whole; the steps since are
    /// only in the window.</summary>
    public IReadOnlyDictionary<string, (long Needed, long Free)> NoRoom => noRoom;

    /// <summary>Called when an entry's write did not fit, and again (with null) when a later one did.</summary>
    public Action<string, (long Needed, long Free)?>? OnRoom { get; set; }

#if DEBUG
    /// <summary>One shot, for the restart test: the next state write stops at this point.</summary>
    public (LinkJournalWriter.StopPoint Point, Action Stop)? StopNextWriteAt { get; set; }
#endif

    public LinkJournal(EditorEnvironment environment)
    {
        this.environment = environment;
        var named = environment.Settings.Get<string?>(Preferences.LinkJournalKey, null);
        if (named == "NONE") return;
        Folder = Path.Combine(environment.DataFolder, named is { Length: > 0 } ? $"LinkJournal-{named}" : "LinkJournal");
    }

    // MARK: Reading

    /// <summary>Every entry that can be read, oldest first. Folders that can't are remembered in Broken and left alone.</summary>
    public IReadOnlyList<Entry> Entries()
    {
        if (Folder is null || !Directory.Exists(Folder))
        {
            broken = [];
            return [];
        }
        var found = new List<Entry>();
        var unreadable = new List<string>();
        foreach (var path in Directory.GetDirectories(Folder).OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.')) continue;
            try
            {
                LinkJournalWriter.Recover(path);
                var record = LinkJournalRecord.Decode(File.ReadAllBytes(Path.Combine(path, RecordName)));
                if (record.Id != name || !Directory.Exists(Path.Combine(path, WorkCopyName))) throw new InvalidDataException();
                found.Add(new Entry(record, path));
            }
            catch
            {
                unreadable.Add(name);
            }
        }
        if (!unreadable.SequenceEqual(broken))
        {
            broken = unreadable;
            foreach (var name in unreadable) environment.Log?.Invoke($"a journal entry can't be read and is left where it is: {name}");
        }
        return [.. found.OrderBy(e => e.Record.LinkedAt)];
    }

    /// <summary>An entry whose work copy turned out not to be readable when it was opened.</summary>
    public void NoteUnreadable(string id)
    {
        if (!broken.Contains(id)) broken.Add(id);
    }

    /// <summary>The work copy of an entry, read with care: an entry that can't be read is reported, not removed.</summary>
    public static DocumentState StateOf(Entry entry)
    {
        LinkJournalWriter.Recover(entry.Folder);
        return GlimmerPackage.Load(entry.WorkCopy) with { Link = null };   // the record holds the link
    }

    /// <summary>The entry that edits <paramref name="file"/>, if one is kept (by its path).</summary>
    public Entry? EntryFor(string file)
    {
        var wanted = Canonical(file);
        return Entries().LastOrDefault(entry => Canonical(entry.Record.Link.Path) == wanted);
    }

    /// <summary>A path compared the way the file system does (case-insensitively on Windows).</summary>
    internal static string Canonical(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { full = path; }
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    // MARK: Writing

    /// <summary>Writes the entry: the record, and the work copy when <paramref name="state"/> is given. Returns at once;
    /// the newest state wins.</summary>
    public void Write(LinkJournalRecord record, DocumentState? state)
    {
        if (Folder is null) return;
        // A record alone must not push a state that is still waiting out of the queue.
        waiting[record.Id] = (record, state ?? (waiting.TryGetValue(record.Id, out var w) ? w.State : null));
        if (busy.Contains(record.Id)) return;
        busy.Add(record.Id);
        if (!writers.TryGetValue(record.Id, out var writer))
        {
            writer = new LinkJournalWriter(Path.Combine(Folder, record.Id), new EncodedLayerCache(byteLimit: DocumentLimits.PhysicalMemory / 16));
            writers[record.Id] = writer;
        }
        var id = record.Id;
#if DEBUG
        if (state is not null && StopNextWriteAt is { } stopAt)
        {
            StopNextWriteAt = null;
            writer.StopNextWrite(stopAt.Point, stopAt.Stop);
        }
#endif
        Drain(id, writer);
    }

    /// <summary>Writes what waits for <paramref name="id"/>, one write at a time, off the UI thread.</summary>
    private void Drain(string id, LinkJournalWriter writer)
    {
        if (!waiting.Remove(id, out var next))
        {
            Finished(id);
            return;
        }
        LinkJournalWriter.Outcome? outcome = null;
        Exception? failure = null;
        environment.Scheduler.RunInBackground(() =>
        {
            try { outcome = writer.Write(next.Record, next.State); }
            catch (Exception e) { failure = e; }
        }, () =>
        {
            switch (failure)
            {
                case null when outcome is { } o:
                    lastWrite[id] = o;
                    if (noRoom.Remove(id)) OnRoom?.Invoke(id, null);
                    break;
                case LinkJournalWriter.NoRoomException room:
                    environment.Log?.Invoke($"no room for journal entry {id}: {room.Needed} needed, {room.Free} free");
                    noRoom[id] = (room.Needed, room.Free);
                    OnRoom?.Invoke(id, (room.Needed, room.Free));
                    break;
                case { } error:
                    environment.Log?.Invoke($"couldn't write journal entry {id}: {error.Message}");
                    break;
            }
            Drain(id, writer);
        });
    }

    private void Finished(string id)
    {
        busy.Remove(id);
        if (waiting.TryGetValue(id, out var again)) Write(again.Record, again.State);
    }

    /// <summary>Whether everything asked for has reached the disk (tests wait for this before a hard stop "after" a step).</summary>
    public bool IsIdle => busy.Count == 0 && waiting.Count == 0;

    /// <summary>Removes an entry: its edits are saved back and its document closed.</summary>
    public void Remove(string id)
    {
        if (Folder is null) return;
        waiting.Remove(id);
        writers.Remove(id);
        lastWrite.Remove(id);
        var entry = Path.Combine(Folder, id);
        bool wasBusy = busy.Contains(id);
        // After a write that is under way, so a late file does not bring the folder back.
        environment.Scheduler.RunInBackground(() =>
        {
            if (wasBusy) Thread.Sleep(400);
            try { if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        });
    }

    /// <summary>The person declined an entry: its work copy goes to the Recycle Bin, not away, and the entry is removed.
    /// Returns false when the work copy could not be moved; then the entry is left as it is.</summary>
    public bool Discard(Entry entry)
    {
        // In the Recycle Bin under a name the person can recognise.
        var named = Path.Combine(entry.Folder, $"{Path.GetFileNameWithoutExtension(entry.Record.FileName)} (edit for {entry.Record.Link.Requester}).glimmer");
        try
        {
            Directory.Move(entry.WorkCopy, named);
            if (environment.MoveToRecycleBin is not { } recycle || !recycle(named)) throw new IOException("The Recycle Bin didn't take the edits.");
        }
        catch (Exception error)
        {
            environment.Log?.Invoke($"couldn't move a declined work copy to the Recycle Bin: {error.Message}");
            try { if (Directory.Exists(named)) Directory.Move(named, entry.WorkCopy); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return false;
        }
        Remove(entry.Id);
        return true;
    }

    /// <summary>For GET /api/v1/status: how many entries are kept, how many wait with edits not saved back, and which can't be read.</summary>
    public IReadOnlyDictionary<string, object> Summary
    {
        get
        {
            var all = Entries();
            var d = new Dictionary<string, object>
            {
                ["entries"] = all.Count,
                ["unreturned"] = all.Count(e => e.Record.Unreturned),
                ["broken"] = Broken.ToList(),
            };
            if (noRoom.Count > 0) d["noRoom"] = noRoom.Keys.Order(StringComparer.Ordinal).ToList();
            if (lastWrite.Values.MaxBy(o => o.At) is { } last)
            {
                d["lastWrite"] = new Dictionary<string, object>
                {
                    ["bytes"] = last.Bytes, ["bytesWritten"] = last.BytesWritten, ["filesWritten"] = last.FilesWritten,
                    ["milliseconds"] = Math.Round(last.Milliseconds * 10) / 10,
                };
            }
            return d;
        }
    }
}
