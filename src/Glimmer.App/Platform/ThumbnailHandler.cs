// New file, no Swift original. File Explorer thumbnails for .glimmer documents, as QuickLook gives them in Finder: the
// native handler in src/Glimmer.Thumbnails (it shows the QuickLook/Thumbnail.png every saved document carries), kept
// inside Glimmer.exe for x64 and ARM64, written to %LOCALAPPDATA%\Glimmer\Shell under a name that carries its content's
// hash (a newer one never has to replace a file Explorer may still have loaded), and registered for the current user
// under HKEY_CURRENT_USER\Software\Classes: no administrator rights, nothing for other people on the computer.
// Registered with the .glimmer file type (FileAssociations.RegisterDocuments) and taken back with it. Explorer runs the
// handler in a surrogate process of its own; Glimmer itself never loads it.
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Glimmer.App.Platform;

public static class ThumbnailHandler
{
    /// <summary>The handler's class (ThumbnailProvider.cpp's CLSID_GlimmerThumbnails).</summary>
    public const string ClassId = "{bccdb912-b48b-4f71-ae30-b3dda8d16651}";
    /// <summary>IThumbnailProvider: the key under a file type's ShellEx that names its thumbnail handler.</summary>
    private const string ThumbnailProvider = "{e357fccd-a995-4576-b01f-234630154e96}";
    private const string ClassesPath = @"Software\Classes";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glimmer", "Shell");

    /// <summary>The handler for Explorer's architecture, which is the computer's (an x64 Glimmer emulated on an ARM64
    /// computer still needs the ARM64 handler); null for one without a handler.</summary>
    private static string? Resource => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "Glimmer.Thumbnails.x64.dll",
        Architecture.Arm64 => "Glimmer.Thumbnails.arm64.dll",
        _ => null,
    };

    /// <summary>Writes the handler when it isn't there yet and registers it for .glimmer; tells Explorer only when
    /// something changed. False when this computer's architecture has no handler.</summary>
    public static bool Register()
    {
        if (Resource is not { } resource) return false;
        byte[] bytes;
        using (var stream = typeof(ThumbnailHandler).Assembly.GetManifestResourceStream(resource))
        {
            if (stream is null) return false;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        var hash = SHA256.HashData(bytes);
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"GlimmerThumbnails-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}.dll");
        bool changed = false;
        if (!File.Exists(path) || !SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(hash))
        {
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            changed = true;
        }
        using (var classes = Registry.CurrentUser.CreateSubKey(ClassesPath))
        {
            using (var server = classes.CreateSubKey($@"CLSID\{ClassId}\InprocServer32"))
            {
                changed |= Set(server, "", path) | Set(server, "ThreadingModel", "Apartment");
            }
            using (var type = classes.CreateSubKey($@"CLSID\{ClassId}")) changed |= Set(type, "", "Glimmer Thumbnail Handler");
            using var handler = classes.CreateSubKey($@".glimmer\ShellEx\{ThumbnailProvider}");
            changed |= Set(handler, "", ClassId);
        }
        // Listed as approved for this user too: on a computer whose policy allows "only per user or approved shell
        // extensions", Explorer runs a per-user handler only when it is listed here.
        using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedPath)) changed |= Set(approved, ClassId, "Glimmer Thumbnail Handler");
        RemoveOthers(keep: path);
        if (changed) SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    /// <summary>Takes the registration back and removes the handler files Explorer doesn't hold (one it still has
    /// loaded goes the next time).</summary>
    public static void Unregister()
    {
        using (var classes = Registry.CurrentUser.OpenSubKey(ClassesPath, writable: true))
        {
            if (classes is not null)
            {
                classes.DeleteSubKeyTree($@"CLSID\{ClassId}", throwOnMissingSubKey: false);
                classes.DeleteSubKeyTree($@".glimmer\ShellEx\{ThumbnailProvider}", throwOnMissingSubKey: false);
            }
        }
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: true)) approved?.DeleteValue(ClassId, throwOnMissingValue: false);
        RemoveOthers(keep: null);
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Sets a string value; true when it was different.</summary>
    private static bool Set(RegistryKey key, string name, string value)
    {
        if (key.GetValue(name) as string == value) return false;
        key.SetValue(name, value);
        return true;
    }

    private static void RemoveOthers(string? keep)
    {
        if (!Directory.Exists(Folder)) return;
        foreach (var file in Directory.EnumerateFiles(Folder, "GlimmerThumbnails-*"))
        {
            if (keep is not null && string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // still loaded by Explorer
        }
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
