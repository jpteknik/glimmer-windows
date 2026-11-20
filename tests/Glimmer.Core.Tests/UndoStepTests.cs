// Ported from Packages/GlimmerKit/Tests/GlimmerCoreTests/UndoStepTests.swift (Foundation's UndoManager is
// Glimmer.Core.UndoManager here).

namespace Glimmer.Core.Tests;

/// <summary>Three agent changes in a row are three undo steps (Jämmer found the merge first).</summary>
public class UndoStepTests
{
    private sealed class Doc
    {
        public List<string> Layers = [];

        public void Add(string name, UndoManager undo, bool separate)
        {
            void Register() => Set([.. Layers, name], undo);
            if (separate) undo.SeparateStep(Register);
            else Register();
        }

        /// <summary>Like GlimmerDocument: a step restores the earlier state and registers the opposite.</summary>
        public void Set(List<string> next, UndoManager undo)
        {
            var old = Layers;
            Layers = next;
            undo.RegisterUndo(this, doc => doc.Set(old, undo));
        }
    }

    [Fact]
    public void AutomaticGroupingMergesChangesWithoutEvents()
    {
        // No event ends between the changes, like back-to-back API requests.
        var undo = new UndoManager();
        var doc = new Doc();
        foreach (var name in new[] { "a", "b", "c" }) doc.Add(name, undo, separate: false);
        undo.Undo();
        Assert.True(doc.Layers.Count == 0, "the bug: one undo took all three back");
    }

    [Fact]
    public void SeparateStepsUndoOneAtATime()
    {
        var undo = new UndoManager();
        var doc = new Doc();
        foreach (var name in new[] { "a", "b", "c" }) doc.Add(name, undo, separate: true);
        undo.Undo();
        undo.Undo();
        Assert.Equal(["a"], doc.Layers);
        undo.Redo();
        Assert.Equal(["a", "b"], doc.Layers);
    }

    [Fact]
    public void StepsStaySeparateAcrossUndoAndRedo()
    {
        var undo = new UndoManager();
        var doc = new Doc();
        doc.Add("a", undo, separate: true);
        doc.Add("b", undo, separate: true);
        undo.Undo();
        doc.Add("c", undo, separate: true);   // a new step after an undo drops the redo
        Assert.True(doc.Layers.SequenceEqual(["a", "c"]) && !undo.CanRedo);
        undo.Undo();
        undo.Undo();
        Assert.Empty(doc.Layers);
        undo.Redo();
        Assert.Equal(["a"], doc.Layers);
        Assert.True(undo.GroupsByEvent, "automatic grouping is back on for the user's own edits");
    }

    /// <summary>
    /// What AppKit's run loop does after each event, done by the app here: the changes of one event are one step, the
    /// next event's another. Not in the Mac's tests.
    /// </summary>
    [Fact]
    public void EachUserEventIsOneStepAndKeepsItsName()
    {
        var undo = new UndoManager();
        var doc = new Doc();
        doc.Add("a", undo, separate: false);
        doc.Add("b", undo, separate: false);
        undo.SetActionName("Paint");
        undo.CloseEventGroup();
        doc.Add("c", undo, separate: false);
        undo.SetActionName("Erase");
        undo.CloseEventGroup();
        Assert.Equal("Erase", undo.UndoActionName);
        undo.Undo();
        Assert.Equal(["a", "b"], doc.Layers);
        Assert.Equal("Paint", undo.UndoActionName);
        Assert.Equal("Erase", undo.RedoActionName);
        undo.Undo();
        Assert.Empty(doc.Layers);
        Assert.False(undo.CanUndo);
    }
}
