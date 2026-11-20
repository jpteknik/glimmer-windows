// Windows only: crash recovery for documents with unsaved changes. The Mac relies on NSDocument's autosave (and asks it
// to autosave a second after each step of a small untitled document, EditorSession+Linked.swift's autosaveSoon); a
// Windows document is only written when the person saves it, so the editor keeps its own copy of what isn't saved yet.
using System.Text.Json;
using Glimmer.Core;

namespace Glimmer.Editor;

/// <summary>Recovery copies of documents with unsaved changes, in %LOCALAPPDATA%\Glimmer\Recovery: one folder per open
/// document (document.glimmer and info.json). A copy is written in the background every couple of minutes while its
/// document has unsaved changes, removed after a clean save or close, and offered at the next start when Glimmer
/// stopped without either.</summary>
public static class RecoveryStore
{
    public const string DocumentName = "document.glimmer";
    public const string InfoName = "info.json";

    /// <summary>What a recovery copy says about itself.</summary>
    public sealed record Info(string Name, string? OriginalPath, DateTimeOffset SavedAt, int ProcessId, long? ProcessStart);

    /// <summary>A recovery copy on disk.</summary>
    public sealed record Entry(string Id, string Folder, Info Info)
    {
        public string DocumentPath => Path.Combine(Folder, DocumentName);
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>Writes the copy (the package first, then its info, each beside its place and moved in: a stop leaves the
    /// previous copy or the new one, never half of one). Call off the UI thread.</summary>
    public static void Write(string folder, string id, DocumentState state, string name, string? originalPath)
    {
        var entry = Path.Combine(folder, id);
        Directory.CreateDirectory(entry);
        GlimmerPackage.Save(state, Path.Combine(entry, DocumentName));
        var info = new Info(name, originalPath, DateTimeOffset.UtcNow, System.Environment.ProcessId,
                            UndoSpillStore.StartTime(System.Environment.ProcessId));
        var temp = Path.Combine(entry, InfoName + ".tmp");
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(info, Json));
        File.Move(temp, Path.Combine(entry, InfoName), overwrite: true);
    }

    /// <summary>The copies left by Glimmer runs that ended without saving or closing their documents, newest first.
    /// Copies of a Glimmer that is still running (another window of the app) are not offered.</summary>
    public static IReadOnlyList<Entry> Entries(string folder, Func<int, bool>? isRunning = null, Func<int, long?>? startTime = null)
    {
        isRunning ??= UndoSpillStore.IsRunning;
        startTime ??= UndoSpillStore.StartTime;
        var found = new List<Entry>();
        if (!Directory.Exists(folder)) return found;
        foreach (var path in Directory.GetDirectories(folder))
        {
            try
            {
                var info = JsonSerializer.Deserialize<Info>(File.ReadAllBytes(Path.Combine(path, InfoName)), Json);
                if (info is null || !File.Exists(Path.Combine(path, DocumentName))) continue;
                // Still being kept by a live process (the same id and the same start time).
                bool alive = isRunning(info.ProcessId)
                    && (info.ProcessStart is not { } s || startTime(info.ProcessId) is not { } now || Math.Abs(now - s) <= 1);
                if (alive) continue;
                found.Add(new Entry(Path.GetFileName(path), path, info));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Not one of ours, or half-written: left where it is.
            }
        }
        return [.. found.OrderByDescending(e => e.Info.SavedAt)];
    }

    public static DocumentState Read(Entry entry) => GlimmerPackage.Load(entry.DocumentPath);

    /// <summary>A copy this run took over (opened from it): it says so, so another running Glimmer doesn't offer it.</summary>
    public static void Claim(string folder, string id)
    {
        var path = Path.Combine(folder, id, InfoName);
        var info = JsonSerializer.Deserialize<Info>(File.ReadAllBytes(path), Json);
        if (info is null) return;
        var claimed = info with { ProcessId = System.Environment.ProcessId, ProcessStart = UndoSpillStore.StartTime(System.Environment.ProcessId) };
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(claimed, Json));
        File.Move(temp, path, overwrite: true);
    }

    public static void Remove(string folder, string id)
    {
        var path = Path.Combine(folder, id);
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
