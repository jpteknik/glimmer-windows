// New file, no Swift original. The logic of Glimmer.com, the console companion of the windowed Glimmer.exe: find the GUI
// exe, and either run a command through it with its output relayed (waiting, and returning its exit status) or start the
// window and return at once. Kept separate from Program so the tests can drive it with a stand-in child process.
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Glimmer.Editor;

namespace Glimmer.Console;

internal static class Companion
{
    public const int Failed = 1, Interrupted = 130;

    /// <summary>The environment variable that makes Glimmer.exe write its console output as UTF-8 without a BOM.</summary>
    public const string EncodingVariable = "GLIMMER_OUTPUT_ENCODING";

    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>The GUI exe this companion starts: next to it, its own name with .exe (so Glimmer-0.1.0-win-x64.com starts
    /// Glimmer-0.1.0-win-x64.exe), else Glimmer.exe there. Null when neither exists.</summary>
    public static string? FindGui(string? ownPath)
    {
        var folder = ownPath is null ? AppContext.BaseDirectory : Path.GetDirectoryName(ownPath) ?? ".";
        var candidates = new List<string>();
        if (ownPath is not null) candidates.Add(Path.Combine(folder, Path.GetFileNameWithoutExtension(ownPath) + ".exe"));
        candidates.Add(Path.Combine(folder, "Glimmer.exe"));
        foreach (var candidate in candidates)
        {
            // Never the companion itself (a build output called Glimmer.Console.exe would otherwise find itself).
            if (ownPath is not null && string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(ownPath), StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The whole companion: <paramref name="ownPath"/> is this program's own path (Environment.ProcessPath).</summary>
    public static int Execute(string[] args, string? ownPath, TextWriter output, TextWriter error, CancellationToken cancel)
    {
        var gui = FindGui(ownPath);
        if (gui is null)
        {
            var shown = ownPath is null ? AppContext.BaseDirectory : Path.GetDirectoryName(ownPath);
            error.WriteLine($"Glimmer.exe was not found next to {(ownPath is null ? "this program" : Path.GetFileName(ownPath))} in {shown}.");
            return Failed;
        }
        return CommandLineWords.IsCommand(args) ? Relay(gui, args, output, error, cancel) : Open(gui, args, error);
    }

    /// <summary>Starts <paramref name="fileName"/> with <paramref name="args"/>, no window, both output streams piped, and
    /// copies them to <paramref name="output"/> and <paramref name="error"/> as they arrive. Returns the child's exit code;
    /// when <paramref name="cancel"/> fires (Ctrl+C), the child and everything it started is killed and the result is
    /// <see cref="Interrupted"/>.</summary>
    public static int Relay(string fileName, IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        start.Environment[EncodingVariable] = "utf-8";
        Process? child;
        try { child = Process.Start(start); }
        catch (Exception problem) when (problem is Win32Exception or IOException or InvalidOperationException)
        {
            error.WriteLine($"Glimmer could not be started: {problem.Message}");
            return Failed;
        }
        if (child is null) { error.WriteLine("Glimmer could not be started."); return Failed; }
        using (child)
        {
            using var killOnCancel = cancel.Register(() => Kill(child));
            var out1 = Pump(child.StandardOutput, output);
            var err1 = Pump(child.StandardError, error);
            child.WaitForExit();
            // The pipes are at their end once the child (and any process it left holding them) is gone.
            Task.WaitAll(out1, err1);
            return cancel.IsCancellationRequested ? Interrupted : child.ExitCode;
        }
    }

    /// <summary>Anything that is not a command (no arguments, file paths): the window, detached; return at once.</summary>
    public static int Open(string fileName, IReadOnlyList<string> args, TextWriter error)
    {
        // On Windows ShellExecute starts the window apart from this console's handles, so a pipe the caller redirected
        // is not held open by a window that stays. Elsewhere (the tests) the plain start is the only one there is.
        var start = new ProcessStartInfo(fileName) { UseShellExecute = OperatingSystem.IsWindows() };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            return 0;
        }
        catch (Exception problem) when (problem is Win32Exception or IOException or InvalidOperationException)
        {
            error.WriteLine($"Glimmer could not be started: {problem.Message}");
            return Failed;
        }
    }

    private static Task Pump(StreamReader from, TextWriter to) => Task.Run(() =>
    {
        var buffer = new char[4096];
        int count;
        while ((count = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            to.Write(buffer, 0, count);
            to.Flush();
        }
    });

    private static void Kill(Process child)
    {
        try { child.Kill(entireProcessTree: true); }
        catch (Exception problem) when (problem is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }
}
