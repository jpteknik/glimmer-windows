// Ported from Packages/GlimmerKit/Sources/GlimmerCore/UndoSnapshot.swift

using System.Security.Cryptography;

namespace Glimmer.Core;

/// <summary>
/// A document state kept for undo. It starts in memory and can be moved to a package on disk when the history
/// outgrows its memory budget; it loads back when the step is undone. The files are removed when the snapshot is
/// released: call <see cref="Dispose"/> (the Mac's ARC does this when the last reference goes; a finalizer is the
/// safety net here).
/// </summary>
public sealed class UndoSnapshot : IDisposable
{
    private abstract record Storage;
    private sealed record Memory(DocumentState State) : Storage;
    /// <summary>
    /// The selection isn't part of the document format, so it stays in memory. <c>Blobs</c> are the shared layer files
    /// this snapshot links to (see <see cref="UndoSpillStore"/>).
    /// </summary>
    private sealed record Disk(string Path, Selection? Selection, IReadOnlyList<string> Blobs) : Storage;
    private sealed record Released : Storage;

    private Storage storage;
    private readonly Lock gate = new();

    public UndoSnapshot(DocumentState state) => storage = new Memory(state);

    ~UndoSnapshot() => Release();

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    private void Release()
    {
        Storage old;
        lock (gate)
        {
            old = storage;
            storage = new Released();
        }
        if (old is Disk disk) UndoSpillStore.Remove(disk.Path, disk.Blobs);
    }

    private Storage Current
    {
        get { lock (gate) return storage; }
    }

    public bool IsOnDisk => Current is Disk;

    /// <summary>The state, read back from disk if it was moved there.</summary>
    public DocumentState State() => Current switch
    {
        Memory m => m.State,
        Disk d => GlimmerPackage.State(PackageContents.ReadDirectory(d.Path)) with { Selection = d.Selection },
        _ => throw new ObjectDisposedException(nameof(UndoSnapshot)),
    };

    /// <summary>
    /// Writes the state to <paramref name="directory"/> and releases the in-memory copy. Safe to call from a background
    /// task; does nothing when already on disk.
    /// </summary>
    public void MoveToDisk(string directory, EncodedLayerCache? cache = null)
    {
        if (Current is not Memory memory) return;
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, SwiftJson.UuidString(Guid.NewGuid()) + ".glimmer");
        // Layer files are written once into the folder's shared store and hard-linked into the snapshot, so steps that
        // share layers share their bytes on disk. A failure (a full disk, a write error) leaves nothing behind, and the
        // state stays in memory.
        var blobs = UndoSpillStore.Write(GlimmerPackage.Contents(memory.State, cache), path, directory);
        bool released = false;
        lock (gate)
        {
            if (storage is Memory) storage = new Disk(path, memory.State.Selection, blobs);
            else released = true;
        }
        if (released) UndoSpillStore.Remove(path, blobs);
    }

    /// <summary>The most bytes this snapshot can take on disk: its pixel tiles, uncompressed.</summary>
    public long DiskBytesUpperBound => Current is Memory m ? (long)m.State.TextureTileIDs.Count * TileGeometry.ByteCount : 0;
}

/// <summary>What to do with an undo history that has outgrown its memory budget.</summary>
public static class UndoSpillPolicy
{
    /// <summary>Free space Glimmer always leaves on the disk it spills to.</summary>
    public const long ReserveBytes = 1_073_741_824;

    /// <summary>Bytes free for Glimmer's use on the volume that holds <paramref name="path"/> (null if it can't be read).</summary>
    public static long? FreeBytes(string path) => FileSystemLinks.FreeBytes(path);

    /// <summary>Whether a snapshot of at most <paramref name="bytes"/> may be written when <paramref name="free"/> bytes are available.</summary>
    public static bool CanSpill(long bytes, long? free)
    {
        if (free is not { } f) return true;   // unknown: try, and handle the failure
        return f - bytes >= ReserveBytes;
    }

    /// <summary>
    /// How many of the oldest applied steps to forget so the steps kept in memory fit <paramref name="budget"/>.
    /// <paramref name="costs"/> are the applied steps' memory costs, oldest first, 0 for steps already on disk. The
    /// newest applied step is always kept.
    /// </summary>
    public static int StepsToForget(IReadOnlyList<long> costs, long budget)
    {
        long inMemory = costs.Sum();
        int forget = 0;
        while (inMemory > budget && forget < costs.Count - 1)
        {
            inMemory -= costs[forget];
            forget++;
        }
        return forget;
    }
}

/// <summary>Undo snapshots on disk without duplicates.</summary>
/// <remarks>
/// A spilled snapshot is a whole document package, and consecutive steps share almost all of their layers. Each layer
/// file is therefore stored once, under the hash of its bytes, in <c>&lt;folder&gt;/blobs</c>, and hard-linked into
/// every snapshot package that has it. A blob goes when its last snapshot goes. Packages are written as
/// <c>&lt;name&gt;.partial</c> and renamed when complete, so nothing half-written is ever read back.
/// </remarks>
public static class UndoSpillStore
{
    public enum SpillError
    {
        /// <summary>The store and the snapshot aren't on the same volume (hard links can't cross volumes).</summary>
        DifferentVolume,
        CannotLink,
    }

    public sealed class SpillException : Exception
    {
        public SpillException(SpillError error, int code = 0)
            : base(error == SpillError.DifferentVolume
                ? "The undo store and the snapshot are on different volumes."
                : $"A file of the undo history could not be linked (error {code}).")
        {
            Error = error;
            Code = code;
        }

        public SpillError Error { get; }
        /// <summary>The system's error code, for <see cref="SpillError.CannotLink"/>.</summary>
        public int Code { get; }
    }

    /// <summary>One writer or remover at a time (per process; a spill folder belongs to one process).</summary>
    private static readonly Lock StoreLock = new();
    /// <summary>Files smaller than this (the manifest) are written into the package as they are.</summary>
    internal const int SmallFile = 16_384;

    internal static string Blobs(string store) => Path.Combine(store, "blobs");

    /// <summary>
    /// Writes <paramref name="contents"/> as a package folder at <paramref name="path"/>, sharing its larger files
    /// through <paramref name="store"/>. Returns the blobs the package links to.
    /// </summary>
    public static IReadOnlyList<string> Write(PackageContents contents, string path, string store)
    {
        Directory.CreateDirectory(Blobs(store));
        var partial = path + ".partial";
        TryDelete(partial);
        var linked = new List<string>();
        try
        {
            WriteTree(contents, partial, store, linked);
            Directory.Move(partial, path);
        }
        catch
        {
            TryDelete(partial);
            lock (StoreLock) ReleaseBlobs(linked);
            throw;
        }
        return linked;
    }

    private static void WriteTree(PackageContents contents, string root, string store, List<string> linked)
    {
        Directory.CreateDirectory(root);
        foreach (var dir in contents.Directories) Directory.CreateDirectory(Path.Combine(root, dir));
        foreach (var (relative, data) in contents.Files)
        {
            var target = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (data.Length < SmallFile)
            {
                File.WriteAllBytes(target, data.ToArray());
                continue;
            }
            var name = Convert.ToHexStringLower(SHA256.HashData(data.Span));
            var blob = Path.Combine(Blobs(store), name);
            lock (StoreLock)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (!File.Exists(blob))
                    {
                        // Written under a temporary name first: a blob with its real name is always whole.
                        var temp = Path.Combine(Blobs(store), name + ".tmp-" + SwiftJson.UuidString(Guid.NewGuid()));
                        try
                        {
                            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                file.Write(data.Span);
                            File.Move(temp, blob);
                        }
                        catch
                        {
                            TryDelete(temp);
                            throw;
                        }
                    }
                    var (failure, code) = FileSystemLinks.CreateHardLink(blob, target);
                    if (failure == FileSystemLinks.LinkFailure.None) break;
                    if (failure == FileSystemLinks.LinkFailure.SourceMissing && attempt == 0) continue;   // the blob went meanwhile: write it again
                    ReleaseBlobs([blob]);   // not linked: don't leave a blob that no step uses
                    throw new SpillException(failure == FileSystemLinks.LinkFailure.DifferentVolume ? SpillError.DifferentVolume : SpillError.CannotLink, code);
                }
            }
            linked.Add(blob);
        }
    }

    /// <summary>How many names a file has (1 for a blob no snapshot links to).</summary>
    internal static int LinkCount(string path) => FileSystemLinks.LinkCount(path);

    /// <summary>Removes the blobs that nothing links to any more. Call with the lock held.</summary>
    private static void ReleaseBlobs(IEnumerable<string> blobs)
    {
        foreach (var blob in blobs.Distinct())
            if (LinkCount(blob) == 1) TryDelete(blob);
    }

    /// <summary>Removes a snapshot package and the blobs only it used.</summary>
    public static void Remove(string package, IReadOnlyList<string> blobs)
    {
        lock (StoreLock)
        {
            TryDelete(package);
            ReleaseBlobs(blobs);
        }
    }

    /// <summary>
    /// Removes what an interrupted write left in a spill folder: unfinished packages, temporary blob files, and blobs
    /// that no package links to.
    /// </summary>
    public static void Sweep(string store)
    {
        lock (StoreLock)
        {
            foreach (var entry in SafeEntries(store))
                if (Path.GetFileName(entry).EndsWith(".partial", StringComparison.Ordinal)) TryDelete(entry);
            foreach (var file in SafeEntries(Blobs(store)))
            {
                if (Path.GetFileName(file).Contains(".tmp-", StringComparison.Ordinal) || LinkCount(file) == 1) TryDelete(file);
            }
        }
    }

    /// <summary>
    /// The name of a spill folder for a process: <c>undo-&lt;pid&gt;.&lt;start&gt;-&lt;uuid&gt;</c>, where <c>start</c>
    /// is when the process started (seconds since 1970). A process id alone is not enough: the system hands a finished
    /// process's id to a new one, and the folder would then look alive.
    /// </summary>
    public static string FolderName(int? pid = null, long? start = null)
    {
        int id = pid ?? Environment.ProcessId;
        var started = start ?? StartTime(id);
        var uuid = SwiftJson.UuidString(Guid.NewGuid());
        return started is { } s ? $"undo-{id}.{s}-{uuid}" : $"undo-{id}-{uuid}";
    }

    /// <summary>
    /// What a spill folder's name says about who made it. The three forms are told apart by their shape, never by
    /// whether a part happens to read as a number: an earlier version took a bare UUID that began with eight digits for
    /// a process id and removed a fresh folder.
    /// </summary>
    internal abstract record Owner
    {
        /// <summary><c>undo-&lt;pid&gt;.&lt;start&gt;-&lt;uuid&gt;</c> and the earlier <c>undo-&lt;pid&gt;-&lt;uuid&gt;</c>.</summary>
        internal sealed record Process(int Pid, long? Start) : Owner;
        /// <summary>The first form, <c>undo-&lt;uuid&gt;</c>.</summary>
        internal sealed record Unknown : Owner;
    }

    internal static Owner? OwnerOfFolderNamed(string name)
    {
        if (!name.StartsWith("undo-", StringComparison.Ordinal)) return null;
        var parts = name[5..].Split('-');
        static bool IsUuid(ArraySegment<string> groups) =>
            groups.Count == 5 && Guid.TryParseExact(string.Join("-", (IEnumerable<string>)groups), "D", out _);
        if (parts.Length == 5 && IsUuid(parts)) return new Owner.Unknown();
        if (parts.Length != 6 || !IsUuid(new ArraySegment<string>(parts, 1, 5))) return null;   // not one of ours: left alone
        var who = parts[0].Split('.');
        if (!int.TryParse(who[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid) || pid <= 0)
            return null;
        if (who.Length == 1) return new Owner.Process(pid, null);
        if (who.Length != 2 || !long.TryParse(who[1], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long start))
            return null;
        return new Owner.Process(pid, start);
    }

    /// <summary>
    /// Removes the spill folders of processes that no longer run (the history isn't kept across launches, so after a
    /// crash they are only wasted space). Folders of running processes, this one included, are left alone, and so is
    /// anything whose name isn't one of the three forms. "The same process" means the id and the start time both
    /// match; when the start time of a running process can't be read, its folder is kept. Returns how many were removed.
    /// </summary>
    public static int RemoveOrphans(string directory, Func<int, bool>? isRunning = null, Func<int, long?>? startTime = null)
    {
        isRunning ??= IsRunning;
        startTime ??= StartTime;
        int removed = 0;
        foreach (var path in SafeEntries(directory))
        {
            var name = Path.GetFileName(path);
            if (OwnerOfFolderNamed(name) is not { } owner) continue;
            switch (owner)
            {
                case Owner.Process(var pid, var start):
                    if (isRunning(pid))
                    {
                        // The id is in use. It is the folder's maker unless both start times are known and differ.
                        if (start is not { } s || startTime(pid) is not { } now || Math.Abs(now - s) <= 1) continue;
                    }
                    break;
                case Owner.Unknown:
                    // A folder from before folders carried the process id: only when it is a day old.
                    DateTime modified;
                    try { modified = Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { modified = DateTime.MinValue; }
                    if ((DateTime.UtcNow - modified).TotalSeconds < 86_400) continue;
                    break;
            }
            if (TryDelete(path)) removed++;
        }
        return removed;
    }

    /// <summary>When the process with this id started, in seconds since 1970, or null when the system doesn't say.</summary>
    public static long? StartTime(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            var seconds = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
            return seconds > 0 ? seconds : null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Whether a process with this id exists (one we may not look at counts as running).</summary>
    public static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            try { return !process.HasExited; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return true; }
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeEntries(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFileSystemEntries(directory) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Removes a file or a folder (with what is in it); true when it is gone.</summary>
    internal static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
            else return false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
