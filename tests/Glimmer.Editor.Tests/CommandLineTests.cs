// Tests for the command line that stands in for App/Intents.swift on Windows (GlimmerCommandLine), the headless editor
// behind it (HeadlessEditor) and Batch Process (BatchRunner, from App/BatchSheet.swift), which run the same code.
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;
using SkiaSharp;

namespace Glimmer.Editor.Tests;

[Collection(SharedModelStoreCollection.Name)]
public class CommandLineTests
{
    private static string Png(Harness h, string name, int width = 60, int height = 40)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(90, 120, 200));
        using (var canvas = new SKCanvas(bitmap)) canvas.DrawRect(10, 10, 20, 15, new SKPaint { Color = SKColors.Orange });
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var path = h.PathIn(name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static (int Code, string Output, string Error) Run(Harness h, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = GlimmerCommandLine.Run(args, h.Settings, output, error);
        return (code, output.ToString().Trim(), error.ToString().Trim());
    }

    [Fact]
    public void TheCommandsAreRecognisedAndHelpIsShown()
    {
        Assert.True(GlimmerCommandLine.IsCommand(["--auto-enhance", "a", "b"]));
        Assert.True(GlimmerCommandLine.IsCommand(["--help"]));
        Assert.False(GlimmerCommandLine.IsCommand([]));
        Assert.False(GlimmerCommandLine.IsCommand(["C:\\photo.jpg"]));
        using var h = new Harness();
        var (code, output, _) = Run(h, "--help");
        Assert.Equal(GlimmerCommandLine.Success, code);
        Assert.Contains("--apply-filter <filter> <input> <output>", output);
    }

    [Fact]
    public void AWrongCommandLineIsAUsageError()
    {
        using var h = new Harness();
        Assert.Equal(GlimmerCommandLine.Usage, Run(h, "--auto-enhance", "only-one").Code);
        Assert.Equal(GlimmerCommandLine.Usage, Run(h, "--export", "a.png", "b.png", "--format", "bmp").Code);
        Assert.Equal(GlimmerCommandLine.Usage, Run(h, "--apply-preset", "a.png", "b.png").Code);
    }

    [Fact]
    public void ExportWritesTheFormatTheOutputNames()
    {
        using var h = new Harness();
        var input = Png(h, "in.png");
        var (code, output, error) = Run(h, "--export", input, h.PathIn("out/picture.jpg"));
        Assert.Equal(GlimmerCommandLine.Success, code);
        Assert.Equal("", error);
        Assert.Equal(h.PathIn("out/picture.jpg"), output);
        var bytes = File.ReadAllBytes(output);
        Assert.Equal([0xFF, 0xD8], bytes.Take(2));
        // --format wins over the extension.
        (code, output, _) = Run(h, "--export", input, h.PathIn("picture.out"), "--format", "png");
        Assert.Equal(GlimmerCommandLine.Success, code);
        Assert.Equal([0x89, (byte)'P'], File.ReadAllBytes(output).Take(2));
    }

    [Fact]
    public void FiltersPresetsAndAutoEnhanceRunHeadless()
    {
        using var h = new Harness();
        var input = Png(h, "in.png");
        Assert.Equal(GlimmerCommandLine.Success, Run(h, "--apply-filter", "vignette", input, h.PathIn("v.png")).Code);
        Assert.True(File.Exists(h.PathIn("v.png")));
        var preset = Preset.All[0];
        Assert.Equal(GlimmerCommandLine.Success, Run(h, "--apply-preset", preset.Name, input, h.PathIn("p.jpg")).Code);
        Assert.Equal(GlimmerCommandLine.Success, Run(h, "--auto-enhance", input, h.PathIn("e.jpg")).Code);
        var (code, _, error) = Run(h, "--apply-filter", "melt", input, h.PathIn("x.png"));
        Assert.Equal(GlimmerCommandLine.Failed, code);
        Assert.StartsWith("There is no filter named “melt”.", error);
        (code, _, error) = Run(h, "--apply-preset", "No Such Look", input, h.PathIn("x.png"));
        Assert.Equal(GlimmerCommandLine.Failed, code);
        Assert.Equal("There is no preset named “No Such Look”.", error);
        (code, _, error) = Run(h, "--export", h.PathIn("missing.png"), h.PathIn("x.png"));
        Assert.Equal(GlimmerCommandLine.Failed, code);
        Assert.StartsWith("There is no file", error);
    }

    [Fact]
    public void ASavedActionRunsByItsName()
    {
        using var h = new Harness();
        var input = Png(h, "in.png");
        Glimmer.AI.SavedActions.Store([new SavedAction("Warm it up", [new AgentStep("add_adjustment", ("type", "vibrance")), new AgentStep("deselect")])],
                                      new SettingsSavedActionStore(h.Settings));
        Assert.Equal(GlimmerCommandLine.Success, Run(h, "--run-action", "warm it up", input, h.PathIn("a.png")).Code);
        Glimmer.AI.SavedActions.Store([new SavedAction("Paid", [new AgentStep("generate_layer", ("prompt", "a cat"))])], new SettingsSavedActionStore(h.Settings));
        var (code, _, error) = Run(h, "--run-action", "Paid", input, h.PathIn("b.png"));
        Assert.Equal(GlimmerCommandLine.Failed, code);
        Assert.Contains("needs the Glimmer window", error);
    }

    [Fact]
    public void RemoveBackgroundWithoutItsModelSaysWhatIsMissing()
    {
        using var h = new Harness();
        using var models = new SharedModelStore(h);
        var input = Png(h, "in.png");
        var (code, _, error) = Run(h, "--remove-background", input, h.PathIn("cut.png"));
        Assert.Equal(GlimmerCommandLine.Failed, code);
        Assert.Equal(new ModelMissingError([OnDeviceModel.Subject]).Message, error);
        Assert.False(File.Exists(h.PathIn("cut.png")));
    }

    [Fact]
    public void BatchProcessWritesNumberedFilesAndListsFailures()
    {
        using var h = new Harness();
        var a = Png(h, "a.png");
        var b = h.PathIn("broken.png");
        File.WriteAllText(b, "not a picture");
        var destination = h.PathIn("batch");
        Directory.CreateDirectory(destination);
        File.WriteAllBytes(Path.Combine(destination, "a-web.jpg"), [1]);
        var runner = new BatchRunner(h.Environment);
        runner.Start([a, b], new BatchRecipe.ConvertOnly(), new ExportOptions(ExportFormat.Jpeg), destination, "-web");
        Assert.True(runner.IsRunning);
        h.Scheduler.RunPosted();
        Assert.False(runner.IsRunning);
        Assert.Equal(2, runner.Done);
        Assert.Equal(Path.Combine(destination, "a-web 2.jpg"), runner.Outcomes[0].Output);
        Assert.True(runner.Succeeded(a));
        Assert.False(runner.Succeeded(b));
        Assert.Equal(b, Assert.Single(runner.Failures).Source);
        // Rating: every readable photo gets one.
        runner.Rate([a, b]);
        h.Scheduler.RunPosted();
        Assert.False(runner.IsRating);
        Assert.Equal(2, runner.Rated);
        Assert.NotNull(runner.Rating(a));
        Assert.Null(runner.Rating(b));
    }
}
