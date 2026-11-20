// Small Windows services the editor asks the app for: the app's sound (AppSound.swift), the Recycle Bin
// (FileManager.trashItem) and the recent-documents list (noteNewRecentDocumentURL).
using System.IO;
using System.Runtime.InteropServices;

namespace Glimmer.App.Platform;

/// <summary>IAppSound: Windows' default beep, as NSSound.beep is the Mac's (the editor checks "quiet" first).</summary>
public sealed class AppSoundPlayer : IAppSound
{
    public void Beep() => System.Media.SystemSounds.Beep.Play();
}

/// <summary>Moves a file or folder to the Recycle Bin without asking or showing progress; false when Windows couldn't.</summary>
public static class RecycleBin
{
    public static bool Move(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        var operation = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",   // a double-null-terminated list of one
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperationW(ref operation) == 0 && !operation.fAnyOperationsAborted;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW operation);
}

/// <summary>Windows' recent documents (the jump list and Explorer's Recent), and the app's own short list.</summary>
public static class RecentDocuments
{
    public const string Key = "documents.recent";
    private const int Kept = 12;

    public static IReadOnlyList<string> All => AppServices.Settings.Get<List<string>>(Key, []);

    public static void Note(string path)
    {
        var full = Path.GetFullPath(path);
        var list = AppServices.Settings.Get<List<string>>(Key, []);
        list.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, full);
        if (list.Count > Kept) list.RemoveRange(Kept, list.Count - Kept);
        AppServices.Settings.Set(Key, list);
        try { SHAddToRecentDocs(SHARD_PATHW, full); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private const uint SHARD_PATHW = 3;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHAddToRecentDocs(uint flags, string path);
}
