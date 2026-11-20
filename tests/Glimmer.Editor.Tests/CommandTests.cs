// Tests for the port of App/AppCommands.swift and the command dispatch of App/EditorSession.swift
// (canPerform/perform/isChecked): every command, on an empty document and on one with something in it, and the menus
// built from them. The original has no tests at this level.
using Glimmer.Core;
using Glimmer.Tools;

namespace Glimmer.Editor.Tests;

public class CommandTests
{
    /// <summary>Every command a menu, a shortcut or the palette can send: the registry with the Windows commands, every
    /// adjustment layer and every filter.</summary>
    public static IEnumerable<CommandID> AllCommands()
    {
        var ids = new List<CommandID>(WindowsCommands.Registry.Descriptors.Select(d => d.Id));
        ids.AddRange(Enum.GetValues<AdjustmentKind>().Select(CommandID.NewAdjustment));
        ids.AddRange(Enum.GetValues<FilterKind>().Select(CommandID.Filter));
        return ids.Distinct();
    }

    public static TheoryData<string> CommandNames()
    {
        var data = new TheoryData<string>();
        foreach (var id in AllCommands()) data.Add(id.RawValue);
        return data;
    }

    /// <summary>A document with something of everything: pixels, a selection, text, a shape, a path and a guide.</summary>
    private static EditorSession Normal(Harness h)
    {
        var s = h.NewSession(Docs.WithSquare());
        s.TypeClick(new Vec2(40, 60));
        s.CommitText("Glimmer", [], s.EditingText!.Value);
        s.EditingText = null;
        s.Tool = ToolID.Shape;
        new CanvasInput(s, h.Scheduler).Drag(new Vec2(500, 400), new Vec2(600, 500));
        s.Tool = ToolID.Pen;
        var input = new CanvasInput(s, h.Scheduler);
        input.Click(new Vec2(100, 400));
        input.Click(new Vec2(200, 450));
        s.FinishPathSegment();
        s.Tool = ToolID.Move;
        s.AddGuide(GuideAxis.Horizontal, 300);
        // The square layer active, with a selection inside it.
        s.SetActiveLayer(s.State.Named("Square").Id);
        s.Edit("Select", st => st with { Selection = Selection.Rectangle(new CGRectLike(250, 200, 350, 300)) });
        h.Scheduler.Settle();
        return s;
    }

    private static void Exercise(Harness h, EditorSession s, CommandID id)
    {
        // As in the app, commands run on the UI thread: an await inside one (Auto Straighten waits for the analysis)
        // comes back through the scheduler, not on the thread pool where it would race Settle over the timer list.
        using var ui = h.Scheduler.AsUIThread();
        _ = s.IsChecked(id);
        _ = s.Title(id);
        _ = s.CanPerform(id);
        // Performed whether enabled or not: a disabled command does nothing.
        s.Perform(id);
        h.Scheduler.Settle();
        // A modal tool or a dialog a command opened ends cleanly too.
        s.CancelTransform();
        s.CancelCrop();
        s.CancelLiquify();
        s.CancelFilterDialog();
        h.Scheduler.Settle();
        _ = s.CanPerform(id);
        _ = s.IsChecked(id);
    }

    [Theory]
    [MemberData(nameof(CommandNames))]
    public void EveryCommandOnAnEmptyDocument(string raw)
    {
        using var h = new Harness();
        Exercise(h, h.NewSession(), new CommandID(raw));
    }

    [Theory]
    [MemberData(nameof(CommandNames))]
    public void EveryCommandOnADocumentWithoutLayers(string raw)
    {
        using var h = new Harness();
        Exercise(h, h.NewSession(new DocumentState(640, 480)), new CommandID(raw));
    }

    [Theory]
    [MemberData(nameof(CommandNames))]
    public void EveryCommandOnANormalDocument(string raw)
    {
        using var h = new Harness();
        var s = Normal(h);
        Exercise(h, s, new CommandID(raw));
        if (new CommandID(raw) == WindowsCommands.Close) return;
        // And once more with the text layer active.
        if (s.State.AllLayers.FirstOrDefault(l => l.IsText) is { } text)
        {
            s.SetActiveLayer(text.Id);
            Exercise(h, s, new CommandID(raw));
        }
    }

    [Fact]
    public void EveryMenuEntryHasADescriptor()
    {
        var registry = WindowsCommands.Registry;
        var missing = new List<string>();
        void Walk(IEnumerable<AppMenus.Entry> entries)
        {
            foreach (var e in entries)
            {
                switch (e)
                {
                    case AppMenus.Entry.Command c when registry.Descriptor(c.Id) is null: missing.Add(c.Id.RawValue); break;
                    case AppMenus.Entry.Toggle t when registry.Descriptor(t.Id) is null: missing.Add(t.Id.RawValue); break;
                    case AppMenus.Entry.Submenu m: Walk(m.Entries); break;
                }
            }
        }
        foreach (var (_, entries) in AppMenus.Menus) Walk(entries);
        Assert.Empty(missing);
        Assert.Equal(["File", "Edit", "Image", "Layer", "Select", "Filter", "AI", "View"], AppMenus.Menus.Select(m => m.Title));
    }

    [Fact]
    public void WindowsCommandsAreNotInCore()
    {
        foreach (var d in WindowsCommands.Descriptors) Assert.Null(CommandRegistry.Standard.Descriptor(d.Id));
        Assert.Equal(CommandRegistry.Standard.Descriptors.Length + WindowsCommands.Descriptors.Count, WindowsCommands.Registry.Descriptors.Length);
    }

    [Fact]
    public void LayerCommandsAreEnabledByWhatIsActive()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.WithSquare());
        Assert.True(s.CanPerform(CommandID.SendLayerBackward));
        Assert.False(s.CanPerform(CommandID.BringLayerForward));
        Assert.False(s.CanPerform(CommandID.UngroupLayers));
        Assert.False(s.CanPerform(CommandID.ToggleMask));
        Assert.True(s.CanPerform(CommandID.AddMaskRevealAll));
        Assert.True(s.CanPerform(CommandID.ToggleClipping));
        s.Perform(CommandID.SendLayerBackward);
        Assert.Equal("Square", s.State.Layers[0].Name);
        Assert.False(s.CanPerform(CommandID.ToggleClipping));
        s.Perform(CommandID.BringLayerToFront);
        s.Perform(CommandID.ToggleClipping);
        Assert.True(s.IsChecked(CommandID.ToggleClipping));
        s.Perform(CommandID.AddMaskHideAll);
        Assert.False(s.CanPerform(CommandID.AddMaskRevealAll));
        s.Perform(CommandID.ToggleMask);
        Assert.True(s.IsChecked(CommandID.ToggleMask));
        s.Perform(CommandID.DeleteMask);
        Assert.Null(s.ActiveLayer!.Mask);
        s.Perform(CommandID.GroupLayers);
        Assert.True(s.ActiveLayer!.IsGroup);
        Assert.True(s.CanPerform(CommandID.UngroupLayers));
        s.Perform(CommandID.UngroupLayers);
        Assert.False(s.State.AllLayers.Any(l => l.IsGroup));
        Assert.Equal(["Send Backward", "Bring to Front", "Create Clipping Mask", "Add Layer Mask", "Disable Layer Mask", "Delete Layer Mask",
                      "Group Layer", "Ungroup"], s.Document.History.Select(t => t.ToString()));
    }

    [Fact]
    public void AdjustmentAndFillLayers()
    {
        using var h = new Harness();
        var s = h.NewSession();
        var curves = CommandID.NewAdjustment(AdjustmentKind.Curves);
        Assert.True(s.CanPerform(curves));
        s.Perform(curves);
        Assert.Equal("Curves", s.ActiveLayer!.Name);
        Assert.Equal("New {0}", s.Document.History[^1].Template);
        Assert.Equal("New Curves", s.Document.History[^1].ToString());
        s.Perform(CommandID.NewGradientFill);
        Assert.Equal("Gradient Fill", s.ActiveLayer!.Name);
        Assert.True(s.CanPerform(CommandID.RasterizeLayer));
        s.Perform(CommandID.RasterizeLayer);
        Assert.True(s.ActiveLayer!.IsRaster);
    }

    [Fact]
    public void LayerNamesAreInThePersonsLanguage()
    {
        using var h = new Harness();
        var env = new EditorEnvironment(h.Settings, h.Scheduler, h.Host, h.Dialogs, h.Clipboard, h.Sound, h.PathIn("temp"), h.PathIn("data"))
        {
            Localize = english => english switch { "Layer" => "Lager", "Curves" => "Kurvor", _ => english },
        };
        var s = new EditorSession(new GlimmerDocument(env, Docs.White()), env);
        s.Perform(CommandID.NewLayer);
        Assert.Equal("Lager 1", s.ActiveLayer!.Name);
        s.Perform(CommandID.NewAdjustment(AdjustmentKind.Curves));
        Assert.Equal("Kurvor", s.ActiveLayer!.Name);
        // Step names stay templates for the app to translate.
        Assert.Equal("New Layer", s.Document.History[0].Template);
    }

    [Fact]
    public void ToggleChecksFollowTheDocumentAndTheWindow()
    {
        using var h = new Harness();
        var s = h.NewSession();
        Assert.False(s.IsChecked(CommandID.ToggleRulers));
        s.Perform(CommandID.ToggleRulers);
        Assert.True(s.IsChecked(CommandID.ToggleRulers));
        Assert.Equal("Show Rulers", s.Document.History[^1].ToString());
        s.Perform(CommandID.ToggleGrid);
        Assert.True(s.IsChecked(CommandID.ToggleGrid));
        Assert.True(s.IsChecked(CommandID.ToggleSnap));
        s.Perform(CommandID.ToggleNavigator);
        Assert.True(s.IsChecked(CommandID.ToggleNavigator));
        s.Perform(CommandID.HideInterface);
        Assert.True(s.InterfaceHidden && s.IsChecked(CommandID.HideInterface));
        s.Perform(CommandID.ToggleClassicLayout);
        Assert.True(s.IsChecked(CommandID.ToggleClassicLayout));
        Assert.True(h.Settings.Get(Preferences.ClassicLayoutKey, false));
        s.Perform(CommandID.ToggleGammaBlending);
        Assert.True(s.IsChecked(CommandID.ToggleGammaBlending));
    }

    [Fact]
    public void ViewCommandsGoToTheCanvas()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.White(2000, 1000));
        s.Canvas.ViewDidResize(new Vec2(1000, 800));
        double fit = s.Canvas.Zoom;
        s.Perform(CommandID.ActualSize);
        Assert.Equal(1, s.Canvas.Zoom, 6);
        s.Perform(CommandID.ZoomIn);
        Assert.True(s.Canvas.Zoom > 1);
        s.Perform(CommandID.FitToWindow);
        Assert.Equal(fit, s.Canvas.Zoom, 6);
        Assert.False(s.CanPerform(CommandID.ResetViewRotation));
        s.Perform(CommandID.RotateViewRight);
        Assert.Equal(15, s.Canvas.RotationDegrees, 6);
        Assert.True(s.CanPerform(CommandID.ResetViewRotation));
        s.Perform(CommandID.ResetViewRotation);
        Assert.Equal(0, s.Canvas.RotationDegrees, 6);
        Assert.Empty(s.Document.History);
    }

    [Fact]
    public void PanelCommandsShowTheirTab()
    {
        using var h = new Harness();
        var s = h.NewSession();
        s.ShowLayersPanel = false;
        s.Perform(CommandID.ShowHistory);
        Assert.True(s.ShowLayersPanel);
        Assert.Equal(InspectorTab.History, s.InspectorTab);
        s.Perform(CommandID.AiExpand);
        Assert.Equal((InspectorTab.AI, AISection.Expand), (s.InspectorTab, s.AISection));
        s.Perform(CommandID.CommandPalette);
        Assert.True(s.IsPaletteOpen);
        s.Perform(CommandID.NewDocument);
        s.Perform(WindowsCommands.LinkedFilesWaiting);
        Assert.Equal([nameof(IDocumentHost.ShowNewDocumentWindow), nameof(IDocumentHost.ShowLinkedWaiting)], h.Host.Calls);
    }

    [Fact]
    public void FileOpenChoosesDocumentsAndOpensThem()
    {
        using var h = new Harness();
        var opener = new DocumentOpener(h.Environment);
        var s = opener.CreateDocument(Docs.TwoTone());
        s.Perform(CommandID.NewLayer);
        var saved = h.PathIn("two.glimmer");
        s.SaveTo(saved);
        h.Scheduler.RunPosted();
        var other = h.PathIn("other.glimmer");
        GlimmerPackage.Save(Docs.White(), other);
        // Cancelled: nothing.
        s.Perform(CommandID.OpenDocument);
        Assert.Single(opener.Sessions);
        // Two chosen: one is open already (brought forward), the other opens.
        h.Dialogs.DocumentAnswers.Enqueue([saved, other]);
        s.Perform(CommandID.OpenDocument);
        Assert.Equal(2, opener.Sessions.Count);
        Assert.Contains(nameof(IDocumentHost.BringForward), h.Host.Calls);
    }

    [Fact]
    public void OpenPackageFolderOpensADocumentFromAMac()
    {
        using var h = new Harness();
        var opener = new DocumentOpener(h.Environment);
        var s = opener.CreateDocument(Docs.TwoTone());
        Assert.True(s.CanPerform(WindowsCommands.OpenPackageFolder));
        // A Mac document is a folder; Save a Copy for Mac writes one.
        var package = h.PathIn("From the Mac.glimmer");
        GlimmerDocument.WritePackageFolder(GlimmerPackage.Contents(Docs.WithSquare()), package);
        Assert.True(Directory.Exists(package));
        h.Dialogs.FolderAnswers.Enqueue(package);
        s.Perform(WindowsCommands.OpenPackageFolder);
        Assert.Equal("Open Package Folder", Assert.Single(h.Dialogs.FolderQuestions));
        var mac = opener.Sessions[^1];
        Assert.Equal("From the Mac", mac.DocumentName);
        Assert.Equal(2, mac.State.AllLayers.Length);
        Assert.Equal(package, mac.Document.FilePath);
        // Its first save writes one file in its place.
        mac.Perform(CommandID.NewLayer);
        mac.Perform(WindowsCommands.Save);
        h.Scheduler.RunPosted();
        Assert.True(File.Exists(package));
        Assert.Equal(3, GlimmerPackage.Load(package).AllLayers.Length);
        // A folder that isn't a document says so.
        var plain = h.PathIn("Pictures");
        Directory.CreateDirectory(plain);
        h.Dialogs.FolderAnswers.Enqueue(plain);
        s.Perform(WindowsCommands.OpenPackageFolder);
        Assert.StartsWith("Pictures is not a Glimmer document.", Assert.Single(h.Host.Errors).ToString());
        // Without an opener (a session on its own) the command is off.
        Assert.False(new EditorSession(new GlimmerDocument(h.MakeEnvironment(), Docs.White()), h.MakeEnvironment()).CanPerform(WindowsCommands.OpenPackageFolder));
    }

    [Fact]
    public void CloseAsksAboutUnsavedChanges()
    {
        using var h = new Harness();
        var s = h.NewSession();
        // Nothing to ask about: closes.
        s.Perform(WindowsCommands.Close);
        Assert.Same(s, Assert.Single(h.Host.Closed));
        Assert.Equal(0, h.Host.SaveQuestions);

        var t = h.NewSession();
        t.Perform(CommandID.NewLayer);
        h.Host.SaveAnswer = SaveChangesChoice.Cancel;
        t.Perform(WindowsCommands.Close);
        Assert.Single(h.Host.Closed);
        Assert.Equal(1, h.Host.SaveQuestions);
        // Save, but the save dialog is cancelled: stays open.
        h.Host.SaveAnswer = SaveChangesChoice.Save;
        Assert.False(t.TryClose());
        // Save to a file: saved, then closed.
        var path = h.PathIn("closing.glimmer");
        h.Dialogs.SaveAnswers.Enqueue(path);
        Assert.True(t.TryClose());
        Assert.True(File.Exists(path));
        Assert.Equal("closing", t.DocumentName);
        Assert.Contains(path, h.Host.Recent);
        // Don't Save: closes without writing.
        var u = h.NewSession();
        u.Perform(CommandID.NewLayer);
        h.Host.SaveAnswer = SaveChangesChoice.DontSave;
        Assert.True(u.TryClose());
        // Closing twice is harmless.
        Assert.True(u.TryClose());
    }

    [Fact]
    public void SaveAsAndSaveACopyForMac()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.Perform(CommandID.NewLayer);
        var first = h.PathIn("first.glimmer");
        h.Dialogs.SaveAnswers.Enqueue(first);
        s.Perform(WindowsCommands.SaveAs);
        h.Scheduler.RunPosted();
        Assert.Equal(first, s.Document.FilePath);
        // A copy for the Mac: a folder, and the document keeps its own file and state.
        s.Perform(CommandID.NewLayer);
        var mac = h.PathIn("for mac.glimmer");
        h.Dialogs.SaveAnswers.Enqueue(mac);
        s.Perform(WindowsCommands.SaveCopyForMac);
        h.Scheduler.RunPosted();
        Assert.True(Directory.Exists(mac));
        Assert.Equal("A Mac opens this folder as a Glimmer document.", h.Dialogs.SaveQuestions[^1].Message?.ToString());
        Assert.Equal(first, s.Document.FilePath);
        Assert.True(s.Document.HasUnsavedChanges);
        Assert.Equal("Saved for mac.glimmer", s.Toast?.ToString());
        var read = GlimmerPackage.Load(mac);
        Assert.Equal(s.State.AllLayers.Length, read.AllLayers.Length);
        // Writing it again replaces it.
        h.Dialogs.SaveAnswers.Enqueue(mac);
        s.Perform(WindowsCommands.SaveCopyForMac);
        h.Scheduler.RunPosted();
        Assert.Equal(s.State.AllLayers.Length, GlimmerPackage.Load(mac).AllLayers.Length);
        Assert.Empty(Directory.GetFileSystemEntries(h.Root, "*.saving"));
        Assert.Empty(Directory.GetFileSystemEntries(h.Root, "*.previous"));
        // Save As again: a new file, the window takes its name.
        var second = h.PathIn("second.glimmer");
        h.Dialogs.SaveAnswers.Enqueue(second);
        s.Perform(WindowsCommands.SaveAs);
        h.Scheduler.RunPosted();
        Assert.Equal("second", s.DocumentName);
        Assert.Equal("first.glimmer", h.Dialogs.SaveQuestions[^1].Suggested);
    }

    [Fact]
    public void AFailedSaveSaysWhyAndKeepsTheDocument()
    {
        using var h = new Harness();
        var s = h.NewSession();
        s.Perform(CommandID.NewLayer);
        // A folder that isn't there (a drive that went away).
        var blocked = Path.Combine(h.Root, "gone", "blocked.glimmer");
        s.SaveTo(blocked);
        h.Scheduler.RunPosted();
        Assert.NotNull(s.ErrorMessage);
        Assert.StartsWith("The document couldn't be saved", s.ErrorMessage!.ToString());
        Assert.True(s.Document.HasUnsavedChanges);
        Assert.Null(s.Document.FilePath);
        Assert.Contains(h.Log, line => line.StartsWith("couldn't save blocked.glimmer"));
    }
}
