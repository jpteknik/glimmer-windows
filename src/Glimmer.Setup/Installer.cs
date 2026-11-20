// New file, no Swift original: what the installer and uninstaller do (Program.cs says when). Everything is the current
// user's: files under %LOCALAPPDATA%\Programs\Glimmer, keys under HKEY_CURRENT_USER, a shortcut in the user's Start menu.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Glimmer.Setup;

[SupportedOSPlatform("windows")]
internal static partial class Installer
{
    private static readonly string[] Files = ["Glimmer.exe", "Glimmer.com", "Uninstall.exe"];
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Glimmer";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Glimmer");

    /// <summary>The app's own data (settings, models, recovery copies), which uninstalling keeps.</summary>
    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glimmer");

    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Glimmer.lnk");

    public static string Version => typeof(Installer).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    public static bool HasPayload => typeof(Installer).Assembly.GetManifestResourceNames().Contains("Payload/Glimmer.exe");

    /// <summary>A Glimmer file in <paramref name="folder"/> that can't be replaced now (a running Glimmer), or null.</summary>
    public static string? InUse(string folder)
    {
        foreach (var name in Files.Take(2))
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            try { using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return path; }
        }
        return null;
    }

    public static void Install()
    {
        Directory.CreateDirectory(Folder);
        long bytes = 0;
        foreach (var name in Files)
        {
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload/" + name)
                ?? throw new InvalidOperationException($"The installer is missing {name}.");
            var target = Path.Combine(Folder, name);
            var temporary = target + ".new";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                payload.CopyTo(file);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
            bytes += new FileInfo(target).Length;
        }
        var app = Path.Combine(Folder, "Glimmer.exe");
        ShellLink.Create(Shortcut, app, Folder, "Glimmer image editor");
        ChangePath(add: true);
        // Uninstallable from here on, even if the file types below fail.
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            var uninstaller = Path.Combine(Folder, "Uninstall.exe");
            key.SetValue("DisplayName", "Glimmer");
            key.SetValue("DisplayVersion", Version);
            // The Windows port's publisher, who is responsible for it (the Mac original is Anders Bjarby's, credited in the
            // licence and the README, but he doesn't publish this build).
            key.SetValue("Publisher", "Johan Pålsson");
            key.SetValue("DisplayIcon", app + ",0");
            key.SetValue("InstallLocation", Folder);
            key.SetValue("UninstallString", $"\"{uninstaller}\"");
            key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /S");
            key.SetValue("URLInfoAbout", "https://github.com/fltman/glimmer-native");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
        }
        RunApp(app, "--register-file-types");
    }

    public static void Uninstall()
    {
        var app = Path.Combine(Folder, "Glimmer.exe");
        if (File.Exists(app)) RunApp(app, "--unregister-file-types");
        if (File.Exists(Shortcut)) File.Delete(Shortcut);
        ChangePath(add: false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        foreach (var name in Files.Take(2))
        {
            var path = Path.Combine(Folder, name);
            if (File.Exists(path)) File.Delete(path);
        }
        // This uninstaller is in the folder and can't delete itself while it runs: a hidden command removes the folder a
        // moment after it has ended.
        var own = Environment.ProcessPath;
        if (own is not null && string.Equals(Path.GetDirectoryName(own), Folder, StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{Folder}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            });
        }
        else if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }
    }

    /// <summary>Runs the installed Glimmer for one of its installer steps and waits; throws when it reports a failure.</summary>
    private static void RunApp(string app, string step)
    {
        using var process = Process.Start(new ProcessStartInfo(app, step) { UseShellExecute = false, WorkingDirectory = Folder })
            ?? throw new InvalidOperationException("Glimmer could not be started.");
        if (!process.WaitForExit(TimeSpan.FromMinutes(2))) throw new InvalidOperationException($"Glimmer didn't finish {step}.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException(step == "--register-file-types"
                ? "Glimmer is installed, but its file types couldn't be registered. Settings › General › Make Glimmer the default for .glimmer files does it."
                : "Glimmer's file types couldn't be taken back.");
    }

    /// <summary>The install folder added to or taken off the user's PATH, then every window told the environment changed
    /// (terminals opened after this see it).</summary>
    private static void ChangePath(bool add)
    {
        using (var environment = Registry.CurrentUser.CreateSubKey("Environment"))
        {
            // Read without expanding, so %VARIABLES% in the user's PATH are kept as they are.
            var current = environment.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var kind = current is null ? RegistryValueKind.ExpandString : environment.GetValueKind("Path");
            var changed = add ? PathList.Add(current, Folder) : PathList.Remove(current, Folder);
            if (changed == current) return;
            if (changed.Length == 0) environment.DeleteValue("Path", throwOnMissingValue: false);
            else environment.SetValue("Path", changed, kind is RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
        }
        _ = SendMessageTimeoutW(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out _);   // WM_SETTINGCHANGE
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeoutW(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}

/// <summary>A PATH value (folders separated by semicolons) with one folder added or taken off; everything else in it is
/// kept as it was, in order. Folders compare without case and without a trailing backslash.</summary>
internal static class PathList
{
    private static string Normal(string entry) => entry.Trim().TrimEnd('\\', '/');

    public static bool Contains(string? path, string folder) =>
        (path ?? "").Split(';').Any(e => string.Equals(Normal(e), Normal(folder), StringComparison.OrdinalIgnoreCase));

    public static string Add(string? path, string folder)
    {
        if (Contains(path, folder)) return path!;
        var kept = (path ?? "").TrimEnd(';');
        return kept.Length == 0 ? folder : kept + ";" + folder;
    }

    public static string Remove(string? path, string folder)
    {
        if (!Contains(path, folder)) return path ?? "";
        return string.Join(';', path!.Split(';').Where(e => !string.Equals(Normal(e), Normal(folder), StringComparison.OrdinalIgnoreCase)));
    }
}
