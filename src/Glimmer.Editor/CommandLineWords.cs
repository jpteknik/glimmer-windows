// New file, no Swift original. The words that make Glimmer's arguments a command-line action (--auto-enhance, ..., --help)
// rather than files to open. Glimmer.Editor's GlimmerCommandLine and the console companion (src/Glimmer.Console, which
// compiles this very file) both read this list, so the two can never disagree about what a command is. It must depend on
// nothing but the BCL, because the companion has no reference to any other Glimmer project.
namespace Glimmer.Editor;

public static class CommandLineWords
{
    /// <summary>The actions that take an input and an output image.</summary>
    public static readonly string[] Commands = ["--auto-enhance", "--remove-background", "--apply-preset", "--apply-filter", "--run-action", "--export"];

    /// <summary>Whether <paramref name="word"/> asks for the help text.</summary>
    public static bool IsHelp(string word) => word is "--help" or "-h" or "/?";

    /// <summary>Whether the app was started to run a command rather than to show a window.</summary>
    public static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && (Array.IndexOf(Commands, args[0]) >= 0 || IsHelp(args[0]));
}
