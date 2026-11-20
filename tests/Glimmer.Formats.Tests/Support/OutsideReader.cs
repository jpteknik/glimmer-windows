// New file, no Swift original. The Swift tests check written files with the one outside reader every Mac has
// (ImageIO). Linux and Windows have none built in for PSD or TIFF, so these tests use Python's Pillow and tifffile
// (independent implementations) when they are installed, and are skipped, with the reason, when they are not:
//   python3 -m pip install --user pillow numpy tifffile
using System.Diagnostics;
using System.Text.Json;

namespace Glimmer.Formats.Tests;

internal static class OutsideReader
{
    private static readonly Lazy<string?> Python = new(FindPython);
    private static readonly Dictionary<string, bool> Modules = [];

    private static string? FindPython()
    {
        foreach (var candidate in new[] { "python3", "python" })
        {
            try
            {
                var (code, output, _) = Run(candidate, ["-I", "-c", "import sys; print(sys.version_info[0])"], TimeSpan.FromSeconds(20));
                if (code == 0 && output.Trim() == "3") return candidate;
            }
            catch (System.ComponentModel.Win32Exception) { }   // not installed
        }
        return null;
    }

    /// <summary>Why the outside reader can't be used here, or null when it can.</summary>
    public static string? Missing(params string[] modules)
    {
        if (Python.Value is null) return "no python3 on this computer: the outside reader can't run";
        lock (Modules)
        {
            foreach (var module in modules)
            {
                if (!Modules.TryGetValue(module, out bool present))
                {
                    // -I leaves the user's own site-packages (where pip --user installs) out; the scripts add it back by name.
                    var (code, _, _) = Run(Python.Value, ["-I", "-c", $"import site, sys; sys.path.append(site.getusersitepackages()); import {module}"], TimeSpan.FromSeconds(60));
                    Modules[module] = present = code == 0;
                }
                if (!present) return $"python3 has no '{module}' module: python3 -m pip install --user pillow numpy tifffile";
            }
        }
        return null;
    }

    /// <summary>Runs one of the scripts beside the tests on a file and returns what it printed as JSON.</summary>
    public static JsonElement Read(string script, string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Support", script);
        // -I: isolated, so nothing from the current folder or the user's site is loaded in place of the real modules.
        var (code, output, error) = Run(Python.Value!, ["-I", path, file], TimeSpan.FromMinutes(2));
        if (code != 0) throw new InvalidOperationException($"{script} failed ({code}): {error}");
        return JsonDocument.Parse(output).RootElement.Clone();
    }

    /// <summary>Runs a script that takes many files and prints one JSON line for each.</summary>
    public static List<JsonElement> ReadMany(string script, IReadOnlyList<string> files)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Support", script);
        var (code, output, error) = Run(Python.Value!, ["-I", path, .. files], TimeSpan.FromMinutes(30));
        if (code != 0) throw new InvalidOperationException($"{script} failed ({code}): {error}");
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone())];
    }

    private static (int Code, string Output, string Error) Run(string program, string[] arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout)) { process.Kill(entireProcessTree: true); throw new TimeoutException($"{program} took longer than {timeout}."); }
        return (process.ExitCode, output.Result, error.Result);
    }
}

/// <summary>A test that needs the outside reader: skipped, with the reason, where Python or a module is missing.</summary>
public sealed class OutsideReaderFactAttribute : FactAttribute
{
    public OutsideReaderFactAttribute(params string[] modules)
    {
        if (OutsideReader.Missing(modules) is { } why) Skip = why;
    }
}

public sealed class OutsideReaderTheoryAttribute : TheoryAttribute
{
    public OutsideReaderTheoryAttribute(params string[] modules)
    {
        if (OutsideReader.Missing(modules) is { } why) Skip = why;
    }
}
