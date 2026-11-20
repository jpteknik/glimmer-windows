// New file, no Swift original (the Mac app is a drag-to-Applications disk image). Glimmer for Windows' installer and
// uninstaller in one program.
//
// With its payload (Glimmer.exe, Glimmer.com, Uninstall.exe, embedded by the second build in scripts/publish.*) it
// installs for the current user, with no administrator rights:
//   - the three files in %LOCALAPPDATA%\Programs\Glimmer (an update replaces them; a running Glimmer is asked to close);
//   - a Start menu shortcut;
//   - that folder on the user's PATH, so "glimmer" in a new terminal is the console companion (Glimmer.com);
//   - .glimmer documents opening with Glimmer, its File Explorer thumbnails and "Open with" for pictures, registered by
//     the installed Glimmer.exe itself (--register-file-types), exactly as Settings › General does it;
//   - an entry under Settings › Apps, whose Uninstall runs Uninstall.exe.
// Without a payload it is that Uninstall.exe: it takes all of the above back and removes the folder, and keeps the
// person's own data (%LOCALAPPDATA%\Glimmer: settings, downloaded models, recovery copies) and documents.
//
// /S (or --silent) asks nothing and shows nothing, for scripted installs; the exit status is 0 when it worked.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Glimmer.Setup;

internal static partial class Program
{
    private const string Title = "Glimmer Setup";

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        bool silent = args.Any(a => a is "/S" or "/s" or "--silent");
        bool uninstall = args.Any(a => a is "/uninstall" or "--uninstall") || !Installer.HasPayload;
        try
        {
            return uninstall ? Uninstall(silent) : Install(silent);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                          or COMException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!silent) Show($"{(uninstall ? "Glimmer couldn't be uninstalled" : "Glimmer couldn't be installed")}: {error.Message}", Icon.Error);
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int Install(bool silent)
    {
        var folder = Installer.Folder;
        if (!silent && Ask($"Install Glimmer {Installer.Version} for {Environment.UserName}?\n\n" +
                           $"It goes in {folder}, with a Start menu shortcut, \"glimmer\" on the command line (in new terminals), " +
                           ".glimmer documents opening with Glimmer and their pictures in File Explorer.\n\nNo administrator rights are needed.",
                           Buttons.OkCancel) != Answer.Ok)
            return 1;
        if (!WaitUntilClosed(folder, silent)) return 1;
        Installer.Install();
        if (!silent && Ask($"Glimmer {Installer.Version} is installed. Start it now?", Buttons.YesNo) == Answer.Yes)
            Process.Start(new ProcessStartInfo(Path.Combine(folder, "Glimmer.exe")) { UseShellExecute = true });
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int Uninstall(bool silent)
    {
        var folder = Installer.Folder;
        if (!silent && Ask("Uninstall Glimmer?\n\nYour documents, settings and downloaded models are kept " +
                           $"(the settings and models are in {Installer.DataFolder}).", Buttons.OkCancel) != Answer.Ok)
            return 1;
        if (!WaitUntilClosed(folder, silent)) return 1;
        Installer.Uninstall();
        if (!silent) Show("Glimmer is uninstalled.", Icon.Information);
        return 0;
    }

    /// <summary>Glimmer can't be replaced or removed while it runs: until its files are free, asks to close it (silent:
    /// gives up at once).</summary>
    [SupportedOSPlatform("windows")]
    private static bool WaitUntilClosed(string folder, bool silent)
    {
        while (Installer.InUse(folder) is { } file)
        {
            if (silent || Ask($"Glimmer is running ({Path.GetFileName(file)}). Close it, then choose Retry.", Buttons.RetryCancel) != Answer.Retry)
                return false;
        }
        return true;
    }

    private enum Buttons : uint { OkCancel = 0x1, YesNo = 0x4, RetryCancel = 0x5 }
    private enum Icon : uint { Error = 0x10, Question = 0x20, Information = 0x40 }
    private enum Answer { Ok = 1, Cancel = 2, Retry = 4, Yes = 6, No = 7 }

    private static Answer Ask(string text, Buttons buttons) => (Answer)MessageBoxW(IntPtr.Zero, text, Title, (uint)buttons | (uint)Icon.Question);

    private static void Show(string text, Icon icon) => _ = MessageBoxW(IntPtr.Zero, text, Title, (uint)icon);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
