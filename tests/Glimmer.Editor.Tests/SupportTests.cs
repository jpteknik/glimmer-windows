// Tests for the ports of App/PersonMessage.swift, App/SaveFailures.swift, App/TabletSettings.swift, the command
// palette's items (App/CommandPalette.swift), ImageLabel's naming rule (GlimmerAI/SmartTools.swift) and the UI-thread
// discipline that replaces Swift's @MainActor (UiScheduler.cs).
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Tools;

namespace Glimmer.Editor.Tests;

public class SupportTests
{
    [Fact]
    public void MessagesAreTranslatedWithTheirValues()
    {
        var table = new Dictionary<string, string?>
        {
            ["The image could not be read."] = "Bilden kunde inte läsas.",
            ["This RAW file is {0}, more than Glimmer keeps in a document."] = "Den här RAW-filen är {0}, mer än Glimmer behåller i ett dokument.",
            ["{0} of {1}"] = "{1}: {0}",   // too little of its own to be matched: shown as it is
            ["Moved {0} to {1} and {2}."] = "Flyttade {0} till {2} och {1}.",
        };
        Assert.Equal("Bilden kunde inte läsas.", PersonMessage.Translate("The image could not be read.", table));
        Assert.Equal("Den här RAW-filen är 1.2 GB, mer än Glimmer behåller i ett dokument.",
                     PersonMessage.Translate("This RAW file is 1.2 GB, more than Glimmer keeps in a document.", table));
        Assert.Equal("3 of 4", PersonMessage.Translate("3 of 4", table));
        // Placeholders may be reordered in the translation.
        Assert.Equal("Flyttade a till c och b.", PersonMessage.Translate("Moved a to b and c.", table));
        Assert.Equal("Something else entirely.", PersonMessage.Translate("Something else entirely.", table));
    }

    [Fact]
    public void AnErrorBecomesATextTheAppCanLookUp()
    {
        Assert.Equal("Disk full", PersonMessage.Text(new IOException("Disk full")).Template);
        // Braces of its own: shown as it is, never formatted.
        var odd = PersonMessage.Text(new InvalidOperationException("Expected {name}"));
        Assert.True(odd.IsVerbatim);
        Assert.Equal("Expected {name}", odd.ToString());
    }

    [Fact]
    public void ASaveThatFailsIsAlwaysExplainedTheRecoveryCopyOnlyOnce()
    {
        var failures = new SaveFailures();
        var first = failures.Failed("No space", null, "a.glimmer");
        Assert.Equal("The document couldn't be saved\n\nNo space\n\nThe document is still open and nothing was lost. Try saving it somewhere else.",
                     first!.ToString());
        Assert.NotNull(failures.Failed("No space", null, "a.glimmer"));
        Assert.Null(failures.Failed("No space", null, "a.glimmer", always: false));
        failures.Saved("a.glimmer");
        Assert.NotNull(failures.Failed("No space", null, "a.glimmer", always: false));
        Assert.EndsWith("Free 2 GB.", failures.Failed("No space", "Free 2 GB.", "b.glimmer")!.ToString());
    }

    [Fact]
    public void ThePressureCurve()
    {
        var settings = new FakeSettings();
        Assert.Equal(1, TabletSettings.PressureExponent(settings));
        Assert.True(TabletSettings.UseTilt(settings));
        settings.Set(TabletSettings.PressureCurveKey, 2.0);
        Assert.Equal(0.25, TabletSettings.Curve(0.5, settings), 9);
        Assert.Equal(1, TabletSettings.Curve(1.5, settings));
        Assert.Equal(0, TabletSettings.Curve(-1, settings));
    }

    [Fact]
    public void ThePaletteFindsCommandsToolsAndPresets()
    {
        var items = Palette.AllItems(english => english == "Export…" ? "Exportera…" : english);
        Assert.Contains(items, i => i.Id == "file.save" && i.Subtitle == "File");
        Assert.Contains(items, i => i.Id == "tool.brush" && i.Subtitle == "Tool (B)");
        Assert.Contains(items, i => i.Id.StartsWith("preset.") && i.Subtitle == "Preset");
        Assert.Contains(items, i => i.Id == CommandID.Export.RawValue && i.Keywords.Contains("Exportera…"));
        Assert.DoesNotContain(items, i => i.Id == CommandID.CommandPalette.RawValue);
        Assert.Contains(items, i => i.Id == CommandID.NewDocument.RawValue);
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct().Count());
        Assert.Equal(Enum.GetValues<ToolID>().Length, ToolTitles.Available.Count);
    }

    [Fact]
    public void PaletteItemsRun()
    {
        using var h = new Harness();
        var s = h.NewSession();
        var items = Palette.Items(s);
        Assert.True(s.RunPaletteItem(items.First(i => i.Id == "tool.gradient")));
        Assert.Equal(ToolID.Gradient, s.Tool);
        Assert.True(s.RunPaletteItem(items.First(i => i.Id == CommandID.NewLayer.RawValue)));
        Assert.Equal(2, s.State.AllLayers.Length);
        // Not available now: says so instead of doing nothing silently.
        Assert.False(s.RunPaletteItem(items.First(i => i.Id == CommandID.Deselect.RawValue)));
    }

    [Fact]
    public void GenericNamesAreWorthReplacing()
    {
        Assert.True(ImageLabel.IsGeneric("IMG_1234"));
        Assert.True(ImageLabel.IsGeneric("Skärmavbild 2026-11-19"));
        Assert.True(ImageLabel.IsGeneric("20261119_120000"));
        Assert.True(ImageLabel.IsGeneric("  "));
        Assert.False(ImageLabel.IsGeneric("Grandma at the lake"));
        Assert.False(ImageLabel.IsGeneric("Logo 2"));
    }

    [Fact]
    public void FileSizesAreWrittenAsTheMacWritesThem()
    {
        Assert.Equal("Zero KB", EditorHelpers.FileSize(0));
        Assert.Equal("999 bytes", EditorHelpers.FileSize(999));
        Assert.Equal("2 KB", EditorHelpers.FileSize(1_500));
        Assert.Equal("1.5 MB", EditorHelpers.FileSize(1_500_000));
        Assert.Equal("2.25 GB", EditorHelpers.FileSize(2_250_000_000));
    }

    [Fact]
    public void BackgroundWorkComesBackOnTheUIThread()
    {
        var context = new QueueContext();
        var scheduler = new SynchronizationContextScheduler(context);
        int? result = null;
        int uiThread = Environment.CurrentManagedThreadId;
        int? workThread = null;
        scheduler.Background(() =>
        {
            workThread = Environment.CurrentManagedThreadId;
            return 42;
        }, value => result = value);
        // Nothing arrives until the UI thread runs its queue.
        Assert.True(context.WaitForPost(TimeSpan.FromSeconds(5)));
        Assert.Null(result);
        context.RunAll();
        Assert.Equal(42, result);
        Assert.NotEqual(uiThread, workThread);

        // A cancelled result is dropped; a failure goes to its handler.
        bool cancelled = true;
        Exception? failure = null;
        scheduler.Background<int>(() => throw new InvalidOperationException("no"), _ => result = -1, () => false, e => failure = e);
        scheduler.Background(() => 7, value => result = value, () => cancelled);
        Assert.True(context.WaitForPost(TimeSpan.FromSeconds(5), count: 2));
        context.RunAll();
        Assert.Equal(42, result);
        Assert.Equal("no", failure?.Message);

        // A scheduled action that is disposed never runs.
        bool ran = false;
        scheduler.Schedule(TimeSpan.FromMilliseconds(10), () => ran = true).Dispose();
        Thread.Sleep(100);
        context.RunAll();
        Assert.False(ran);
        // Made away from a UI thread (no context to capture): says so.
        Exception? thrown = null;
        var thread = new Thread(() =>
        {
            try { _ = new SynchronizationContextScheduler(); }
            catch (Exception e) { thrown = e; }
        });
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(thrown);
    }

    /// <summary>A UI thread's queue that the test empties by hand.</summary>
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback, object?)> queue = new();
        private readonly SemaphoreSlim posted = new(0);

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (queue) queue.Enqueue((d, state));
            posted.Release();
        }

        public bool WaitForPost(TimeSpan timeout, int count = 1)
        {
            for (int i = 0; i < count; i++) if (!posted.Wait(timeout)) return false;
            return true;
        }

        public void RunAll()
        {
            while (true)
            {
                (SendOrPostCallback, object?) next;
                lock (queue)
                {
                    if (queue.Count == 0) return;
                    next = queue.Dequeue();
                }
                next.Item1(next.Item2);
            }
        }
    }
}
