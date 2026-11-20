// The file-system calls the Mac code makes through Foundation and libc (link, stat, fsync, volume capacity), for
// Windows and Linux. Used by UndoSnapshot.cs, DocumentSaver.cs and LinkJournalStore.cs. No Swift file of its own.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Glimmer.Core;

internal static partial class FileSystemLinks
{
    /// <summary>The ways creating a hard link can fail, as the Mac code tells them apart (errno ENOENT, EXDEV, other).</summary>
    internal enum LinkFailure { None, SourceMissing, DifferentVolume, Other }

    /// <summary>Makes <paramref name="newPath"/> another name of <paramref name="existingPath"/> (POSIX <c>link</c>).</summary>
    internal static (LinkFailure Failure, int Code) CreateHardLink(string existingPath, string newPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (CreateHardLinkW(newPath, existingPath, IntPtr.Zero)) return (LinkFailure.None, 0);
            int error = Marshal.GetLastPInvokeError();
            return error switch
            {
                2 or 3 => (LinkFailure.SourceMissing, error),        // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND
                17 => (LinkFailure.DifferentVolume, error),          // ERROR_NOT_SAME_DEVICE
                _ => (LinkFailure.Other, error),
            };
        }
        if (link(newPath: newPath, oldPath: existingPath) == 0) return (LinkFailure.None, 0);
        int errno = Marshal.GetLastPInvokeError();
        return errno switch
        {
            2 => (LinkFailure.SourceMissing, errno),                 // ENOENT
            18 => (LinkFailure.DifferentVolume, errno),              // EXDEV (Linux and macOS)
            _ => (LinkFailure.Other, errno),
        };
    }

    /// <summary>How many names a file has (1 for a file nothing else links to); 0 when it can't be read.</summary>
    internal static int LinkCount(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsInfo(path) is { } info ? (int)info.NumberOfLinks : 0;
        }
        if (OperatingSystem.IsLinux())
        {
            return Statx(path) is { } s ? (int)s.NLink : 0;
        }
        return 0;
    }

    /// <summary>The file's identity on its volume (inode, or Windows' file index), or null.</summary>
    internal static (ulong Volume, ulong File)? FileId(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsInfo(path) is { } info
                ? (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
                : null;
        }
        if (OperatingSystem.IsLinux())
        {
            return Statx(path) is { } s ? (((ulong)s.DevMajor << 32) | s.DevMinor, s.Inode) : null;
        }
        return null;
    }

    /// <summary>The volume a path is on, or null when that can't be read.</summary>
    internal static ulong? VolumeId(string path) => FileId(path)?.Volume;

    /// <summary>
    /// Bytes free for Glimmer's use on the volume that holds <paramref name="path"/> (walking up to a folder that
    /// exists), or null when it can't be read.
    /// </summary>
    internal static long? FreeBytes(string path)
    {
        try
        {
            var probe = Path.GetFullPath(path);
            while (!Directory.Exists(probe) && !File.Exists(probe))
            {
                var parent = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent) || parent == probe) return null;
                probe = parent;
            }
            if (File.Exists(probe)) probe = Path.GetDirectoryName(probe)!;
            if (OperatingSystem.IsWindows())
            {
                return GetDiskFreeSpaceExW(probe, out ulong available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : null;
            }
            return new DriveInfo(probe).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The volume's name as a person knows it ("Data", or "C:" when it has no label), or null.</summary>
    internal static string? VolumeName(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            if (OperatingSystem.IsWindows())
            {
                var drive = new DriveInfo(root);
                var label = drive.IsReady ? drive.VolumeLabel : "";
                return string.IsNullOrWhiteSpace(label) ? root.TrimEnd('\\') : label;
            }
            return root;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks the system to put a file's bytes, or a folder's entries (a rename, a new file), on the disk now (<c>fsync</c>).
    /// On Windows a folder needs nothing: NTFS journals its entries.
    /// </summary>
    internal static void Sync(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                stream.Flush(flushToDisk: true);
                return;
            }
            if (Directory.Exists(path) && !OperatingSystem.IsWindows())
            {
                int fd = open(path, 0 /* O_RDONLY */);
                if (fd < 0) return;
                _ = fsync(fd);
                _ = close(fd);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort, as on the Mac: a sync that can't be done is not an error.
        }
    }

    // MARK: Windows

    private readonly record struct WinFileInfo(uint VolumeSerialNumber, uint FileIndexHigh, uint FileIndexLow, uint NumberOfLinks);

    private static WinFileInfo? WindowsInfo(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var info)) return null;
            return new WinFileInfo(info.dwVolumeSerialNumber, info.nFileIndexHigh, info.nFileIndexLow, info.nNumberOfLinks);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>BY_HANDLE_FILE_INFORMATION; its FILETIMEs are 4-byte aligned, hence <c>Pack = 4</c>.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong lpFreeBytesAvailableToCaller,
                                                    out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    // MARK: Linux and other Unix systems

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int link(string oldPath, string newPath);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int fsync(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int close(int fd);

    /// <summary>
    /// <c>statx(2)</c>: unlike <c>stat</c>, its structure is the same on every Linux architecture.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int statx(int dirfd, string path, int flags, uint mask, byte* buffer);

    private readonly record struct StatxResult(uint NLink, ulong Inode, uint DevMajor, uint DevMinor);

    private static unsafe StatxResult? Statx(string path)
    {
        const int AtFdCwd = -100;
        const uint StatxNlink = 0x4, StatxIno = 0x100;
        byte* buffer = stackalloc byte[256];
        try
        {
            if (statx(AtFdCwd, path, 0, StatxNlink | StatxIno, buffer) != 0) return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        // Offsets in struct statx: stx_nlink 0x10, stx_ino 0x20, stx_dev_major 0x88, stx_dev_minor 0x8c.
        return new StatxResult(*(uint*)(buffer + 0x10), *(ulong*)(buffer + 0x20), *(uint*)(buffer + 0x88), *(uint*)(buffer + 0x8c));
    }
}
