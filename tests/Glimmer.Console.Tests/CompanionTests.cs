// New file, no Swift original. Tests of the console companion on Linux, with shell scripts standing in for Glimmer.exe.
// What cannot be shown here: that Windows starts Glimmer.com before Glimmer.exe (PATHEXT), and the real exe's output.
extern alias editor;
using Glimmer.Console;
using GlimmerCommandLine = editor::Glimmer.Editor.GlimmerCommandLine;
using EditorWords = editor::Glimmer.Editor.CommandLineWords;
using ConsoleWords = Glimmer.Editor.CommandLineWords; // the copy compiled into Glimmer.Console

namespace Glimmer.Console.Tests;

public sealed class CompanionTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("glimmer-console-").FullName;

    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    private string Script(string name, string body)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private string Touch(string name) { var path = Path.Combine(_folder, name); File.WriteAllText(path, ""); return path; }

    [Fact]
    public void RelayPassesBothStreamsThroughWithUtf8AndReturnsTheExitCode()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Companion.Relay("/bin/sh", ["-c", "printf 'å ä ö\\n'; printf 'oops\\n' >&2; exit 3"], output, error, CancellationToken.None);
        Assert.Equal(3, code);
        Assert.Equal("å ä ö\n", output.ToString());
        Assert.Equal("oops\n", error.ToString());
    }

    [Fact]
    public void RelayPassesTheArgumentsOnUnchangedAndSetsTheEncodingVariable()
    {
        var output = new StringWriter();
        var args = new[] { "-c", "printf '%s|' \"$@\"; printf '%s' \"$GLIMMER_OUTPUT_ENCODING\"", "sh", "a b", "", "ö \"quoted\"" };
        var code = Companion.Relay("/bin/sh", args, output, new StringWriter(), CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Equal("a b||ö \"quoted\"|utf-8", output.ToString());
    }

    [Fact]
    public void RelayReportsAChildThatCannotBeStarted()
    {
        var error = new StringWriter();
        Assert.Equal(Companion.Failed, Companion.Relay(Path.Combine(_folder, "nothing"), [], new StringWriter(), error, CancellationToken.None));
        Assert.Contains("could not be started", error.ToString());
    }

    [Fact]
    public async Task CancellingKillsTheChildAndItsChildren()
    {
        var marker = Path.Combine(_folder, "survivor");
        using var cancel = new CancellationTokenSource();
        var task = Task.Run(() => Companion.Relay("/bin/sh", ["-c", $"(sleep 3; touch '{marker}') & sleep 30"], new StringWriter(), new StringWriter(), cancel.Token));
        await Task.Delay(500);
        await cancel.CancelAsync();
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Equal(Companion.Interrupted, await task);
        await Task.Delay(4000);
        Assert.False(File.Exists(marker), "the grandchild was killed too");
    }

    [Fact]
    public void TheGuiExeIsFoundByTheCompanionsOwnNameFirst()
    {
        var own = Touch("Glimmer-0.1.0-win-x64.com");
        var versioned = Touch("Glimmer-0.1.0-win-x64.exe");
        Touch("Glimmer.exe");
        Assert.Equal(versioned, Companion.FindGui(own));
    }

    [Fact]
    public void ThenGlimmerExeInTheSameFolder()
    {
        var own = Touch("Glimmer-0.1.0-win-x64.com");
        var plain = Touch("Glimmer.exe");
        Assert.Equal(plain, Companion.FindGui(own));
    }

    [Fact]
    public void ACompanionNeverFindsItself()
    {
        var own = Touch("Glimmer.Console.exe"); // what a plain build of the project is called
        Assert.Null(Companion.FindGui(own));
        var plain = Touch("Glimmer.exe");
        Assert.Equal(plain, Companion.FindGui(own));
    }

    [Fact]
    public void NoGuiExeIsAMessageOnStderrAndExitCode1()
    {
        var own = Touch("Glimmer-0.1.0-win-x64.com");
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(1, Companion.Execute(["--export", "a", "b"], own, output, error, CancellationToken.None));
        Assert.Equal("", output.ToString());
        Assert.Contains("Glimmer.exe was not found", error.ToString());
    }

    [Fact]
    public void ACommandRunsTheGuiExeAndReturnsItsExitCode()
    {
        var own = Touch("Glimmer-0.1.0-win-x64.com");
        Script("Glimmer-0.1.0-win-x64.exe", "printf '%s\\n' \"$@\"; printf 'å\\n' >&2; exit 2");
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Companion.Execute(["--apply-filter", "sharpen", "in å.jpg", "out.png"], own, output, error, CancellationToken.None);
        Assert.Equal(2, code);
        Assert.Equal("--apply-filter\nsharpen\nin å.jpg\nout.png\n", output.ToString());
        Assert.Equal("å\n", error.ToString());
    }

    [Theory]
    [InlineData]
    [InlineData("photo.jpg")]
    [InlineData("C:\\pictures\\photo.jpg", "second.png")]
    public void ArgumentsThatAreNotACommandStartTheWindowAndReturnAtOnce(params string[] args)
    {
        var own = Touch("Glimmer.com");
        var marker = Path.Combine(_folder, "started");
        Script("Glimmer.exe", $"printf '%s;' \"$@\" > '{marker}.tmp'; mv '{marker}.tmp' '{marker}'; sleep 5");
        var output = new StringWriter();
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(0, Companion.Execute(args, own, output, new StringWriter(), CancellationToken.None));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(4), "did not wait for the window");
        Assert.Equal("", output.ToString());
        for (int i = 0; i < 100 && !File.Exists(marker); i++) Thread.Sleep(50);
        // printf with no arguments still prints its format once.
        Assert.Equal(args.Length == 0 ? ";" : string.Concat(args.Select(a => a + ";")), File.ReadAllText(marker));
    }

    [Theory]
    [InlineData("--auto-enhance")]
    [InlineData("--remove-background")]
    [InlineData("--apply-preset")]
    [InlineData("--apply-filter")]
    [InlineData("--run-action")]
    [InlineData("--export")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    [InlineData("--format")]
    [InlineData("--auto")]
    [InlineData("photo.jpg")]
    [InlineData("--EXPORT")]
    [InlineData("")]
    public void TheCommandWordsAreOneListShownByBothSides(string first)
    {
        string[] args = [first, "x", "y"];
        Assert.Equal(GlimmerCommandLine.IsCommand(args), ConsoleWords.IsCommand(args));
        Assert.Equal(EditorWords.IsCommand(args), ConsoleWords.IsCommand(args));
        Assert.Equal(ConsoleWords.IsCommand(args), first is not "--format" and not "--auto" and not "photo.jpg" and not "--EXPORT" and not "");
    }

    [Fact]
    public void NoArgumentsIsNotACommand()
    {
        Assert.False(ConsoleWords.IsCommand([]));
        Assert.False(GlimmerCommandLine.IsCommand([]));
    }

    [Fact]
    public void TheCommandListIsTheOneGlimmerCommandLineRunsOn()
    {
        // Every word the list names is one Run accepts (here: with too few arguments it is a usage error about the
        // arguments, never "doesn't know the command").
        Assert.Equal(EditorWords.Commands, ConsoleWords.Commands);
        foreach (var word in ConsoleWords.Commands)
        {
            var error = new StringWriter();
            GlimmerCommandLine.Run([word], null!, new StringWriter(), error);
            Assert.DoesNotContain("doesn't know the command", error.ToString());
        }
    }
}
