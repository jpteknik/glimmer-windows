// Ported from UITests/GlimmerUITests.swift: the critical flows end to end (new document, paint, undo, save, export),
// driven through the session the views call instead of through the UI. The UI tests start from an 800 × 600 white
// document (-uiTestingNewDocument) and paint with a click, which is a one-dab stroke.
using Glimmer.Core;
using Glimmer.Formats;
using Glimmer.Tools;

namespace Glimmer.Editor.Tests;

public class CriticalFlowTests
{
    private static readonly Vec2 Dab = new(320, 300);

    private static void PaintStroke(EditorSession session, ManualScheduler scheduler)
    {
        session.Tool = ToolID.Brush;
        new CanvasInput(session, scheduler).Click(Dab);
    }

    [Fact]
    public void NewDocument()
    {
        using var h = new Harness();
        var opener = new DocumentOpener(h.Environment);
        var session = opener.CreateDocument(Docs.White());
        Assert.Same(session, Assert.Single(h.Host.Shown));
        Assert.Same(session, Assert.Single(opener.Sessions));
        Assert.Equal((800, 600), (session.State.Width, session.State.Height));
        Assert.True(session.IsUntouchedBlank);
        Assert.False(session.Document.HasUnsavedChanges);
        Assert.Equal("Untitled", session.DocumentName);
        Assert.Empty(session.Document.History);
        Assert.False(session.CanPerform(CommandID.Undo));
    }

    [Fact]
    public void PaintAndUndo()
    {
        using var h = new Harness();
        var session = h.NewSession();
        PaintStroke(session, h.Scheduler);
        // The stroke is in the history.
        Assert.Equal(["Brush Stroke"], session.Document.History.Select(t => t.ToString()));
        Assert.True(session.Document.HasUnsavedChanges);
        var painted = Docs.Pixel(session.State.Layers[0], (int)Dab.X, (int)Dab.Y);
        Assert.True(painted.X < 0.05 && painted.W > 0.99, $"the dab didn't paint: {painted}");
        Assert.Equal("Undo Brush Stroke", session.Title(CommandID.Undo)?.ToString());
        session.Perform(CommandID.Undo);
        h.Scheduler.RunPosted();
        Assert.Equal(Docs.WhitePixel, Docs.Pixel(session.State.Layers[0], (int)Dab.X, (int)Dab.Y));
        Assert.False(session.Document.HasUnsavedChanges);
        Assert.True(session.CanPerform(CommandID.Redo));
        Assert.Equal("Redo Brush Stroke", session.Title(CommandID.Redo)?.ToString());
        session.Perform(CommandID.Redo);
        h.Scheduler.RunPosted();
        Assert.Equal(painted, Docs.Pixel(session.State.Layers[0], (int)Dab.X, (int)Dab.Y));
        Assert.Equal(1, session.Document.HistoryIndex);
    }

    [Fact]
    public void SaveDocument()
    {
        using var h = new Harness();
        var session = h.NewSession();
        PaintStroke(session, h.Scheduler);
        var path = h.PathIn("GlimmerUITest.glimmer");
        h.Dialogs.SaveAnswers.Enqueue(path);
        session.Perform(WindowsCommands.Save);
        h.Scheduler.RunPosted();
        // The window takes the saved name.
        Assert.Equal("GlimmerUITest", session.DocumentName);
        Assert.Equal("Untitled.glimmer", h.Dialogs.SaveQuestions.Single().Suggested);
        Assert.Equal(path, session.Document.FilePath);
        Assert.False(session.Document.HasUnsavedChanges);
        Assert.Contains(path, h.Host.Recent);
        Assert.Null(session.ErrorMessage);

        // A second Save writes the same file without asking.
        PaintStroke(session, h.Scheduler);
        Assert.True(session.Document.HasUnsavedChanges);
        session.Perform(WindowsCommands.Save);
        h.Scheduler.RunPosted();
        Assert.Single(h.Dialogs.SaveQuestions);
        Assert.False(session.Document.HasUnsavedChanges);

        // Reopening gives the document as it was saved, under its own name.
        var opener = new DocumentOpener(h.Environment);
        opener.Open([path]);
        var reopened = Assert.Single(opener.Sessions);
        Assert.Equal("GlimmerUITest", reopened.DocumentName);
        Assert.Equal(path, reopened.Document.FilePath);
        Assert.False(reopened.Document.HasUnsavedChanges);
        Assert.Equal(Docs.Pixel(session.State.Layers[0], (int)Dab.X, (int)Dab.Y), Docs.Pixel(reopened.State.Layers[0], (int)Dab.X, (int)Dab.Y));
        // Opening it again brings the open one forward instead of a second tab.
        opener.Open([path]);
        Assert.Single(opener.Sessions);
        Assert.Contains(nameof(IDocumentHost.BringForward), h.Host.Calls);
    }

    [Fact]
    public void ExportPNGAndReopen()
    {
        using var h = new Harness();
        var session = h.NewSession();
        PaintStroke(session, h.Scheduler);
        session.Perform(CommandID.Export);
        Assert.True(session.IsExportSheetPresented);
        // The sheet's Export… button: the save dialog, then the file.
        var path = h.PathIn("GlimmerUITest.png");
        h.Dialogs.SaveAnswers.Enqueue(path);
        session.Export(new ExportOptions(ExportFormat.Png));
        h.Scheduler.RunPosted();
        Assert.True(File.Exists(path));
        Assert.False(session.IsExporting);
        Assert.StartsWith("Exported GlimmerUITest.png (", session.Toast?.ToString());
        // An export is not a save.
        Assert.True(session.Document.HasUnsavedChanges);

        // Opening the exported file makes a new 800 × 600 document; the image isn't opened in place.
        var opener = new DocumentOpener(h.Environment);
        opener.Open([path]);
        var opened = Assert.Single(opener.Sessions);
        Assert.Equal((800, 600), (opened.State.Width, opened.State.Height));
        Assert.Null(opened.Document.FilePath);
        var dab = Docs.Pixel(opened.State.Layers[0], (int)Dab.X, (int)Dab.Y);
        Assert.True(dab.X < 0.05 && dab.W > 0.99, $"{dab}");
        Assert.Equal(Docs.WhitePixel, Docs.Pixel(opened.State.Layers[0], 700, 100));
    }

    [Fact]
    public void ToastGoesAwayByItself()
    {
        using var h = new Harness();
        var session = h.NewSession();
        session.ShowToast("Hello");
        Assert.Equal("Hello", session.Toast?.ToString());
        h.Scheduler.Settle();
        Assert.Null(session.Toast);
    }
}
