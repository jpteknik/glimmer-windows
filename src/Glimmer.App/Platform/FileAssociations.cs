// "Make Glimmer the default for .glimmer files" (Settings › General), and "Open with Glimmer" for images, Photoshop
// documents and web projects. Everything goes under HKEY_CURRENT_USER\Software\Classes: no administrator rights, and
// nothing changes for other people on the computer. Windows protects the default app of a type a person has already
// chosen (the UserChoice key is signed): for .glimmer, which nothing else claims, this makes Glimmer the default; for
// the other types Glimmer only joins the "Open with" list, and Windows asks the person the next time they open one.
using System.IO;
using System.Runtime.InteropServices;
using Glimmer.App.Localization;
using Microsoft.Win32;

namespace Glimmer.App.Platform;

public static class FileAssociations
{
    public const string DocumentProgId = "Glimmer.Document";
    public const string ImageProgId = "Glimmer.Image";
    public const string ProjectProgId = "Glimmer.WebProject";
    private const string ClassesPath = @"Software\Classes";

    private static string Executable => Environment.ProcessPath ?? throw new InvalidOperationException("The app's own path is unknown.");

    /// <summary>Whether .glimmer opens with this copy of Glimmer (its ProgID points at this executable).</summary>
    public static bool IsDefaultForDocuments
    {
        get
        {
            using var ext = Registry.CurrentUser.OpenSubKey($@"{ClassesPath}\.glimmer");
            using var command = Registry.CurrentUser.OpenSubKey($@"{ClassesPath}\{DocumentProgId}\shell\open\command");
            return ext?.GetValue("") as string == DocumentProgId
                && (command?.GetValue("") as string)?.Contains(Executable, StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    public static void RegisterDocuments()
    {
        using var classes = Registry.CurrentUser.CreateSubKey(ClassesPath);
        ProgId(classes, DocumentProgId, Loc.T("Glimmer Document"));
        using (var ext = classes.CreateSubKey(".glimmer"))
        {
            ext.SetValue("", DocumentProgId);
            using var with = ext.CreateSubKey("OpenWithProgids");
            with.SetValue(DocumentProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
        ProgId(classes, ImageProgId, Loc.T("Image"));
        ProgId(classes, ProjectProgId, Loc.T("Web Project"));
        foreach (var e in FileDialogs.ImageExtensions.Concat(FileDialogs.RawExtensions).Concat(FileDialogs.PhotoshopExtensions))
            OpenWith(classes, "." + e, ImageProgId);
        foreach (var e in FileDialogs.ProjectExtensions) OpenWith(classes, "." + e, ProjectProgId);
        // Listed under "Open with › Choose another app", with the types it opens.
        using (var app = classes.CreateSubKey($@"Applications\{Path.GetFileName(Executable)}"))
        {
            app.SetValue("FriendlyAppName", "Glimmer");
            using (var command = app.CreateSubKey(@"shell\open\command")) command.SetValue("", Command);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var e in FileDialogs.DocumentExtensions.Concat(FileDialogs.ImageExtensions).Concat(FileDialogs.RawExtensions)
                         .Concat(FileDialogs.PhotoshopExtensions).Concat(FileDialogs.ProjectExtensions))
                types.SetValue("." + e, "");
        }
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        // File Explorer thumbnails of .glimmer documents come with the file type. They are an extra: if they can't be
        // registered, the documents still open with Glimmer.
        try { ThumbnailHandler.Register(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            CrashLog.Note("Thumbnail handler not registered: " + e.Message);
        }
    }

    /// <summary>Takes back everything RegisterDocuments wrote (for an uninstaller, or "stop").</summary>
    public static void Unregister()
    {
        using var classes = Registry.CurrentUser.OpenSubKey(ClassesPath, writable: true);
        if (classes is null) return;
        foreach (var id in new[] { DocumentProgId, ImageProgId, ProjectProgId }) classes.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
        foreach (var name in classes.GetSubKeyNames().Where(n => n.StartsWith('.')))
        {
            using var ext = classes.OpenSubKey(name, writable: true);
            if (ext is null) continue;
            using (var with = ext.OpenSubKey("OpenWithProgids", writable: true))
                foreach (var id in new[] { DocumentProgId, ImageProgId, ProjectProgId }) with?.DeleteValue(id, throwOnMissingValue: false);
            if (ext.GetValue("") as string == DocumentProgId) ext.DeleteValue("");
        }
        classes.DeleteSubKeyTree($@"Applications\{Path.GetFileName(Executable)}", throwOnMissingSubKey: false);
        ThumbnailHandler.Unregister();
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    private static string Command => $"\"{Executable}\" \"%1\"";

    private static void ProgId(RegistryKey classes, string id, string name)
    {
        using var key = classes.CreateSubKey(id);
        key.SetValue("", name);
        key.SetValue("FriendlyTypeName", name);
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{Executable}\",0");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", Command);
    }

    private static void OpenWith(RegistryKey classes, string extension, string progId)
    {
        using var ext = classes.CreateSubKey(extension);
        using var with = ext.CreateSubKey("OpenWithProgids");
        with.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
