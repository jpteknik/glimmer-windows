// Ported from App/Intents.swift: the Shortcuts actions (Auto Enhance Image, Remove Background, Apply Glimmer Preset,
// Apply Glimmer Filter, Run Glimmer Action, Export Glimmer Document) as a command line for Glimmer.exe. Windows has no
// Shortcuts; a script or the Task Scheduler calls the app instead. Headless: no window opens, and the same code Batch
// Process runs does the work (HeadlessEditor).
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;

namespace Glimmer.Editor;

/// <summary>Glimmer.exe --auto-enhance in.jpg out.jpg, and the other actions. The app calls <see cref="Run"/> before it
/// shows a window when <see cref="IsCommand"/> says the arguments are one, after it has installed the platform decoder and
/// encoder (ImageImporter.PlatformDecoder, ImageExporter.PlatformEncoder) as for any run.</summary>
public static class GlimmerCommandLine
{
    public const int Success = 0, Failed = 1, Usage = 2;

    /// <summary>The filters --apply-filter takes (the Shortcuts action's list).</summary>
    public static readonly IReadOnlyList<string> Filters =
    [
        "gaussian_blur", "lens_blur", "motion_blur", "sharpen", "unsharp_mask", "clarity", "add_noise", "pixelate", "find_edges",
        "emboss", "oil_paint", "halftone", "vignette", "chromatic_aberration",
    ];

    public const string Help =
        """
        Glimmer from the command line: one image in, one image out, no window.

          Glimmer.exe --auto-enhance <input> <output> [--format <format>]
          Glimmer.exe --remove-background <input> <output> [--format <format>]
          Glimmer.exe --apply-preset <preset> <input> <output> [--format <format>]
          Glimmer.exe --apply-filter <filter> <input> <output> [--format <format>]
          Glimmer.exe --run-action <action> <input> <output> [--format <format>]
          Glimmer.exe --export <input> <output> [--format <format>]

        <input> is an image file, or a .glimmer folder for --export (a document from a Mac).
        <format> is png, jpeg, heic, tiff or webp. Without --format, the output's extension decides;
        without one Glimmer knows, Remove Background and Export write PNG and the others JPEG.
        <preset> is a preset's name or id. <action> is a saved Assistant action's name or id.
        <filter> is one of: gaussian_blur, lens_blur, motion_blur, sharpen, unsharp_mask, clarity, add_noise,
        pixelate, find_edges, emboss, oil_paint, halftone, vignette, chromatic_aberration.

        Exit status: 0 done, 1 the image could not be made, 2 the command was not understood.
        """;

    /// <summary>Whether the app was started to run a command rather than to show a window.</summary>
    public static bool IsCommand(IReadOnlyList<string> args) => CommandLineWords.IsCommand(args);

    /// <summary>Runs the command in <paramref name="args"/>; returns the exit status. <paramref name="settings"/> holds the
    /// saved actions (the app's settings store).</summary>
    public static int Run(IReadOnlyList<string> args, IAppSettings settings, TextWriter output, TextWriter error)
    {
        if (args.Count == 0 || args[0] is "--help" or "-h" or "/?")
        {
            output.WriteLine(Help);
            return args.Count == 0 ? Usage : Success;
        }
        var command = args[0];
        var rest = args.Skip(1).ToList();
        ExportFormat? format = null;
        int f = rest.IndexOf("--format");
        if (f >= 0)
        {
            if (f + 1 >= rest.Count || ExportFormatExtensions.FormatFromRawValue(Normalized(rest[f + 1])) is not { } given)
                return Refuse(error, "--format takes png, jpeg, heic, tiff or webp.");
            format = given;
            rest.RemoveRange(f, 2);
        }
        bool takesName = command is "--apply-preset" or "--apply-filter" or "--run-action";
        if (!CommandLineWords.Commands.Contains(command)) return Refuse(error, $"Glimmer doesn't know the command {command}.");
        if (rest.Count != (takesName ? 3 : 2))
            return Refuse(error, takesName ? $"{command} takes a name, an input and an output." : $"{command} takes an input and an output.");
        var named = takesName ? rest[0] : null;
        var input = Path.GetFullPath(rest[^2]);
        var outputPath = Path.GetFullPath(rest[^1]);
        try
        {
            var defaultFormat = command is "--remove-background" or "--export" ? ExportFormat.Png : ExportFormat.Jpeg;
            var chosen = format ?? FormatFor(outputPath) ?? defaultFormat;
            var state = HeadlessEditor.Document(input);
            state = command switch
            {
                "--auto-enhance" => HeadlessEditor.AutoEnhance(state),
                "--remove-background" => HeadlessEditor.RemoveBackground(state),
                "--apply-preset" => HeadlessEditor.ApplyPreset(FindPreset(named!), state),
                "--apply-filter" => HeadlessEditor.ApplyFilter(FilterFor(named!), state),
                "--run-action" => HeadlessEditor.Run(FindAction(named!, settings), state),
                _ => state,
            };
            var data = HeadlessEditor.Encoded(state, new ExportOptions(chosen));
            var folder = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            BatchRunner.WriteAtomically(outputPath, data);
            output.WriteLine(outputPath);
            return Success;
        }
        catch (Exception problem)
        {
            error.WriteLine(problem is FileNotFoundException or DirectoryNotFoundException ? $"There is no file {input}." : problem.Message);
            return Failed;
        }
    }

    private static int Refuse(TextWriter error, string message)
    {
        error.WriteLine(message);
        error.WriteLine("Glimmer.exe --help lists the commands.");
        return Usage;
    }

    private static string Normalized(string format) => format.ToLowerInvariant() is "jpg" ? "jpeg" : format.ToLowerInvariant() is "tif" ? "tiff" : format.ToLowerInvariant();

    /// <summary>The format an output path's extension names, if Glimmer writes it.</summary>
    internal static ExportFormat? FormatFor(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        return extension.Length == 0 ? null : ExportFormatExtensions.FormatFromRawValue(Normalized(extension));
    }

    private static Preset FindPreset(string key) =>
        Preset.All.FirstOrDefault(p => p.Id == key || string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new HeadlessEditor.Failure($"There is no preset named “{key}”.");

    private static FilterSettings FilterFor(string key)
    {
        if (!Filters.Contains(key) || FilterKind.FromRawValue(key) is not { } kind)
            throw new HeadlessEditor.Failure($"There is no filter named “{key}”. These are: {string.Join(", ", Filters)}.");
        return new FilterSettings(kind);
    }

    private static SavedAction FindAction(string key, IAppSettings settings)
    {
        var actions = Glimmer.AI.SavedActions.Load(new SettingsSavedActionStore(settings));
        return actions.FirstOrDefault(a => string.Equals(a.Id.ToString(), key, StringComparison.OrdinalIgnoreCase))
            ?? actions.FirstOrDefault(a => string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new HeadlessEditor.Failure($"There is no saved action named “{key}”.");
    }
}
