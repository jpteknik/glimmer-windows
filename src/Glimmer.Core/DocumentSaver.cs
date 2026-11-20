// Ported from Packages/GlimmerKit/Sources/GlimmerCore/DocumentSaver.swift

using System.Globalization;

namespace Glimmer.Core;

/// <summary>Why a document couldn't be saved, in words for the person saving it (Swift's <c>DocumentSaveError</c>).</summary>
public sealed class DocumentSaveException : Exception
{
    /// <summary>The disk has <paramref name="free"/> bytes and the document needs <paramref name="needed"/>.</summary>
    public DocumentSaveException(long needed, long free, string volume)
        : base($"There isn't enough room on “{volume}” to save this document: it needs {ByteCountText.File(needed)} and {ByteCountText.File(Math.Max(free, 0))} is free.")
    {
        Needed = needed;
        Free = free;
        Volume = volume;
    }

    public long Needed { get; }
    public long Free { get; }
    public string Volume { get; }

    public string RecoverySuggestion => "The document is still open and nothing was lost. Free up space or save it on another disk.";
}

/// <summary>
/// Writes a document so that a failure never leaves half a document: free space is checked first, the file is
/// written beside its destination and moved into place, and an earlier saved version stays as it was when the write
/// fails.
/// </summary>
/// <remarks>
/// The Mac writes a package folder with <c>FileWrapper.write(to:options: .atomic)</c>. Here a document is one ZIP
/// file: it is written to a temporary file in the destination's folder (same volume, so the move is a rename), flushed
/// to the disk, and renamed over the destination. A package folder from a Mac at the destination is replaced by the
/// file the same way, and put back if the replacement fails.
/// </remarks>
public static class DocumentSaver
{
    /// <summary>Room left over on top of the document itself (file system overhead, the temporary copy's metadata).</summary>
    internal const long Margin = 2 * 1_048_576;

    /// <summary>The bytes of every file in <paramref name="contents"/>.</summary>
    public static long ByteCount(PackageContents contents) => contents.ByteCount;

    /// <summary>
    /// Removes <paramref name="destination"/> when it is an empty folder or an empty file: one left behind after a failed
    /// save looks like a saved document that can't be opened.
    /// </summary>
    internal static void RemoveIfEmpty(string destination)
    {
        try
        {
            if (Directory.Exists(destination) && !Directory.EnumerateFileSystemEntries(destination).Any()) Directory.Delete(destination);
            else if (File.Exists(destination) && new FileInfo(destination).Length == 0) File.Delete(destination);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <exception cref="DocumentSaveException">The disk is too full.</exception>
    public static void Write(PackageContents contents, string destination)
    {
        try
        {
            WriteChecked(contents, Path.GetFullPath(destination));
        }
        catch
        {
            RemoveIfEmpty(destination);
            throw;
        }
    }

    private static void WriteChecked(PackageContents contents, string destination)
    {
        long needed = ByteCount(contents) + Margin;
        var folder = Path.GetDirectoryName(destination) ?? ".";
        var volume = FileSystemLinks.VolumeName(folder) ?? "the disk";
        if (FileSystemLinks.FreeBytes(folder) is { } free && free < needed) throw new DocumentSaveException(needed, free, volume);

        bool existedAsFile = File.Exists(destination);
        bool existedAsFolder = Directory.Exists(destination);
        var temp = Path.Combine(folder, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.saving");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                contents.WriteZip(stream);
                stream.Flush(flushToDisk: true);
            }
            if (existedAsFolder)
            {
                // A package folder (from a Mac) becomes the file: moved aside, replaced, then removed.
                var aside = $"{destination}.{Guid.NewGuid():N}.previous";
                Directory.Move(destination, aside);
                try
                {
                    File.Move(temp, destination);
                }
                catch
                {
                    Directory.Move(aside, destination);
                    throw;
                }
                try { Directory.Delete(aside, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            else
            {
                File.Move(temp, destination, overwrite: true);
            }
            FileSystemLinks.Sync(folder);
        }
        catch (Exception error)
        {
            // Nothing half-written may stay behind, where it would look like a saved document.
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (!existedAsFile && !existedAsFolder && File.Exists(destination) && new FileInfo(destination).Length == 0)
            {
                try { File.Delete(destination); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            if (IsOutOfSpace(error)) throw new DocumentSaveException(needed, FileSystemLinks.FreeBytes(folder) ?? 0, volume);
            throw;
        }
    }

    /// <summary>A write that failed for want of room: ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL on Windows, ENOSPC elsewhere.</summary>
    internal static bool IsOutOfSpace(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is not IOException io) continue;
            int code = io.HResult;
            bool windowsFull = code == unchecked((int)0x80070070) || code == unchecked((int)0x80070027);
            bool posixFull = !OperatingSystem.IsWindows() && code == 28;   // .NET puts errno in HResult on Unix
            if (windowsFull || posixFull) return true;
        }
        return false;
    }
}

/// <summary>Byte counts in words, as macOS's <c>ByteCountFormatter</c> with <c>.file</c> writes them (decimal units).</summary>
internal static class ByteCountText
{
    public static string File(long bytes)
    {
        if (bytes == 0) return "Zero KB";
        if (Math.Abs(bytes) < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        int unit = -1;
        while (Math.Abs(value) >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        // Whole kilobytes; one decimal for megabytes; two from gigabytes up; no trailing zeros.
        int decimals = unit == 0 ? 0 : unit == 1 ? 1 : 2;
        var text = Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString("0." + new string('#', Math.Max(decimals, 1)), CultureInfo.InvariantCulture);
        if (decimals == 0) text = Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        return $"{text} {units[unit]}";
    }
}
