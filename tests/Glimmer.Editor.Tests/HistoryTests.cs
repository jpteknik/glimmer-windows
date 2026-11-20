// Tests for the port of App/GlimmerDocument.swift's undo history: one step per user action (AppKit groups what an event
// registers), slider drags as one step, transactions (an assistant plan undoes as a whole), the step limit, the memory
// budget with older steps spilled to disk, jumping in the History panel, and the change count behind "unsaved changes".
// The original has no tests at this level (UndoStepTests.swift covers Core's undo manager).
using Glimmer.Core;
using Glimmer.Tools;

namespace Glimmer.Editor.Tests;

public class HistoryTests
{
    private static string[] Steps(EditorSession s) => [.. s.Document.History.Select(t => t.ToString())];

    /// <summary>One brush dab at (x, y) as its own event.</summary>
    private static void Dab(EditorSession s, ManualScheduler scheduler, double x, double y)
    {
        s.Tool = ToolID.Brush;
        new CanvasInput(s, scheduler).Click(new Vec2(x, y));
    }

    [Fact]
    public void WhatOneEventRegistersIsOneStep()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        // Two changes while handling one event (as penMouseDown makes "New Path" and its first anchor)…
        s.Perform(CommandID.NewLayer);
        s.Perform(CommandID.NewLayer);
        Assert.Equal(3, s.State.AllLayers.Length);
        h.Scheduler.RunPosted();
        // …undo together.
        s.Document.Undo();
        Assert.Single(s.State.AllLayers);
        // In separate events they are separate steps.
        s.Document.Redo();
        s.Perform(CommandID.NewLayer);
        h.Scheduler.RunPosted();
        s.Perform(CommandID.NewLayer);
        h.Scheduler.RunPosted();
        s.Document.Undo();
        Assert.Equal(4, s.State.AllLayers.Length);
    }

    [Fact]
    public void SliderDragsAreOneStepOnceTheyGoQuiet()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        for (int i = 9; i >= 2; i--) s.SetOpacity(i / 10.0);
        Assert.Equal(0.2, s.State.Layers[0].Opacity, 6);
        Assert.Empty(s.Document.History);
        Assert.True(s.CanPerform(CommandID.Undo));
        h.Scheduler.Advance(TimeSpan.FromMilliseconds(700));
        Assert.Equal(["Change Opacity"], Steps(s));
        s.Document.Undo();
        Assert.Equal(1, s.State.Layers[0].Opacity);
    }

    [Fact]
    public void UndoCommitsAPendingSliderDragFirst()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.Perform(CommandID.NewLayer);
        h.Scheduler.RunPosted();
        s.SetOpacity(0.5);
        // Undo before the drag went quiet: the drag is a step of its own, and it is what Undo takes back.
        s.Perform(CommandID.Undo);
        Assert.Equal(1, s.State.ActiveLayer!.Opacity);
        Assert.Equal(2, s.State.AllLayers.Length);
        Assert.Equal(["New Layer", "Change Opacity"], Steps(s));
        Assert.Equal(1, s.Document.HistoryIndex);
    }

    [Fact]
    public void RevertingALiveEditLeavesNoStep()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        var before = s.State;
        s.SetOpacity(0.3);
        s.RevertLiveEdit(before);
        h.Scheduler.Settle();
        Assert.Same(before, s.State);
        Assert.Empty(s.Document.History);
    }

    [Fact]
    public void ATransactionIsOneStep()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.Document.BeginTransaction("Assistant Plan");
        Assert.True(s.Document.IsInTransaction);
        s.Perform(CommandID.NewLayer);
        s.Perform(CommandID.SelectAll);
        s.Perform(CommandID.FillForeground);
        s.Document.EndTransaction();
        h.Scheduler.RunPosted();
        Assert.Equal(["Assistant Plan"], Steps(s));
        s.Document.Undo();
        Assert.Single(s.State.AllLayers);
        Assert.Null(s.State.Selection);
        s.Document.Redo();
        Assert.Equal(2, s.State.AllLayers.Length);
        // An empty transaction leaves no step.
        s.Document.BeginTransaction("Nothing");
        s.Document.EndTransaction();
        Assert.Single(s.Document.History);
    }

    [Fact]
    public void ChangesOutsideAnEventAreStepsOfTheirOwn()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        // An agent's three requests, with no event between them.
        s.Document.Edit("A", st => st with { Selection = Selection.All() }, separateStep: true);
        s.Document.Edit("B", st => st with { Selection = null }, separateStep: true);
        s.Document.Edit("C", st => st with { Selection = Selection.All() }, separateStep: true);
        s.Document.Undo();
        Assert.Null(s.State.Selection);
        Assert.Equal(2, s.Document.HistoryIndex);
    }

    [Fact]
    public void TheStepLimitDropsTheOldest()
    {
        using var h = new Harness(settings => settings.Set(Preferences.HistoryLimitKey, 3));
        var s = h.NewSession(Docs.TwoTone());
        Assert.Equal(3, s.Document.HistoryLimit);
        for (int i = 0; i < 5; i++)
        {
            s.Perform(CommandID.NewLayer);
            h.Scheduler.RunPosted();
        }
        Assert.Equal(3, s.Document.History.Count);
        int undos = 0;
        while (s.Document.CanUndo)
        {
            s.Document.Undo();
            undos++;
        }
        Assert.Equal(3, undos);
        Assert.Equal(3, s.State.AllLayers.Length);
    }

    [Fact]
    public void DefaultLimitAndBudget()
    {
        using var h = new Harness();
        var s = h.NewSession();
        Assert.Equal(100, s.Document.HistoryLimit);
        Assert.Equal(2048L * 1_048_576, s.Document.HistoryBudgetBytes);
    }

    [Fact]
    public void OlderStepsMoveToDiskAndStillUndo()
    {
        using var h = new Harness(settings => settings.Set(Preferences.HistoryBudgetKey, 1L));
        var s = h.NewSession(Docs.TwoTone());
        var original = s.State;
        // A step costs the textured tiles only its snapshot holds: dabs in one tile replace a textured tile (half a
        // megabyte) each, over a 1 MB budget after a few. (The first dab replaces a solid tile, which costs nothing.)
        for (int i = 0; i < 6; i++) Dab(s, h.Scheduler, 260 + 10 * i, 100);
        h.Scheduler.RunPosted();
        Assert.True(s.Document.StepsOnDisk > 0, "nothing was spilled");
        // The newest step always stays in memory.
        Assert.True(s.Document.StepsOnDisk < 6);
        Assert.True(Directory.Exists(s.Document.SpillDirectory));
        Assert.StartsWith(h.Environment.TemporaryFolder, s.Document.SpillDirectory);
        Assert.Equal(6, s.Document.History.Count);
        var painted = s.State;
        while (s.Document.CanUndo) s.Document.Undo();
        Assert.Equal(0, s.Document.HistoryIndex);
        Assert.Equal(Docs.Pixel(original.Layers[0], 260, 100), Docs.Pixel(s.State.Layers[0], 260, 100));
        Assert.Equal(Docs.Pixel(original.Layers[0], 310, 100), Docs.Pixel(s.State.Layers[0], 310, 100));
        while (s.Document.CanRedo) s.Document.Redo();
        Assert.Equal(Docs.Pixel(painted.Layers[0], 260, 100), Docs.Pixel(s.State.Layers[0], 260, 100));
        Assert.Equal(Docs.Pixel(painted.Layers[0], 310, 100), Docs.Pixel(s.State.Layers[0], 310, 100));
        // Closing the document lets go of its steps on disk.
        Assert.True(s.TryClose());
        Assert.False(Directory.Exists(s.Document.SpillDirectory));
    }

    [Fact]
    public void JumpingInTheHistoryUndoesAndRedoesTheSteps()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        for (int i = 0; i < 4; i++)
        {
            s.Perform(CommandID.NewLayer);
            h.Scheduler.RunPosted();
        }
        s.Document.JumpToHistory(1);
        Assert.Equal(1, s.Document.HistoryIndex);
        Assert.Equal(2, s.State.AllLayers.Length);
        Assert.Equal(4, s.Document.History.Count);
        s.Document.JumpToHistory(4);
        Assert.Equal(5, s.State.AllLayers.Length);
        s.Document.JumpToHistory(0);
        Assert.Single(s.State.AllLayers);
        Assert.False(s.Document.HasUnsavedChanges);
        // A new step after jumping back replaces the steps that could be redone.
        s.Perform(CommandID.NewGroup);
        Assert.Equal(["New Group"], Steps(s));
        Assert.False(s.Document.CanRedo);
    }

    [Fact]
    public void UnsavedChangesFollowTheChangeCount()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        var changed = new List<string>();
        s.Document.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        s.Perform(CommandID.NewLayer);
        h.Scheduler.RunPosted();
        Assert.True(s.Document.HasUnsavedChanges);
        Assert.Contains(nameof(GlimmerDocument.HasUnsavedChanges), changed);
        var path = h.PathIn("a.glimmer");
        s.SaveTo(path);
        h.Scheduler.RunPosted();
        Assert.False(s.Document.HasUnsavedChanges);
        s.Document.Undo();
        Assert.True(s.Document.HasUnsavedChanges);
        s.Document.Redo();
        Assert.False(s.Document.HasUnsavedChanges);
    }

    [Fact]
    public void ThePanelIsToldAboutEveryStep()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        var changed = new List<string>();
        s.Document.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        var sessionChanged = new List<string>();
        s.PropertyChanged += (_, e) => sessionChanged.Add(e.PropertyName!);
        s.Perform(CommandID.NewLayer);
        Assert.Contains(nameof(GlimmerDocument.History), changed);
        Assert.Contains(nameof(GlimmerDocument.HistoryIndex), changed);
        Assert.Contains(nameof(GlimmerDocument.CanUndo), changed);
        Assert.Contains(nameof(EditorSession.State), sessionChanged);
        Assert.Contains(nameof(EditorSession.ActiveLayer), sessionChanged);
    }

    [Fact]
    public void LeftoverUndoFoldersAreRemovedAtLaunch()
    {
        using var h = new Harness();
        var orphan = Path.Combine(h.Environment.TemporaryFolder, UndoSpillStore.FolderName(pid: 999_999_999, start: 1));
        Directory.CreateDirectory(orphan);
        var mine = Path.Combine(h.Environment.TemporaryFolder, UndoSpillStore.FolderName());
        Directory.CreateDirectory(mine);
        var notOurs = Path.Combine(h.Environment.TemporaryFolder, "something-else");
        Directory.CreateDirectory(notOurs);
        new DocumentOpener(h.Environment).Launch();
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(mine));
        Assert.True(Directory.Exists(notOurs));
        Assert.Contains("removed 1 leftover undo folder(s)", h.Log);
    }
}
