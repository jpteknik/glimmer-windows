// Ported from Packages/GlimmerKit/Sources/GlimmerCore/LinkJournalStore.swift

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimmer.Core;

/// <summary>What the link journal keeps about one linked document, beside its work copy (the-apps/SIBLINGS.md §6.6).</summary>
[JsonConverter(typeof(LinkJournalRecordJsonConverter))]
public sealed record LinkJournalRecord
{
    public LinkJournalRecord(string id, DocumentLink link, string fileName, bool unreturned, int? unreturnedSteps = null,
                             bool? keptKnowingly = null, DateTimeOffset? linkedAt = null, DateTimeOffset? writtenAt = null)
    {
        Id = id;
        Link = link;
        FileName = fileName;
        Unreturned = unreturned;
        UnreturnedSteps = unreturnedSteps;
        KeptKnowingly = keptKnowingly;
        LinkedAt = linkedAt ?? DateTimeOffset.UtcNow;
        WrittenAt = writtenAt ?? DateTimeOffset.UtcNow;
    }

    public string Id { get; init; }
    /// <summary>The file of the sibling app: bookmark, path, requester.</summary>
    public DocumentLink Link { get; init; }
    public string FileName { get; init; }
    /// <summary>Edits were made that have not been saved back into the file.</summary>
    public bool Unreturned { get; init; }
    /// <summary>How many steps wait to be saved back (said in the close question and in the waiting list).</summary>
    public int? UnreturnedSteps { get; init; }
    /// <summary>
    /// Written at Quit, or when the window was closed with Keep for Later: the person left it knowingly. Without it, an
    /// entry with edits waiting was left by a stop (a crash, a kill, the power).
    /// </summary>
    public bool? KeptKnowingly { get; init; }
    public DateTimeOffset LinkedAt { get; init; }
    public DateTimeOffset WrittenAt { get; init; }

    /// <summary>Reads a record (ISO 8601 dates, as the Mac writes them).</summary>
    public static LinkJournalRecord Decode(ReadOnlySpan<byte> json) => SwiftJson.Deserialize<LinkJournalRecord>(json);

    /// <summary>The record as the Mac writes it: sorted keys, ISO 8601 dates to the second.</summary>
    public byte[] Encode() => SwiftJson.SerializeToUtf8Bytes(this);
}

/// <summary>Swift's <c>.iso8601</c> date strategy: <c>2027-01-15T08:00:00Z</c>, whole seconds.</summary>
internal static class Iso8601
{
    public static string Format(DateTimeOffset date) =>
        date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string text, string key)
    {
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];
        if (DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date;
        throw new JsonException($"Expected date string to be ISO8601-formatted for key \"{key}\".");
    }
}

public sealed class LinkJournalRecordJsonConverter : JsonConverter<LinkJournalRecord>
{
    public override LinkJournalRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(LinkJournalRecord));
        return new LinkJournalRecord(o.ReqString("id"), o.Req<DocumentLink>("link", options), o.ReqString("fileName"), o.ReqBool("unreturned"),
                                     o.OptInt("unreturnedSteps") is { } steps ? (int)steps : null, o.OptBool("keptKnowingly"),
                                     Iso8601.Parse(o.ReqString("linkedAt"), "linkedAt"), Iso8601.Parse(o.ReqString("writtenAt"), "writtenAt"));
    }

    public override void Write(Utf8JsonWriter writer, LinkJournalRecord value, JsonSerializerOptions options) =>
        new JsonFields(options).String("id", value.Id).Value("link", value.Link).String("fileName", value.FileName)
            .Bool("unreturned", value.Unreturned).Int("unreturnedSteps", value.UnreturnedSteps).Bool("keptKnowingly", value.KeptKnowingly)
            .String("linkedAt", Iso8601.Format(value.LinkedAt)).String("writtenAt", Iso8601.Format(value.WrittenAt)).WriteTo(writer);
}

/// <summary>
/// Writes one journal entry: <c>record.json</c> and the layered work copy <c>work.glimmer</c> (a package folder), in
/// the entry's folder.
/// </summary>
/// <remarks>
/// <para><b>An entry on disk is always a whole one: the state before a step or the state after it, never a mix.</b> The new
/// work copy is built beside the old one (<c>work.next.glimmer</c>): files the step did not change are hard links to
/// the old copy's files (they cost no room and no time), the changed ones are written, and then the new copy takes the
/// old one's place. A stop at any point before that leaves the old copy as it was; a stop after it leaves the new one.
/// What was left of an unfinished build is removed by the next write.</para>
/// <para>Windows can't swap two folders in one step, so the old copy is first renamed <c>work.prev.glimmer</c>, the new
/// one takes its name, and the old one is removed. A stop between the two renames leaves no <c>work.glimmer</c> but a
/// whole <c>work.prev.glimmer</c>; <see cref="Recover"/> (run by every write, and by whoever reads an entry) puts it
/// back, so a reader still finds the state before the step.</para>
/// <para>The record is written before the work copy when it says that something waits (so a stop never leaves waiting
/// work marked as nothing waiting) and again after it.</para>
/// <para>Room is looked for before anything is written and again before each changed file: when the step will not fit,
/// nothing of the entry is touched and <see cref="NoRoomException"/> says how much was needed.</para>
/// <para>The Mac's actor becomes a class whose writes take a lock, one at a time.</para>
/// </remarks>
public sealed class LinkJournalWriter
{
    public const string WorkCopyName = "work.glimmer";
    public const string RecordName = "record.json";
    internal const string StagingName = "work.next.glimmer";
    internal const string PreviousName = "work.prev.glimmer";
    /// <summary>Room left over on the volume after a write.</summary>
    internal const long Reserve = 2 * 1_048_576;

    public sealed record Outcome(long Bytes, long BytesWritten, int FilesWritten, double Milliseconds, DateTimeOffset At)
    {
        /// <summary>The whole entry on disk.</summary>
        public long Bytes { get; init; } = Bytes;
        /// <summary>What this write put there (files the step did not change cost nothing).</summary>
        public long BytesWritten { get; init; } = BytesWritten;
    }

    /// <summary>The step needs <see cref="Needed"/> bytes and the volume has <see cref="Free"/> (Swift's <c>Failure.noRoom</c>).</summary>
    public sealed class NoRoomException(long needed, long free)
        : Exception($"There isn't enough room to keep these edits: {ByteCountText.File(needed)} is needed and {ByteCountText.File(Math.Max(free, 0))} is free.")
    {
        public long Needed { get; } = needed;
        public long Free { get; } = free;
    }

    /// <summary>Where a test stops a write, to show what is on disk at that moment.</summary>
    public readonly record struct StopPoint(StopPointKind Kind, int Files = 0)
    {
        /// <summary>After this many of the step's changed files have been written into the new copy.</summary>
        public static StopPoint AfterFiles(int count) => new(StopPointKind.AfterFiles, count);
        /// <summary>With the new copy complete, before it takes the old one's place.</summary>
        public static StopPoint BeforeSwap => new(StopPointKind.BeforeSwap);
        /// <summary>With the new copy in place, before the record is written again.</summary>
        public static StopPoint BeforeRecord => new(StopPointKind.BeforeRecord);
    }

    public enum StopPointKind { AfterFiles, BeforeSwap, BeforeRecord }

    /// <summary>A write stopped on purpose by <see cref="StopNextWrite"/>.</summary>
    public sealed class StoppedException() : Exception("The write was stopped on purpose.");

    private readonly string folder;
    private readonly EncodedLayerCache cache;
    private readonly Lock gate = new();
    /// <summary>The bytes of each file of the work copy as it is on disk now, by its path inside the package.</summary>
    private Dictionary<string, ReadOnlyMemory<byte>> written = new(StringComparer.Ordinal);
    private bool loaded;
    private StopPoint? stopPoint;
    private System.Action? stop;

    public LinkJournalWriter(string folder, EncodedLayerCache? cache = null)
    {
        this.folder = folder;
        this.cache = cache ?? new EncodedLayerCache();
    }

    /// <summary>Arms a stop for the next write that reaches <paramref name="point"/> (once). <paramref name="stop"/> throws, or never returns.</summary>
    public void StopNextWrite(StopPoint point, System.Action? stop = null)
    {
        lock (gate)
        {
            stopPoint = point;
            this.stop = stop ?? (() => throw new StoppedException());
        }
    }

    private void Reached(StopPoint point)
    {
        if (stopPoint != point || stop is not { } s) return;
        stopPoint = null;
        stop = null;
        s();
    }

    /// <summary>
    /// Puts the old work copy back if a write stopped between moving it aside and putting the new one in place. Safe to
    /// call at any time; does nothing for a whole entry.
    /// </summary>
    public static void Recover(string entryFolder)
    {
        var package = Path.Combine(entryFolder, WorkCopyName);
        var previous = Path.Combine(entryFolder, PreviousName);
        if (!Directory.Exists(previous)) return;
        if (Directory.Exists(package)) UndoSpillStore.TryDelete(previous);
        else Directory.Move(previous, package);
    }

    public Task<Outcome> WriteAsync(LinkJournalRecord record, DocumentState? state) => Task.Run(() => Write(record, state));

    /// <exception cref="NoRoomException">The step doesn't fit; nothing of the entry was touched.</exception>
    public Outcome Write(LinkJournalRecord record, DocumentState? state)
    {
        lock (gate) return WriteLocked(record, state);
    }

    private Outcome WriteLocked(LinkJournalRecord record, DocumentState? state)
    {
        var clock = Stopwatch.StartNew();
        Directory.CreateDirectory(folder);
        Recover(folder);
        var package = Path.Combine(folder, WorkCopyName);
        var staging = Path.Combine(folder, StagingName);
        record = record with { WrittenAt = DateTimeOffset.UtcNow };
        long WriteRecord()
        {
            var data = record.Encode();
            var path = Path.Combine(folder, RecordName);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(data);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            FileSystemLinks.Sync(path);
            FileSystemLinks.Sync(folder);
            return data.Length;
        }
        long bytesWritten = 0;
        int filesWritten = 0;
        if (state is not null)
        {
            // The record holds the link. No thumbnail or preview: the work copy is read by Glimmer only, and making them
            // would cost more than the step.
            var contents = GlimmerPackage.Contents(state with { Link = null }, cache, quickLook: null);
            var files = new Dictionary<string, ReadOnlyMemory<byte>>(contents.Files, StringComparer.Ordinal);
            // After a relaunch: what is on disk is what was written, so the first step does not write every layer again.
            if (!loaded)
            {
                loaded = true;
                foreach (var path in files.Keys)
                {
                    var onDisk = Path.Combine(package, path);
                    if (File.Exists(onDisk)) written[path] = File.ReadAllBytes(onDisk);
                }
            }
            bool Same(string path, ReadOnlyMemory<byte> data) =>
                written.TryGetValue(path, out var old) && old.Span.SequenceEqual(data.Span);
            var changed = files.Where(f => !Same(f.Key, f.Value)).OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
            if (changed.Count > 0 || files.Count != written.Count)
            {
                long needed = changed.Sum(f => (long)f.Value.Length);
                void Room()
                {
                    if (UndoSpillPolicy.FreeBytes(folder) is { } free && free - needed < Reserve) throw new NoRoomException(needed, free);
                }
                Room();
                // Something waits: said first, so that a stop from here on never leaves it marked as nothing waiting.
                if (record.Unreturned) WriteRecord();
                UndoSpillStore.TryDelete(staging);   // what an unfinished build left
                Directory.CreateDirectory(staging);
                try
                {
                    foreach (var dir in contents.Directories) Directory.CreateDirectory(Path.Combine(staging, dir));
                    foreach (var (path, data) in files)
                    {
                        if (!Same(path, data)) continue;
                        var to = Path.Combine(staging, path);
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        if (FileSystemLinks.CreateHardLink(Path.Combine(package, path), to).Failure != FileSystemLinks.LinkFailure.None)
                            File.WriteAllBytes(to, data.ToArray());
                    }
                    // The manifest last of the changed files: it is the smallest, and nothing reads the new copy before the move anyway.
                    var ordered = changed.Where(f => f.Key != GlimmerPackage.ManifestFilename)
                        .Concat(changed.Where(f => f.Key == GlimmerPackage.ManifestFilename));
                    foreach (var (path, data) in ordered)
                    {
                        Room();
                        var to = Path.Combine(staging, path);
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        using (var file = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            file.Write(data.Span);
                            file.Flush(flushToDisk: true);   // on the disk before the new copy can take the old one's place
                        }
                        needed -= data.Length;
                        bytesWritten += data.Length;
                        filesWritten++;
                        Reached(StopPoint.AfterFiles(filesWritten));
                    }
                    Reached(StopPoint.BeforeSwap);
                    FileSystemLinks.Sync(staging);
                    if (Directory.Exists(package))
                    {
                        var previous = Path.Combine(folder, PreviousName);
                        UndoSpillStore.TryDelete(previous);
                        Directory.Move(package, previous);
                        Directory.Move(staging, package);
                        UndoSpillStore.TryDelete(previous);
                    }
                    else
                    {
                        Directory.Move(staging, package);
                    }
                    FileSystemLinks.Sync(folder);   // the move itself
                }
                catch (Exception error)
                {
                    // The old copy is as it was. (After a real stop the unfinished build stays until the next write removes it.)
                    if (error is not StoppedException) UndoSpillStore.TryDelete(staging);
                    throw;
                }
                written = files;
                Reached(StopPoint.BeforeRecord);
            }
        }
        long recordBytes = WriteRecord();
        return new Outcome(written.Values.Sum(d => (long)d.Length) + recordBytes, bytesWritten + recordBytes, filesWritten + 1,
                           clock.Elapsed.TotalMilliseconds, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The bytes an entry's folder holds (for the list of what is waiting). Each name of a hard-linked file is counted,
    /// and the unfinished build is left out.
    /// </summary>
    public static long Bytes(string entryFolder)
    {
        if (!Directory.Exists(entryFolder)) return 0;
        long total = 0;
        var staging = Path.DirectorySeparatorChar + StagingName + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(entryFolder, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(staging, StringComparison.Ordinal)) continue;
            try { total += new FileInfo(file).Length; }
            catch (IOException) { }
        }
        return total;
    }
}
