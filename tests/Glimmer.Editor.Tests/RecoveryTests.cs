// Tests for Recovery.cs and the recovery part of EditorSession+Document.cs (Windows only: the Mac relies on NSDocument's
// autosave). A copy of a document with unsaved changes every couple of minutes, offered at the next start when Glimmer
// stopped without saving or closing it, removed after a clean save or close.
using System.Text.Json.Nodes;
using Glimmer.Core;

namespace Glimmer.Editor.Tests;

public class RecoveryTests
{
    private static string CopyOf(Harness h, EditorSession s) => Path.Combine(h.Environment.RecoveryFolder, s.RecoveryID);

    /// <summary>Makes a copy look as if the Glimmer that wrote it is gone (a crash, a power cut).</summary>
    private static void Orphan(string folder)
    {
        var info = Path.Combine(folder, RecoveryStore.InfoName);
        var json = JsonNode.Parse(File.ReadAllText(info))!;
        json["processId"] = 999_999_999;
        File.WriteAllText(info, json.ToJsonString());
    }

    [Fact]
    public void AnUnsavedDocumentIsCopiedEveryCoupleOfMinutes()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        // The copy is taken every two minutes (the first at 2:00), when there is something unsaved.
        h.Scheduler.Advance(TimeSpan.FromSeconds(121));
        Assert.False(Directory.Exists(CopyOf(h, s)), "a document without changes was copied");
        s.Perform(CommandID.NewLayer);
        h.Scheduler.Advance(TimeSpan.FromSeconds(117));
        Assert.False(Directory.Exists(CopyOf(h, s)));
        h.Scheduler.Advance(TimeSpan.FromSeconds(2));
        Assert.True(File.Exists(Path.Combine(CopyOf(h, s), RecoveryStore.DocumentName)));
        Assert.Equal(2, GlimmerPackage.Load(Path.Combine(CopyOf(h, s), RecoveryStore.DocumentName)).AllLayers.Length);
        // Copied again only when there is something new.
        var written = File.GetLastWriteTimeUtc(Path.Combine(CopyOf(h, s), RecoveryStore.InfoName));
        int runs = h.Scheduler.BackgroundRuns;
        h.Scheduler.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(CopyOf(h, s), RecoveryStore.InfoName)));
        s.Perform(CommandID.NewLayer);
        h.Scheduler.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(3, GlimmerPackage.Load(Path.Combine(CopyOf(h, s), RecoveryStore.DocumentName)).AllLayers.Length);
        Assert.True(h.Scheduler.BackgroundRuns > runs);
    }

    [Fact]
    public void SavingOrClosingRemovesTheCopy()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.Perform(CommandID.NewLayer);
        h.Scheduler.Advance(TimeSpan.FromMinutes(2));
        Assert.True(Directory.Exists(CopyOf(h, s)));
        s.SaveTo(h.PathIn("saved.glimmer"));
        h.Scheduler.RunPosted();
        Assert.False(Directory.Exists(CopyOf(h, s)));

        var t = h.NewSession(Docs.TwoTone());
        t.Perform(CommandID.NewLayer);
        h.Scheduler.Advance(TimeSpan.FromMinutes(2));
        Assert.True(Directory.Exists(CopyOf(h, t)));
        Assert.True(t.TryClose());
        Assert.False(Directory.Exists(CopyOf(h, t)));

        // Undo back to the saved state: the copy is no longer needed either.
        var u = h.NewSession(Docs.TwoTone());
        u.Perform(CommandID.NewLayer);
        h.Scheduler.Advance(TimeSpan.FromMinutes(2));
        u.Document.Undo();
        h.Scheduler.Advance(TimeSpan.FromMinutes(2));
        Assert.False(Directory.Exists(CopyOf(h, u)));
    }

    [Fact]
    public void TheNextStartOffersWhatAStopLeftUnsaved()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        var saved = h.PathIn("work.glimmer");
        s.SaveTo(saved);
        h.Scheduler.RunPosted();
        s.Perform(CommandID.NewLayer);
        s.Rename(s.State.ActiveLayerID!.Value, "Not saved yet");
        h.Scheduler.Advance(TimeSpan.FromMinutes(2));
        // This run is still alive: its copies are not offered (another window of the app would see them).
        Assert.Empty(new DocumentOpener(h.Environment).RecoverableDocuments());
        Orphan(CopyOf(h, s));

        // The next start.
        var next = new ManualScheduler();
        var opener = new DocumentOpener(h.MakeEnvironment(next));
        var offered = Assert.Single(opener.Launch());
        Assert.Equal("work", offered.Info.Name);
        Assert.Equal(saved, offered.Info.OriginalPath);
        var recovered = opener.OpenRecovered(offered);
        Assert.Contains(recovered, h.Host.Shown);
        Assert.Equal("work", recovered.DocumentName);
        Assert.True(recovered.Document.HasUnsavedChanges);
        Assert.Equal("Not saved yet", recovered.ActiveLayer!.Name);
        // Save writes the file it came from, and then the copy goes.
        Assert.Equal(saved, recovered.Document.FilePath);
        Assert.True(Directory.Exists(offered.Folder));
        recovered.Perform(WindowsCommands.Save);
        next.RunPosted();
        Assert.False(Directory.Exists(offered.Folder));
        Assert.Equal("Not saved yet", GlimmerPackage.Load(saved).AllLayers[^1].Name);
        Assert.Empty(opener.RecoverableDocuments());
    }

    [Fact]
    public void ARecoveredCopyIsClaimedAndCanBeDeclined()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.Perform(CommandID.NewLayer);
        h.Scheduler.Advance(TimeSpan.FromMinutes(2));
        Orphan(CopyOf(h, s));
        var opener = new DocumentOpener(h.MakeEnvironment(new ManualScheduler()));
        var entry = Assert.Single(opener.RecoverableDocuments());
        var recovered = opener.OpenRecovered(entry);
        // Taken over by this run: not offered again while it is open.
        Assert.Empty(opener.RecoverableDocuments());
        Assert.Equal(entry.Id, recovered.RecoveryID);
        // Closed without saving: the copy goes with it.
        h.Host.SaveAnswer = SaveChangesChoice.DontSave;
        Assert.True(recovered.TryClose());
        Assert.False(Directory.Exists(entry.Folder));

        var t = h.NewSession(Docs.TwoTone());
        t.Perform(CommandID.NewLayer);
        h.Scheduler.Advance(TimeSpan.FromMinutes(2));
        Orphan(CopyOf(h, t));
        var declined = Assert.Single(opener.RecoverableDocuments());
        opener.DiscardRecovered(declined);
        Assert.Empty(opener.RecoverableDocuments());
    }

    [Fact]
    public void AnUntitledDocumentRecoversUntitled()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.Perform(CommandID.NewLayer);
        h.Scheduler.Advance(TimeSpan.FromMinutes(2));
        Orphan(CopyOf(h, s));
        var opener = new DocumentOpener(h.MakeEnvironment(new ManualScheduler()));
        var recovered = opener.OpenRecovered(Assert.Single(opener.RecoverableDocuments()));
        Assert.Null(recovered.Document.FilePath);
        Assert.Equal("Untitled", recovered.DocumentName);
        // Save asks where.
        recovered.Perform(WindowsCommands.Save);
        Assert.Equal("Untitled.glimmer", h.Dialogs.SaveQuestions[^1].Suggested);
    }

    [Fact]
    public void HalfWrittenOrForeignFoldersAreLeftAlone()
    {
        using var h = new Harness();
        var folder = h.Environment.RecoveryFolder;
        Directory.CreateDirectory(Path.Combine(folder, "no-info"));
        Directory.CreateDirectory(Path.Combine(folder, "bad-info"));
        File.WriteAllText(Path.Combine(folder, "bad-info", RecoveryStore.InfoName), "{ not json");
        Assert.Empty(RecoveryStore.Entries(folder));
        Assert.True(Directory.Exists(Path.Combine(folder, "bad-info")));
    }
}
