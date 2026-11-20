// Ported from Packages/GlimmerKit/Tests/GlimmerCoreTests/GlimmerPackageTests.swift
// (ImageImporterTests belong to the package that ports ImageImporter.swift.)

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Glimmer.Core.Tests.TestTiles;

namespace Glimmer.Core.Tests;

public class GlimmerPackageTests
{
    [Fact]
    public void RoundTripIsBitExact()
    {
        var state = SampleState();
        var read = GlimmerPackage.State(GlimmerPackage.Contents(state));

        Assert.True(read.Width == 600 && read.Height == 400);
        Assert.True(read.ColorSpace == WorkingColorSpace.DisplayP3 && read.BlendSpace == BlendSpace.Gamma);
        Assert.Equal(state.ActiveLayerID, read.ActiveLayerID);
        Assert.Equal(state.Layers.Select(l => l.Id), read.Layers.Select(l => l.Id));
        Assert.Equal(0.4, read.Layers[1].Opacity);
        Assert.Equal(BlendMode.Multiply, read.Layers[1].BlendMode);
        Assert.False(read.Layers[1].IsVisible);
        foreach (var (a, b) in state.Layers.Zip(read.Layers)) AssertSamePixels(a.Tiles, b.Tiles);
        var solid = read.Layers[1].Tiles[new TileCoord(1, 0)]!;
        Assert.True(solid.IsSolid, "solid tile was not preserved as solid");
        Assert.Equal(new Vector4(0.2f, 0.1f, 0.05f, 0.5f), solid.SolidColor);
    }

    [Fact]
    public void RoundTripThroughDisk()
    {
        var dir = TempFolder("rt");
        try
        {
            var path = Path.Combine(dir, "doc.glimmer");
            var state = SampleState();
            GlimmerPackage.Save(state, path);
            Assert.True(File.Exists(path), "a document is one file here");
            var read = GlimmerPackage.Load(path);
            foreach (var (a, b) in state.Layers.Zip(read.Layers)) AssertSamePixels(a.Tiles, b.Tiles);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void EncodedLayerCacheReusesUnchangedLayers()
    {
        var cache = new EncodedLayerCache();
        var grid = SampleState().Layers[1].Tiles;
        var first = cache.Data(grid);
        var second = cache.Data(grid);
        Assert.Same(first, second);
        var changed = grid.SetItem(new TileCoord(5, 5), Tile.Solid(new Vector4(1, 0, 0, 1)));
        Assert.False(cache.Data(changed).AsSpan().SequenceEqual(first));
    }

    [Fact]
    public void EncodedLayerCacheStaysWithinItsByteBudget()
    {
        var cache = new EncodedLayerCache(limit: 64, byteLimit: 200);
        long total = 0;
        for (int i = 0; i < 20; i++)
        {
            var grid = TileGrid.Empty.SetItem(new TileCoord(i, 0), Tile.Solid(new Vector4(i / 20f, 0, 0, 1)));
            total += cache.Data(grid).Length;
            Assert.True(cache.ByteCount <= 200);
        }
        Assert.True(total > 200, "the layers together are more than the budget");
    }

    [Fact]
    public void RejectsNewerFormat()
    {
        const string json = """{"formatVersion": 99, "width": 10, "height": 10, "colorSpace": "sRGB", "blendSpace": "linear", "layers": []}""";
        var e = Assert.Throws<GlimmerPackageException>(() => GlimmerPackage.DecodeManifest(Encoding.UTF8.GetBytes(json)));
        Assert.True(e.SameError(GlimmerPackageException.UnsupportedFormatVersion(99)));
        Assert.Equal("The document was saved by a newer version of Glimmer (format 99).", e.Message);
    }

    [Theory]
    [InlineData("""{"width": 10}""")]
    [InlineData("""{"formatVersion": 1, "width": 0, "height": 10, "colorSpace": "sRGB", "blendSpace": "linear", "layers": []}""")]
    [InlineData("""{"formatVersion": 1, "width": 10, "height": 10, "colorSpace": "cmyk", "blendSpace": "linear", "layers": []}""")]
    public void RejectsCorruptManifest(string json)
    {
        var e = Assert.Throws<GlimmerPackageException>(() => GlimmerPackage.DecodeManifest(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(GlimmerPackageError.CorruptManifest, e.Error);
    }

    /// <summary>
    /// The Mac's test hands GlimmerPackage an empty folder and a plain file. Here the empty folder is an empty package,
    /// and the plain file is one that is neither a folder nor a ZIP.
    /// </summary>
    [Fact]
    public void RejectsMissingManifestAndFlatFiles()
    {
        var e = Assert.Throws<GlimmerPackageException>(() => GlimmerPackage.State(PackageContents.Empty));
        Assert.True(e.SameError(GlimmerPackageException.MissingManifest()));
        var dir = TempFolder("flat");
        try
        {
            var empty = Path.Combine(dir, "empty.glimmer");
            File.WriteAllBytes(empty, []);
            Assert.Equal(GlimmerPackageError.NotAPackage, Assert.Throws<GlimmerPackageException>(() => GlimmerPackage.Load(empty)).Error);
            var text = Path.Combine(dir, "text.glimmer");
            File.WriteAllText(text, "not a package");
            Assert.Equal(GlimmerPackageError.NotAPackage, Assert.Throws<GlimmerPackageException>(() => GlimmerPackage.Load(text)).Error);
            var folder = Path.Combine(dir, "folder.glimmer");
            Directory.CreateDirectory(folder);
            Assert.Equal(GlimmerPackageError.MissingManifest, Assert.Throws<GlimmerPackageException>(() => GlimmerPackage.Load(folder)).Error);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void RejectsTruncatedTiles()
    {
        var data = TileCodec.Encode(SampleState().Layers[1].Tiles);
        Assert.Throws<GlimmerPackageException>(() => TileCodec.Decode(data.AsSpan(0, data.Length - 100)));
    }
}

public class TileTests
{
    [Fact]
    public void ClassifiesTransparentUniformAndVaried()
    {
        const int s = TileGeometry.Size;
        var clear = new Half[s * s * 4];
        Assert.Null(Tile.Make(clear));

        var red = new Half[s * s * 4];
        for (int i = 0; i < red.Length; i += 4) { red[i] = (Half)1; red[i + 3] = (Half)1; }
        var solid = Tile.Make(red);
        Assert.NotNull(solid);
        Assert.True(solid!.IsSolid, "expected solid");
        Assert.Equal(new Vector4(1, 0, 0, 1), solid.SolidColor);

        Assert.False(GradientTile().IsSolid, "expected texture");
    }

    [Fact]
    public void TexturePixelsRoundTrip()
    {
        var tile = GradientTile(0.25f);
        var pixels = tile.ToHalfArray();
        var again = Tile.Make(pixels)!;
        Assert.True(again.ToHalfArray().AsSpan().SequenceEqual(pixels));
    }

    [Fact]
    public void TileCoordsHandleNegativeRects()
    {
        var r = new PixelRect(-10, -300, 20, 1);
        Assert.Equal([new TileCoord(-1, -2), new TileCoord(0, -2)], r.TileCoords);
        Assert.Equal([new TileCoord(0, 0)], new PixelRect(0, 0, 256, 256).TileCoords);
        Assert.Empty(new PixelRect(0, 0, 0, 5).TileCoords);
    }

    [Fact]
    public void SignatureChangesWithContent()
    {
        var grid = TileGrid.Empty.SetItem(new TileCoord(0, 0), GradientTile());
        long before = grid.ContentSignature;
        Assert.Equal(before, grid.ContentSignature);
        grid = grid.SetItem(new TileCoord(0, 0), GradientTile());   // same pixels, new identity
        Assert.NotEqual(before, grid.ContentSignature);
    }
}

public class DocumentStateTests
{
    [Fact]
    public void NewDocumentBackgrounds()
    {
        var white = DocumentState.New(600, 300, DocumentState.Background.White);
        Assert.Single(white.Layers);
        Assert.Equal(3 * 2, white.Layers[0].Tiles.Count);
        Assert.True(DocumentState.New(10, 10, DocumentState.Background.Transparent).Layers[0].Tiles.IsEmpty);
    }

    [Fact]
    public void LayerOperations()
    {
        var state = DocumentState.New(100, 100, DocumentState.Background.White);
        var bg = state.Layers[0].Id;
        state = state.InsertLayerAboveActive(new Layer(state.NextLayerName(), new RasterContent(TileGrid.Empty)));
        Assert.True(state.Layers.Length == 2 && state.Layers[1].Name == "Layer 1");
        Assert.Equal(state.Layers[1].Id, state.ActiveLayerID);

        (state, _) = state.DuplicateActiveLayer();
        Assert.True(state.Layers.Length == 3 && state.Layers[2].Name == "Layer 1 copy");

        (state, var moved) = state.MoveActiveLayer(-2);
        Assert.True(moved);
        Assert.Equal("Layer 1 copy", state.Layers[0].Name);
        (state, var movedAgain) = state.MoveActiveLayer(-1);
        Assert.False(movedAgain);

        state = state.DeleteActiveLayer();
        Assert.Contains(bg, state.Layers.Select(l => l.Id));
        Assert.Equal(state.Layers[0].Id, state.ActiveLayerID);
    }
}

public class CommandRegistryTests
{
    [Fact]
    public void IdsAreUnique()
    {
        var ids = CommandRegistry.Standard.Descriptors.Select(d => d.Id).ToList();
        Assert.Equal(ids.Count, ids.ToHashSet().Count);
    }

    [Fact]
    public void ShortcutsAreUnique()
    {
        var shortcuts = CommandRegistry.Standard.Descriptors.Where(d => d.Shortcut is not null).Select(d => d.Shortcut!.Value).ToList();
        Assert.Equal(shortcuts.Count, shortcuts.ToHashSet().Count);
    }

    [Fact]
    public void Lookup() => Assert.Equal(new KeyShortcut('0'), CommandRegistry.Standard.Descriptor(CommandID.FitToWindow)?.Shortcut);

    /// <summary>The Windows mapping (not in the Mac's tests): Photoshop's, and Ctrl+K stays the palette.</summary>
    [Fact]
    public void ShortcutsFollowPhotoshopForWindows()
    {
        KeyShortcut? Of(CommandID id) => CommandRegistry.Standard.Descriptor(id)?.Shortcut;
        Assert.Equal("Ctrl+K", Of(CommandID.CommandPalette).ToString());
        Assert.Equal("Ctrl+Shift+Z", Of(CommandID.Redo).ToString());
        Assert.Equal("Ctrl+Alt+I", Of(CommandID.ImageSize).ToString());
        Assert.Equal("Ctrl+Alt+Shift+W", Of(CommandID.Export).ToString());
        Assert.Equal("Ctrl+Alt+F", Of(CommandID.RepeatFilter).ToString());
        Assert.Equal(FilterKind.GaussianBlur, CommandID.Filter(FilterKind.GaussianBlur).FilterKind);
        Assert.Equal(AdjustmentKind.HueSaturation, CommandID.NewAdjustment(AdjustmentKind.HueSaturation).AdjustmentKind);
        Assert.Null(CommandID.Undo.FilterKind);
    }
}

public class DocumentLimitsTests
{
    private const long Gb = 1_073_741_824;

    [Fact]
    public void ACanvasWhoseSingleLayerCantFitIsRefusedWithTheNumbers()
    {
        long mac16 = 16 * Gb;
        Assert.Equal(858_993_459, DocumentLimits.MaxCanvasPixels(mac16));
        Assert.Null(DocumentLimits.Refusal(12_000, 8_000, mac16));
        Assert.Null(DocumentLimits.Refusal(28_000, 28_000, mac16));
        var message = DocumentLimits.Refusal(30_000, 30_000, mac16) ?? "";
        Assert.True(message.Contains("30000 × 30000") && message.Contains("6.7 GB") && message.Contains("16 GB") && message.Contains("858 megapixels"), message);
        // The same canvas fits a bigger computer and not a smaller one.
        Assert.Null(DocumentLimits.Refusal(30_000, 30_000, 32 * Gb));
        Assert.Null(DocumentLimits.Refusal(20_000, 20_000, 8 * Gb));
        Assert.NotNull(DocumentLimits.Refusal(24_000, 20_000, 8 * Gb));
        Assert.NotNull(DocumentLimits.Refusal(long.MaxValue / 2, 4, mac16));   // an overflowing size is refused too
    }

    [Fact]
    public void ADocumentThatOutgrowsTheMacIsMentionedNotStopped()
    {
        long mac16 = 16 * Gb;
        // The measured document: about 4,100 tiles and 5.5 GB.
        long measured = DocumentLimits.EstimatedBytes(4_100);
        Assert.True(Math.Abs((double)measured / Gb - 5.5) < 0.4, $"{measured}");
        Assert.Null(DocumentLimits.ComfortNotice(4_100, memory: mac16));   // 5.5 GB of 16 is comfortable
        // RAW files kept in the document count too: the same document with 5 GB of them is no longer comfortable.
        Assert.NotNull(DocumentLimits.ComfortNotice(4_100, keptBytes: 5 * Gb, memory: mac16));
        var withRaw = new DocumentState(8, 8, layers: [new Layer("a", new RasterContent(TileGrid.Empty)), new Layer("b", new RasterContent(TileGrid.Empty))]);
        withRaw = withRaw.UpdateLayer(withRaw.Layers[1].Id, l => l with { Raw = new RawSource(new byte[1_000], "x.dng", new RawSettings()) });
        Assert.Equal(1_000, withRaw.KeptRawBytes);
        var notice = DocumentLimits.ComfortNotice(8_000, memory: mac16) ?? "";
        Assert.True(notice.Contains("16 GB") && notice.Contains("Merging layers") && notice.Contains("Image Size"), notice);
        Assert.NotNull(DocumentLimits.ComfortNotice(4_100, memory: 8 * Gb));   // the same document is too much for 8 GB
    }
}

public class UndoSpillStoreTests
{
    private static string[] BlobNames(string dir)
    {
        var blobs = Path.Combine(dir, "blobs");
        return Directory.Exists(blobs) ? [.. Directory.GetFiles(blobs).Select(Path.GetFileName).Order()!] : [];
    }

    [Fact]
    public void StepsThatShareALayerShareItsBytesAndABlobGoesWithItsLastStep()
    {
        var dir = TempFolder("spill-test");
        try
        {
            var shared = NoiseLayer("Shared", 1);
            var a = NoiseLayer("A", 2);
            var b = NoiseLayer("B", 3);
            var first = new UndoSnapshot(new DocumentState(256, 256, layers: [shared, a], activeLayerID: a.Id));
            var second = new UndoSnapshot(new DocumentState(256, 256, layers: [shared, b], activeLayerID: b.Id));
            first.MoveToDisk(dir);
            second.MoveToDisk(dir);
            // Three different layers in two documents of two layers each: three blobs, not four.
            var blobs = BlobNames(dir);
            Assert.True(blobs.Length == 3, string.Join(", ", blobs));
            var counts = blobs.Select(n => UndoSpillStore.LinkCount(Path.Combine(dir, "blobs", n))).Order().ToArray();
            Assert.True(counts.SequenceEqual([2, 2, 3]), $"the shared layer has three names: the store's and one per step; {string.Join(",", counts)}");
            // Both read back whole.
            Assert.Equal(["Shared", "A"], first.State().Layers.Select(l => l.Name));
            Assert.Equal(["Shared", "B"], second.State().Layers.Select(l => l.Name));
            // The first step goes: its own layer's blob goes, the shared one stays for the second.
            first.Dispose();
            Assert.Equal(2, BlobNames(dir).Length);
            Assert.Equal(["Shared", "B"], second.State().Layers.Select(l => l.Name));   // the remaining step is untouched
            second.Dispose();
            Assert.Empty(BlobNames(dir));   // a blob is removed when its last step is
            var left = Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Where(n => n != "blobs").ToList();
            Assert.True(left.Count == 0, string.Join(", ", left));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void WhatAnInterruptedWriteLeftIsNeverReadAndIsSweptAway()
    {
        var dir = TempFolder("spill-test");
        try
        {
            // A good step.
            var layer = NoiseLayer("Kept", 4);
            using var good = new UndoSnapshot(new DocumentState(256, 256, layers: [layer], activeLayerID: layer.Id));
            good.MoveToDisk(dir);
            // What a crash can leave: a blob written but not linked, a blob still under its temporary name, and a package
            // that never got its final name.
            var blobs = Path.Combine(dir, "blobs");
            File.WriteAllBytes(Path.Combine(blobs, string.Concat(Enumerable.Repeat("ab", 32))), Enumerable.Repeat((byte)7, 40_000).ToArray());
            File.WriteAllBytes(Path.Combine(blobs, string.Concat(Enumerable.Repeat("cd", 32)) + ".tmp-X"), Enumerable.Repeat((byte)8, 40_000).ToArray());
            var partial = Path.Combine(dir, "DEAD.glimmer.partial");
            Directory.CreateDirectory(partial);
            File.WriteAllText(Path.Combine(partial, "manifest.json"), "{}");
            // Nothing complete carries the unfinished package's name, so nothing reads it.
            Assert.False(Directory.Exists(Path.Combine(dir, "DEAD.glimmer")));
            UndoSpillStore.Sweep(dir);
            Assert.False(Directory.Exists(partial));
            Assert.True(BlobNames(dir).Length == 1, $"only the good step's blob is left: {string.Join(", ", BlobNames(dir))}");
            Assert.Equal("Kept", good.State().Layers.First().Name);   // the good step still reads back
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void LeftoverFoldersOfDeadProcessesAreRemovedAtLaunch()
    {
        var temp = TempFolder("spill-test");
        try
        {
            var mine = Path.Combine(temp, UndoSpillStore.FolderName());
            var dead = Path.Combine(temp, UndoSpillStore.FolderName(99_999_990, 1_700_000_000));
            var deadEarlierForm = Path.Combine(temp, "undo-99999991-" + SwiftJson.UuidString(Guid.NewGuid()));
            // The earliest naming, a bare UUID. These two begin with eight digits, which an earlier version read as a
            // process id: the fresh one was removed about once in forty runs.
            var old = Path.Combine(temp, "undo-12345678-" + SwiftJson.UuidString(Guid.NewGuid())[9..]);      // a week old
            var recent = Path.Combine(temp, "undo-87654321-" + SwiftJson.UuidString(Guid.NewGuid())[9..]);   // just made
            var other = Path.Combine(temp, "keep-me");
            var notOurs = Path.Combine(temp, "undo-notes");
            foreach (var path in new[] { mine, dead, deadEarlierForm, old, recent, other, notOurs }) Directory.CreateDirectory(path);
            Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-7));
            Assert.True(UndoSpillStore.IsRunning(Environment.ProcessId));
            Assert.NotNull(UndoSpillStore.StartTime(Environment.ProcessId));
            Assert.True(Path.GetFileName(mine).Contains('.'), $"this process's folder carries its start time: {Path.GetFileName(mine)}");
            int removed = UndoSpillStore.RemoveOrphans(temp);
            Assert.Equal(3, removed);
            Assert.True(Directory.Exists(mine) && Directory.Exists(recent) && Directory.Exists(other) && Directory.Exists(notOurs));
            Assert.True(!Directory.Exists(dead) && !Directory.Exists(deadEarlierForm) && !Directory.Exists(old));
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// A process id is handed on: the folder of a finished process whose id a new process now has is removed, and the
    /// folder of the process that really made it never is.
    /// </summary>
    [Fact]
    public void AReusedProcessIdDoesNotKeepADeadFolderAndNeverCostsALiveOne()
    {
        var temp = TempFolder("spill-test");
        try
        {
            var deadButIdReused = Path.Combine(temp, UndoSpillStore.FolderName(4242, 1_000));
            var alive = Path.Combine(temp, UndoSpillStore.FolderName(4243, 2_000));
            var aliveStartUnknown = Path.Combine(temp, UndoSpillStore.FolderName(4244, 3_000));
            var aliveEarlierForm = Path.Combine(temp, "undo-4245-" + SwiftJson.UuidString(Guid.NewGuid()));
            foreach (var path in new[] { deadButIdReused, alive, aliveStartUnknown, aliveEarlierForm }) Directory.CreateDirectory(path);
            // Every id is in use. 4242 now belongs to a process that started later; 4244's start can't be read.
            var starts = new Dictionary<int, long> { [4242] = 9_999, [4243] = 2_000, [4245] = 5_000 };
            int removed = UndoSpillStore.RemoveOrphans(temp, _ => true, pid => starts.TryGetValue(pid, out var s) ? s : null);
            Assert.True(removed == 1 && !Directory.Exists(deadButIdReused));
            Assert.True(Directory.Exists(alive), "the same id and the same start: its maker is running");
            Assert.True(Directory.Exists(aliveStartUnknown), "when the start can't be read, the folder is kept");
            Assert.True(Directory.Exists(aliveEarlierForm), "a folder without a start time is kept while its id is in use");
        }
        finally
        {
            TryDelete(temp);
        }
    }

    [Fact]
    public void AStoreOnAnotherVolumeIsRefusedCleanly()
    {
        // Hard links can't cross volumes. This needs two volumes: the temporary folder and the repository's (they differ
        // under WSL, /tmp against /mnt/c; where they don't, there is nothing to test).
        var here = Path.GetDirectoryName(SourceFile())!;
        var store = TempFolder("spill-test");
        try
        {
            var a = FileSystemLinks.VolumeId(store);
            var b = FileSystemLinks.VolumeId(here);
            if (a is null || b is null || a == b) return;
            var elsewhere = Path.Combine(here, ".spill-volume-test-" + Guid.NewGuid());
            Directory.CreateDirectory(elsewhere);
            try
            {
                var layer = NoiseLayer("L", 5);
                var contents = GlimmerPackage.Contents(new DocumentState(256, 256, layers: [layer], activeLayerID: layer.Id));
                var e = Assert.Throws<UndoSpillStore.SpillException>(() => UndoSpillStore.Write(contents, Path.Combine(elsewhere, "step.glimmer"), store));
                Assert.Equal(UndoSpillStore.SpillError.DifferentVolume, e.Error);
                Assert.Empty(Directory.GetFileSystemEntries(elsewhere));   // nothing is left where the step would have been
                Assert.Empty(BlobNames(store));                             // and no blob stays without a step
            }
            finally
            {
                TryDelete(elsewhere);
            }
        }
        finally
        {
            TryDelete(store);
        }
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;
}

public class UndoSnapshotTests
{
    [Fact]
    public void AFullDiskMakesTheHistoryShorterNotTheDiskFuller()
    {
        const long gb = 1_073_741_824;
        // Room for the snapshot and the reserve: spill. Not enough: don't.
        Assert.True(UndoSpillPolicy.CanSpill(2 * gb, 4 * gb));
        Assert.False(UndoSpillPolicy.CanSpill(2 * gb, 2 * gb + gb / 2));   // the reserve stays free
        Assert.False(UndoSpillPolicy.CanSpill(gb, gb / 2));
        Assert.True(UndoSpillPolicy.CanSpill(gb, null));   // unknown free space: try, and handle the failure
        // Oldest first, until the rest fits; steps on disk (cost 0) don't help; the newest stays.
        Assert.Equal(2, UndoSpillPolicy.StepsToForget([500, 500, 500, 500], 1000));
        Assert.Equal(3, UndoSpillPolicy.StepsToForget([0, 0, 900, 900], 1000));
        Assert.Equal(0, UndoSpillPolicy.StepsToForget([100, 100], 1000));
        Assert.Equal(0, UndoSpillPolicy.StepsToForget([5000], 1000));   // the newest step is always kept
        Assert.Equal(0, UndoSpillPolicy.StepsToForget([], 0));
        Assert.True((UndoSpillPolicy.FreeBytes(Path.Combine(Path.GetTempPath(), "not-there", "yet")) ?? 0) > 0);
    }

    /// <summary>The document forgets steps by lowering the undo manager's limit: pin that it drops the oldest at once.</summary>
    [Fact]
    public void LoweringTheUndoLimitDropsTheOldestSteps()
    {
        var target = new StrongBox<int>(0);
        var undo = new UndoManager { GroupsByEvent = false };
        for (int i = 1; i <= 5; i++)
        {
            undo.BeginUndoGrouping();
            int old = target.Value;
            undo.RegisterUndo(target, t => t.Value = old);
            target.Value = i;
            undo.EndUndoGrouping();
        }
        undo.LevelsOfUndo = 2;
        int undone = 0;
        while (undo.CanUndo)
        {
            undo.Undo();
            undone++;
        }
        Assert.True(undone == 2 && target.Value == 3, $"only the two newest steps are left: {undone} undone, value {target.Value}");
    }

    [Fact]
    public void SavingWritesTheWholePackageAndReplacesAnEarlierOne()
    {
        var dir = TempFolder("save-test");
        try
        {
            var path = Path.Combine(dir, "doc.glimmer");
            var state = DocumentState.New(300, 200, DocumentState.Background.White);
            DocumentSaver.Write(GlimmerPackage.Contents(state), path);
            Assert.Equal(300, GlimmerPackage.Load(path).Width);
            state = state with { Width = 400 };
            DocumentSaver.Write(GlimmerPackage.Contents(state), path);
            Assert.Equal(400, GlimmerPackage.Load(path).Width);   // an earlier version is replaced
            Assert.True(DocumentSaver.ByteCount(GlimmerPackage.Contents(state)) > 100);
            Assert.Single(Directory.GetFileSystemEntries(dir));   // no temporary file is left beside it
            var message = new DocumentSaveException(50_000_000, 3_000_000, "Photos").Message;
            Assert.True(message.Contains("“Photos”") && message.Contains("50") && message.Contains("3"), message);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>Run with GLIMMER_FULL_DISK pointing at a small, nearly full volume (as scripts/test-disk-full.sh does on the Mac).</summary>
    [Fact]
    public void SavingToAFullDiskSaysSoAndLeavesNoHalfDocument()
    {
        var root = Environment.GetEnvironmentVariable("GLIMMER_FULL_DISK");
        if (string.IsNullOrEmpty(root)) return;
        // An earlier, small version saved while there was still room.
        var path = Path.Combine(root, "doc.glimmer");
        var small = DocumentState.New(300, 200, DocumentState.Background.White);
        DocumentSaver.Write(GlimmerPackage.Contents(small), path);
        // A layer that can't fit (random pixels don't compress).
        var layer = NoiseLayer("Noise", 5, tiles: 40, columns: 8);
        var big = new DocumentState(2048, 1280, layers: [layer], activeLayerID: layer.Id);
        var contents = GlimmerPackage.Contents(big);
        // Over the earlier version: refused before writing, and the earlier version is intact.
        Assert.Throws<DocumentSaveException>(() => DocumentSaver.Write(contents, path));
        Assert.Equal(300, GlimmerPackage.Load(path).Width);
        // As a new document: refused, and nothing is left where it would have been.
        var fresh = Path.Combine(root, "new.glimmer");
        Assert.Throws<DocumentSaveException>(() => DocumentSaver.Write(contents, fresh));
        Assert.False(File.Exists(fresh));
        // An empty folder made beforehand must not survive the failure.
        var made = Path.Combine(root, "made.glimmer");
        Directory.CreateDirectory(made);
        Assert.Throws<DocumentSaveException>(() => DocumentSaver.Write(contents, made));
        Assert.False(Directory.Exists(made));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(root), n => Path.GetFileName(n).Contains("new"));
    }

    /// <summary>Run with GLIMMER_FULL_DISK pointing at a small, nearly full volume.</summary>
    [Fact]
    public void ASnapshotThatDoesntFitLeavesNothingBehindAndStaysInMemory()
    {
        var root = Environment.GetEnvironmentVariable("GLIMMER_FULL_DISK");
        if (string.IsNullOrEmpty(root)) return;
        var dir = Path.Combine(root, "undo");
        // About 40 MB of pixels that don't compress.
        var layer = NoiseLayer("Noise", 3, tiles: 80, columns: 10);
        var state = new DocumentState(2560, 2048, layers: [layer], activeLayerID: layer.Id);
        using var snapshot = new UndoSnapshot(state);
        var free = UndoSpillPolicy.FreeBytes(dir);
        Assert.NotNull(free);
        Assert.True(free < snapshot.DiskBytesUpperBound, $"the test disk must be too small: {free} free");
        Assert.False(UndoSpillPolicy.CanSpill(snapshot.DiskBytesUpperBound, free));
        // Forced anyway (as when the disk fills while writing): it fails cleanly.
        Assert.ThrowsAny<Exception>(() => snapshot.MoveToDisk(dir));
        Assert.False(snapshot.IsOnDisk);
        var left = Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir).Where(n => n.EndsWith(".glimmer")).ToList() : [];
        Assert.True(left.Count == 0, $"nothing half-written is left: {string.Join(", ", left)}");
        Assert.Equal(state.Layers[0].Tiles.TileBounds, snapshot.State().Layers[0].Tiles.TileBounds);   // the step is still in memory
    }

    [Fact]
    public void MovesToDiskAndBack()
    {
        var px = Enumerable.Repeat((Half)0.25f, TileGeometry.FloatCount).ToArray();
        px[0] = (Half)0.75f;
        var layer = new Layer("Paint", new RasterContent(TileGrid.Empty.SetItem(new TileCoord(1, 0), Tile.FromHalf(px))));
        var state = new DocumentState(600, 300, layers: [layer], activeLayerID: layer.Id);
        state = state with { Selection = Selection.From(Enumerable.Repeat(1f, 600 * 300).ToArray(), state.Bounds) };
        var dir = Path.Combine(Path.GetTempPath(), "undo-test-" + Guid.NewGuid());
        try
        {
            var snapshot = new UndoSnapshot(state);
            Assert.False(snapshot.IsOnDisk);
            snapshot.MoveToDisk(dir);
            Assert.True(snapshot.IsOnDisk);
            var files = Directory.GetFileSystemEntries(dir).Where(n => Path.GetFileName(n) != "blobs").ToList();
            Assert.Single(files);

            var back = snapshot.State();
            Assert.True(back.Width == 600 && back.Layers.Length == 1 && back.Layers[0].Id == layer.Id);
            Assert.True(back.ActiveLayerID == layer.Id && back.Selection is not null);
            var pixels = back.Layers[0].Tiles[new TileCoord(1, 0)]!.ToHalfArray();
            Assert.True(pixels[0] == (Half)0.75f && pixels[4] == (Half)0.25f);

            // Releasing the snapshot removes its files.
            snapshot.Dispose();
            Assert.DoesNotContain(Directory.GetFileSystemEntries(dir), n => Path.GetFileName(n) != "blobs");
            Assert.Empty(Directory.Exists(Path.Combine(dir, "blobs")) ? Directory.GetFiles(Path.Combine(dir, "blobs")) : []);
        }
        finally
        {
            TryDelete(dir);
        }
    }
}

public class TileCodecSpeedTests
{
    /// <summary>
    /// A full 6000 × 4000 layer of pixels that don't compress (384 tiles, 192 MB): the worst case for saving. The Mac's
    /// test prints its times and asserts none; so does this one.
    /// </summary>
    [Fact]
    public void EncodingAndDecodingABigLayer()
    {
        uint seed = 11;
        var b = new TileGrid.Builder();
        for (int i = 0; i < 384; i++) b[new TileCoord(i % 24, i / 24)] = NoiseTile(ref seed, stride: 7);
        var grid = b.ToImmutable();
        var clock = Stopwatch.StartNew();
        var data = TileCodec.Encode(grid);
        var encode = clock.Elapsed.TotalSeconds;
        clock.Restart();
        var back = TileCodec.Decode(data);
        var decode = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"TileCodec 384 tiles: encode {encode:F2} s, decode {decode:F2} s, {data.Length / 1_048_576} MB");
        Assert.Equal(384, back.Count);
        Assert.True(back[new TileCoord(5, 7)]!.ToHalfArray().AsSpan().SequenceEqual(grid[new TileCoord(5, 7)]!.ToHalfArray()), "the pixels come back exactly");
        Assert.True(TileCodec.Encode(back).AsSpan().SequenceEqual(data), "encoding is deterministic");
    }

    /// <summary>Four layers of 20 tiles of fully random pixels: where does saving and opening spend its time?</summary>
    [Fact]
    public void SavingAndOpeningAPackage()
    {
        uint seed = 17;
        var layers = new List<Layer>();
        for (int l = 0; l < 4; l++)
        {
            var b = new TileGrid.Builder();
            for (int i = 0; i < 20; i++) b[new TileCoord(i % 8, i / 8)] = NoiseTile(ref seed);
            layers.Add(new Layer($"L{l}", new RasterContent(b.ToImmutable())));
        }
        var state = new DocumentState(2048, 1280, layers: layers, activeLayerID: layers[0].Id);
        var dir = TempFolder("speed");
        try
        {
            var path = Path.Combine(dir, "doc.glimmer");
            var clock = Stopwatch.StartNew();
            var contents = GlimmerPackage.Contents(state);
            var encode = clock.Elapsed.TotalSeconds;
            clock.Restart();
            DocumentSaver.Write(contents, path);
            var write = clock.Elapsed.TotalSeconds;
            clock.Restart();
            var read = PackageContents.Read(path);
            var load = clock.Elapsed.TotalSeconds;
            clock.Restart();
            var back = GlimmerPackage.State(read);
            var decode = clock.Elapsed.TotalSeconds;
            Console.WriteLine($"Package 80 tiles, {read.ByteCount / 1_048_576} MB: encode {encode:F2} s, write {write:F2} s, read file {load:F2} s, decode {decode:F2} s");
            Assert.Equal(4, back.Layers.Length);
        }
        finally
        {
            TryDelete(dir);
        }
    }
}

/// <summary>A linked edit keeps its link in the document, so a restored window is linked again.</summary>
public class DocumentLinkTests
{
    [Fact]
    public void TheLinkTravelsWithTheDocument()
    {
        var state = new DocumentState(64, 48) { Link = new DocumentLink([1, 2, 3], "/tmp/photo.png", "Flimmer") };
        var back = GlimmerPackage.State(GlimmerPackage.Contents(state));
        Assert.Equal(state.Link, back.Link);
        Assert.Equal("photo.png", back.Link?.FileName);
    }

    [Fact]
    public void AnOrdinaryDocumentHasNoLinkInItsManifest()
    {
        var contents = GlimmerPackage.Contents(new DocumentState(64, 48));
        var manifest = contents[GlimmerPackage.ManifestFilename];
        Assert.NotNull(manifest);
        Assert.DoesNotContain("\"link\"", Encoding.UTF8.GetString(manifest!.Value.Span));
        Assert.Null(GlimmerPackage.State(contents).Link);
    }

    /// <summary>A build from before the link reads a linked document as an ordinary one: the key is optional and unknown keys are skipped.</summary>
    [Fact]
    public void AManifestWithoutTheKeyStillReads()
    {
        var state = new DocumentState(64, 48) { Link = new DocumentLink(null, "/tmp/a.jpg", "Jämmer") };
        var data = GlimmerPackage.Contents(state)[GlimmerPackage.ManifestFilename]!.Value;
        var json = JsonNode.Parse(data.Span)!.AsObject();
        Assert.NotNull(json["link"]);
        json.Remove("link");
        var manifest = GlimmerPackage.DecodeManifest(Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Null(manifest.Link);
    }

    [Fact]
    public void AWindowsPathGivesItsFileName() =>
        Assert.Equal("photo.png", new DocumentLink(null, @"C:\Users\x\photo.png", "Flimmer").FileName);
}
