// Tests for the port of App/DocumentOpener.swift (images open as new untitled documents, an untouched blank window
// takes the first image, .glimmer documents open as themselves, refusals are said), placing images, and linked editing
// with the link journal (App/EditorSession+Linked.swift, App/LinkJournal.swift). The original has no tests at this level.
using Glimmer.Core;
using Glimmer.Formats;
using Glimmer.Tools;
using SkiaSharp;

namespace Glimmer.Editor.Tests;

public class OpenerTests
{
    [Fact]
    public void FormatsOnlyTheSystemReadsOpenThroughThePlatformDecoder()
    {
        using var h = new Harness();
        var decoder = Assert.IsType<TestPlatformDecoder>(ImageImporter.PlatformDecoder);
        // A picture SkiaSharp can't read (a TIFF or HEIC in the app; the tests' own format here).
        var tif = h.PathIn("scan.tif");
        File.WriteAllBytes(tif, TestPlatformDecoder.Solid(24, 12, 0, 0, 255));
        var opener = new DocumentOpener(h.Environment);
        int before = decoder.Decodes;
        opener.Open([tif]);
        var s = Assert.Single(opener.Sessions);
        Assert.True(decoder.Decodes > before);
        Assert.Equal((24, 12), (s.State.Width, s.State.Height));
        var p = Docs.Pixel(s.State.Layers[0], 5, 5);
        Assert.True(p.Z > 0.99 && p.X < 0.01, $"{p}");
        // One that claims more than it holds is damaged, never half a picture.
        var cut = h.PathIn("cut.tif");
        File.WriteAllBytes(cut, TestPlatformDecoder.Solid(24, 12, 0, 0, 255)[..40]);
        opener.Open([cut]);
        Assert.Equal("The image file is damaged or incomplete.", Assert.Single(h.Host.Errors).ToString());
    }

    /// <summary>A PNG of one colour.</summary>
    private static string Png(Harness h, string name, int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var path = h.PathIn(name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static SKColor PixelOf(string path, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(path);
        return bitmap.GetPixel(x, y);
    }

    [Fact]
    public void ImagesOpenAsNewUntitledDocuments()
    {
        using var h = new Harness();
        var red = Png(h, "red.png", 64, 32, SKColors.Red);
        var blue = Png(h, "blue.png", 16, 16, SKColors.Blue);
        var opener = new DocumentOpener(h.Environment);
        opener.Open([red, blue]);
        Assert.Equal(2, opener.Sessions.Count);
        var first = opener.Sessions[0];
        Assert.Equal((64, 32), (first.State.Width, first.State.Height));
        Assert.Null(first.Document.FilePath);
        Assert.Equal("red", first.State.Layers[0].Name);
        Assert.False(first.Document.HasUnsavedChanges);
        Assert.Equal([red, blue], h.Host.Recent);
        // Saving never writes over the image.
        Assert.Equal("Untitled.glimmer", Suggested(h, first));
    }

    private static string Suggested(Harness h, EditorSession s)
    {
        s.Perform(WindowsCommands.Save);
        return h.Dialogs.SaveQuestions[^1].Suggested;
    }

    [Fact]
    public void AnUntouchedBlankWindowTakesTheFirstImage()
    {
        using var h = new Harness();
        var opener = new DocumentOpener(h.Environment);
        var blank = opener.CreateDocument(Docs.White());
        var red = Png(h, "red.png", 64, 32, SKColors.Red);
        opener.Open([red]);
        Assert.Same(blank, Assert.Single(opener.Sessions));
        Assert.Equal((64, 32), (blank.State.Width, blank.State.Height));
        Assert.Equal("Open Image", blank.Document.History[^1].ToString());
        // A window with something in it doesn't: the next image gets its own.
        opener.Open([Png(h, "blue.png", 8, 8, SKColors.Blue)]);
        Assert.Equal(2, opener.Sessions.Count);
    }

    [Fact]
    public void PlacingAddsLayersCentred()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.WithSquare());
        var red = Png(h, "IMG_0042.png", 100, 50, SKColors.Red);
        h.Dialogs.ImageAnswers.Enqueue([red]);
        s.Perform(CommandID.PlaceImage);
        h.Scheduler.Settle();
        var placed = s.ActiveLayer!;
        Assert.Equal("IMG_0042", placed.Name);
        Assert.Equal("Place Image", s.Document.History[^1].ToString());
        var bounds = new Selection(placed.Tiles).Bounds(s.State.Bounds)!.Value;
        Assert.Equal(new PixelRect(350, 275, 100, 50), bounds);
        // Unreadable data says why.
        s.PlaceImageData([1, 2, 3], "junk");
        Assert.Equal("The image could not be read.", s.ErrorMessage?.ToString());
    }

    [Fact]
    public void FilesThatCantBeOpenedSayWhy()
    {
        using var h = new Harness();
        var opener = new DocumentOpener(h.Environment);
        var notAnImage = h.PathIn("notes.png");
        File.WriteAllText(notAnImage, "not a picture");
        var project = h.PathIn("project.aips");
        File.WriteAllText(project, "{}");
        var broken = h.PathIn("broken.glimmer");
        File.WriteAllText(broken, "not a document");
        opener.Open([notAnImage, project, broken]);
        Assert.Empty(opener.Sessions);
        Assert.Equal(3, h.Host.Errors.Count);
        Assert.Equal("The image could not be read.", h.Host.Errors[0].ToString());
        Assert.Contains(h.Log, line => line.StartsWith("couldn't open notes.png"));
    }

    /// <summary>A Photoshop file of two 8-bit layers: a red background and a half-transparent blue square.</summary>
    internal static byte[] SamplePSD(int width = 20, int height = 10)
    {
        byte[] Plane(byte value) => [.. Enumerable.Repeat(value, width * height)];
        var background = new PSDFile.Layer("Red", new PSDFile.Rect(0, 0, height, width), "norm", 255, isClipped: false, isVisible: true, PSDFile.GroupMark.None, null,
                                           [], new Dictionary<int, ReadOnlyMemory<byte>> { [0] = Plane(255), [1] = Plane(0), [2] = Plane(0), [-1] = Plane(255) }, bytesPerSample: 1);
        var square = new PSDFile.Layer("Blue", new PSDFile.Rect(2, 2, 6, 6), "mul ", 128, isClipped: false, isVisible: true, PSDFile.GroupMark.None, null,
                                       [], new Dictionary<int, ReadOnlyMemory<byte>> { [0] = new byte[16], [1] = new byte[16], [2] = Enumerable.Repeat((byte)255, 16).ToArray(), [-1] = Enumerable.Repeat((byte)255, 16).ToArray() },
                                       bytesPerSample: 1);
        var file = new PSDFile(new PSDFile.FileHeader(isLarge: false, channels: 3, width: width, height: height, depth: 8, mode: PSDFile.ColorMode.Rgb),
                               [background, square], [Plane(255), Plane(0), Plane(0)], null);
        return PSDWriter.Data(file);
    }

    [Fact]
    public void APhotoshopFileOpensWithItsLayersAndItsReport()
    {
        using var h = new Harness();
        var opener = new DocumentOpener(h.Environment);
        // Recognised by its signature, not its name.
        var psd = h.PathIn("named-like-a-picture.png");
        File.WriteAllBytes(psd, SamplePSD());
        opener.Open([psd]);
        var s = Assert.Single(opener.Sessions);
        Assert.Equal(["Red", "Blue"], s.State.Layers.Select(l => l.Name));
        Assert.Equal(BlendMode.Multiply, s.State.Layers[1].BlendMode);
        Assert.NotNull(s.ImportReport);
        Assert.True(s.IsImportReportPresented);
        Assert.True(s.CanPerform(CommandID.ImportReport));
        Assert.Null(s.Document.FilePath);
        Assert.Contains(psd, h.Host.Recent);
        Assert.True(DocumentOpener.IsPhotoshopFile("8BPS!"u8));
        Assert.False(DocumentOpener.IsPhotoshopFile("8BP"u8));
    }

    [Fact]
    public void ADamagedPhotoshopFileSaysWhy()
    {
        using var h = new Harness();
        var opener = new DocumentOpener(h.Environment);
        var psd = h.PathIn("cut.psd");
        File.WriteAllBytes(psd, SamplePSD()[..40]);
        opener.Open([psd]);
        Assert.Empty(opener.Sessions);
        Assert.StartsWith("This Photoshop file is cut short or damaged", Assert.Single(h.Host.Errors).ToString());
    }

    [Fact]
    public void ARawFileWithoutADeveloperOpensItsPreviewAsAPicture()
    {
        using var h = new Harness();
        var opener = new DocumentOpener(h.Environment);
        // A file named like a camera's RAW that the general reader can read (a preview).
        var raw = h.PathIn("photo.dng");
        File.Copy(Png(h, "preview.png", 40, 30, SKColors.Green), raw);
        opener.Open([raw]);
        h.Scheduler.RunPosted();
        Assert.Contains(nameof(IDocumentHost.ShowProgress), h.Host.Calls);
        var s = Assert.Single(opener.Sessions);
        Assert.Equal((40, 30), (s.State.Width, s.State.Height));
        // One that nothing can read says the RAW reason.
        var junk = h.PathIn("junk.cr2");
        File.WriteAllText(junk, "nothing");
        opener.Open([junk]);
        h.Scheduler.RunPosted();
        Assert.StartsWith("Windows gives no picture for this RAW file.", Assert.Single(h.Host.Errors).ToString());
    }

    [Fact]
    public void LinkedEditingSavesBackIntoTheFile()
    {
        using var h = new Harness();
        var opener = new DocumentOpener(h.Environment);
        var file = Png(h, "from-flimmer.png", 200, 100, SKColors.White);
        opener.Open([file], requester: "Flimmer");
        h.Scheduler.RunPosted();
        var s = Assert.Single(opener.Sessions);
        Assert.Equal(file, s.LinkedImage!.Path);
        Assert.Equal("from-flimmer.png", s.DocumentName);
        Assert.Equal("Save Back to from-flimmer.png", s.Title(WindowsCommands.Save)?.ToString());
        Assert.True(s.CanPerform(WindowsCommands.SaveGlimmerCopy));
        // The journal holds the link from the start.
        var entry = Assert.Single(opener.Journal.Entries());
        Assert.False(entry.Record.Unreturned);
        // Paint: the step is kept in the journal, and the document never asks the app's own save question.
        s.Tool = ToolID.Brush;
        new CanvasInput(s, h.Scheduler).Click(new Vec2(100, 50));
        h.Scheduler.RunPosted();
        Assert.True(s.HasUnreturnedEdits);
        Assert.False(s.Document.HasUnsavedChanges);
        Assert.True(Assert.Single(opener.Journal.Entries()).Record.Unreturned);
        // Save writes the picture back.
        s.Perform(WindowsCommands.Save);
        h.Scheduler.RunPosted();
        Assert.Equal("Saved from-flimmer.png for Flimmer", s.Toast?.ToString());
        Assert.False(s.HasUnreturnedEdits);
        Assert.Equal(SKColors.Black, PixelOf(file, 100, 50));
        Assert.Equal(SKColors.White, PixelOf(file, 10, 10));
        // Closing with nothing waiting: closes, and the entry goes.
        Assert.True(s.TryClose());
        h.Scheduler.RunPosted();
        Assert.Empty(opener.Journal.Entries());
    }

    [Fact]
    public void KeepForLaterBringsTheEditsBackAtTheNextStart()
    {
        using var h = new Harness();
        var opener = new DocumentOpener(h.Environment);
        var file = Png(h, "kept.png", 200, 100, SKColors.White);
        opener.Open([file], requester: "Jämmer");
        h.Scheduler.RunPosted();
        var s = opener.Sessions[0];
        s.Perform(CommandID.NewLayer);
        h.Scheduler.RunPosted();
        h.Host.LinkedAnswer = LinkedCloseChoice.KeepForLater;
        Assert.True(s.TryClose());
        h.Scheduler.RunPosted();
        Assert.True(opener.Journal.IsIdle);
        // The file wasn't written; the work waits in the journal.
        Assert.Equal(SKColors.White, PixelOf(file, 100, 50));
        var waiting = Assert.Single(opener.Journal.Entries());
        Assert.True(waiting.Record.Unreturned);
        Assert.True(waiting.Record.KeptKnowingly);

        var next = new ManualScheduler();
        var again = new DocumentOpener(h.MakeEnvironment(next));
        Assert.Empty(again.Launch());
        next.RunPosted();
        var back = Assert.Single(again.Sessions);
        Assert.True(back.KeptFromLastTime);
        Assert.Equal(2, back.State.AllLayers.Length);
        Assert.StartsWith("Kept from last time: the edits of kept.png are not saved back to Jämmer yet.", back.Toast?.ToString());
        Assert.Equal(file, back.LinkedImage!.Path);
        // Opening the same file from the sibling again brings this window forward.
        again.Open([file], requester: "Jämmer");
        Assert.Single(again.Sessions);
    }

    [Fact]
    public void LinkedFilesWaitingListsOpensAndDiscardsKeptEdits()
    {
        using var h = new Harness();
        var opener = new DocumentOpener(h.Environment);
        var first = Png(h, "first.png", 40, 40, SKColors.White);
        var second = Png(h, "second.png", 40, 40, SKColors.White);
        opener.Open([first], requester: "Flimmer");
        opener.Open([second], requester: "Jämmer");
        h.Scheduler.RunPosted();
        foreach (var s in opener.Sessions.ToList())
        {
            s.Perform(CommandID.NewLayer);
            h.Scheduler.RunPosted();
            s.Perform(CommandID.NewLayer);
            h.Scheduler.RunPosted();
        }
        h.Host.LinkedAnswer = LinkedCloseChoice.KeepForLater;
        foreach (var s in opener.Sessions.ToList()) Assert.True(s.TryClose());
        h.Scheduler.RunPosted();
        var waiting = opener.LinkedFilesWaiting;
        var entries = waiting.Entries();
        Assert.Equal(["first.png", "second.png"], entries.Select(e => e.FileName).Order());
        var kept = entries.Single(e => e.FileName == "first.png");
        Assert.Equal("Flimmer", kept.Requester);
        Assert.True(kept.Unreturned);
        Assert.Equal(2, kept.UnreturnedSteps);
        Assert.True(kept.Bytes > 0);
        Assert.Empty(waiting.Broken);
        // Opened: it is in a window, so no longer waiting.
        Assert.True(waiting.Open(kept));
        h.Scheduler.RunPosted();
        Assert.Equal(["second.png"], waiting.Entries().Select(e => e.FileName));
        Assert.Equal(3, opener.Sessions.Single().State.AllLayers.Length);
        // Discarded: to the Recycle Bin, and gone from the list.
        Assert.True(waiting.Discard(waiting.Entries().Single()));
        h.Scheduler.RunPosted();
        Assert.Single(h.Recycled);
        Assert.Empty(waiting.Entries());
        // An entry that has gone meanwhile can't be opened.
        Assert.False(waiting.Open(kept with { Id = "GONE" }));
    }

    [Fact]
    public void DiscardMovesTheWorkToTheRecycleBin()
    {
        using var h = new Harness();
        var opener = new DocumentOpener(h.Environment);
        var file = Png(h, "discard.png", 50, 50, SKColors.White);
        opener.Open([file], requester: "Flimmer");
        h.Scheduler.RunPosted();
        var s = opener.Sessions[0];
        s.Perform(CommandID.NewLayer);
        h.Scheduler.RunPosted();
        h.Host.LinkedAnswer = LinkedCloseChoice.Cancel;
        Assert.False(s.TryClose());
        h.Host.LinkedAnswer = LinkedCloseChoice.Discard;
        Assert.True(s.TryClose());
        h.Scheduler.RunPosted();
        Assert.Single(h.Recycled);
        Assert.Empty(opener.Journal.Entries());
    }

    [Fact]
    public void AFileThatWentAwayMakesABrokenLink()
    {
        using var h = new Harness();
        var opener = new DocumentOpener(h.Environment);
        var file = Png(h, "gone.png", 50, 50, SKColors.White);
        opener.Open([file], requester: "Flimmer");
        h.Scheduler.RunPosted();
        var s = opener.Sessions[0];
        File.Delete(file);
        h.Scheduler.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(s.LinkedImage);
        Assert.Equal("gone.png is no longer where it was, so Glimmer can't save it back to Flimmer.", s.BrokenLink!.Reason);
        s.Perform(WindowsCommands.Save);
        Assert.StartsWith("gone.png is no longer where it was", s.ErrorMessage?.ToString());
        // Back again: the link is whole.
        Png(h, "gone.png", 50, 50, SKColors.White);
        h.Scheduler.Advance(TimeSpan.FromSeconds(2));
        Assert.NotNull(s.LinkedImage);
        Assert.Null(s.BrokenLink);
    }
}
