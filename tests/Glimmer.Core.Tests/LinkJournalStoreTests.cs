// Ported from Packages/GlimmerKit/Tests/GlimmerCoreTests/LinkJournalStoreTests.swift

using System.Numerics;
using static Glimmer.Core.Tests.TestTiles;

namespace Glimmer.Core.Tests;

/// <summary>
/// The link journal's writer (SIBLINGS.md §6.6): an entry on disk is always a whole one, a step writes only what it
/// changed, and a step that does not fit touches nothing.
/// </summary>
public class LinkJournalStoreTests
{
    private static string EntryFolder(string? root = null) =>
        Path.Combine(root ?? Path.GetTempPath(), "journal-test-" + Guid.NewGuid());

    private static LinkJournalRecord Record(int waiting, string id = "E1") =>
        new(id, new DocumentLink(null, "/tmp/photo.png", "Flimmer"), "photo.png", waiting > 0, waiting,
            linkedAt: DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), writtenAt: DateTimeOffset.UtcNow);

    /// <summary>The entry as a launch would read it.</summary>
    private static (DocumentState State, LinkJournalRecord Record, Dictionary<string, byte[]> Files) OnDisk(string folder)
    {
        LinkJournalWriter.Recover(folder);
        var package = Path.Combine(folder, LinkJournalWriter.WorkCopyName);
        var state = GlimmerPackage.State(PackageContents.ReadDirectory(package));
        var record = LinkJournalRecord.Decode(File.ReadAllBytes(Path.Combine(folder, LinkJournalWriter.RecordName)));
        var files = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(package, f), File.ReadAllBytes);
        return (state, record, files);
    }

    private static bool SameFiles(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var o) && o.AsSpan().SequenceEqual(p.Value));

    private static (DocumentState Before, DocumentState After) TwoStates()
    {
        Layer a = NoiseLayer("A", 1, tiles: 4), b = NoiseLayer("B", 2, tiles: 4), c = NoiseLayer("C", 3, tiles: 4);
        var before = new DocumentState(1024, 256, layers: [a, b, c], activeLayerID: c.Id);
        var d = NoiseLayer("D", 4, tiles: 1);
        var after = new DocumentState(1024, 256, layers: [a, b, c, d], activeLayerID: d.Id);
        return (before, after);
    }

    [Fact]
    public async Task AStepWritesOnlyWhatItChangedAndTheRestAreTheSameFilesOnDisk()
    {
        var folder = EntryFolder();
        try
        {
            var (before, after) = TwoStates();
            var writer = new LinkJournalWriter(folder);
            var first = await writer.WriteAsync(Record(0), before);
            var old = OnDisk(folder);
            Assert.True(old.State.Layers.Length == 3 && !old.Record.Unreturned);
            Dictionary<string, (ulong, ulong)?> FileNumbers() =>
                old.Files.Keys.ToDictionary(name => name, name => FileSystemLinks.FileId(Path.Combine(folder, LinkJournalWriter.WorkCopyName, name)));
            var numbersBefore = FileNumbers();
            var second = await writer.WriteAsync(Record(1), after);
            var now = OnDisk(folder);
            Assert.True(now.State.Layers.Length == 4 && now.Record.Unreturned && now.Record.UnreturnedSteps == 1);
            Assert.True(second.BytesWritten * 3 < first.BytesWritten, $"the step wrote {second.BytesWritten} bytes, the whole entry was {first.BytesWritten}");
            // The three layers the step left alone are the very same files (hard links), not copies.
            var numbersAfter = FileNumbers();
            int same = numbersBefore.Count(p => p.Value is not null && numbersAfter[p.Key] == p.Value);
            Assert.True(same >= 3, $"{same} of {numbersBefore.Count} files are the same files on disk as before the step");
            Assert.False(Directory.Exists(Path.Combine(folder, LinkJournalWriter.StagingName)));
            // A writer that starts again (a relaunch) reads what is there, and does not write every layer again.
            var again = new LinkJournalWriter(folder);
            var third = await again.WriteAsync(Record(1), after);
            Assert.True(third.BytesWritten < 4096, $"a write with nothing changed after a relaunch wrote {third.BytesWritten} bytes");
        }
        finally
        {
            TryDelete(folder);
        }
    }

    public static TheoryData<int> StopPoints => new() { 0, 1 };

    /// <summary>
    /// A stop between two files of a step, or with the new copy complete but not yet in place: what a launch reads is
    /// the state before the step, whole, byte for byte.
    /// </summary>
    [Theory]
    [MemberData(nameof(StopPoints))]
    public async Task AStopBeforeTheNewCopyIsInPlaceLeavesTheOldStateWhole(int which)
    {
        var point = which == 0 ? LinkJournalWriter.StopPoint.AfterFiles(1) : LinkJournalWriter.StopPoint.BeforeSwap;
        var folder = EntryFolder();
        try
        {
            var (before, after) = TwoStates();
            var writer = new LinkJournalWriter(folder);
            await writer.WriteAsync(Record(0), before);
            var old = OnDisk(folder);
            writer.StopNextWrite(point);
            await Assert.ThrowsAsync<LinkJournalWriter.StoppedException>(() => writer.WriteAsync(Record(1), after));
            var found = OnDisk(folder);
            Assert.True(found.State.Layers.Length == 3 && found.State.Layers.Select(l => l.Name).SequenceEqual(["A", "B", "C"]));
            Assert.True(SameFiles(found.Files, old.Files), "every file of the work copy is as it was before the step");
            // The record was written first: the entry says something waits, so the launch opens it rather than dropping it.
            Assert.True(found.Record.Unreturned && found.Record.UnreturnedSteps == 1);
            // The next write (after a relaunch: a new writer) clears away what the stop left and completes.
            var next = new LinkJournalWriter(folder);
            await next.WriteAsync(Record(1), after);
            Assert.Equal(4, OnDisk(folder).State.Layers.Length);
            Assert.False(Directory.Exists(Path.Combine(folder, LinkJournalWriter.StagingName)));
        }
        finally
        {
            TryDelete(folder);
        }
    }

    /// <summary>A stop with the new copy in place, before the record is written again: the state after the step, whole.</summary>
    [Fact]
    public async Task AStopAfterTheNewCopyIsInPlaceLeavesTheNewStateWhole()
    {
        var folder = EntryFolder();
        try
        {
            var (before, after) = TwoStates();
            var writer = new LinkJournalWriter(folder);
            await writer.WriteAsync(Record(0), before);
            writer.StopNextWrite(LinkJournalWriter.StopPoint.BeforeRecord);
            await Assert.ThrowsAsync<LinkJournalWriter.StoppedException>(() => writer.WriteAsync(Record(1), after));
            var found = OnDisk(folder);
            Assert.Equal(["A", "B", "C", "D"], found.State.Layers.Select(l => l.Name));
            Assert.True(found.Record.Unreturned && found.Record.UnreturnedSteps == 1);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    /// <summary>
    /// Windows can't swap two folders in one step: a stop between moving the old copy aside and putting the new one in
    /// place leaves the old copy under another name, and reading the entry puts it back (not in the Mac's tests).
    /// </summary>
    [Fact]
    public async Task AStopBetweenTheTwoRenamesIsRecovered()
    {
        var folder = EntryFolder();
        try
        {
            var (before, _) = TwoStates();
            await new LinkJournalWriter(folder).WriteAsync(Record(0), before);
            var old = OnDisk(folder);
            Directory.Move(Path.Combine(folder, LinkJournalWriter.WorkCopyName), Path.Combine(folder, LinkJournalWriter.PreviousName));
            var found = OnDisk(folder);
            Assert.True(SameFiles(found.Files, old.Files));
            Assert.False(Directory.Exists(Path.Combine(folder, LinkJournalWriter.PreviousName)));
        }
        finally
        {
            TryDelete(folder);
        }
    }

    [Fact]
    public async Task TheSizeOfAnEntryCountsEachFileOnce()
    {
        var folder = EntryFolder();
        try
        {
            var (before, _) = TwoStates();
            var outcome = await new LinkJournalWriter(folder).WriteAsync(Record(0), before);
            long size = LinkJournalWriter.Bytes(folder);
            Assert.True(Math.Abs(size - outcome.Bytes) < 4096, $"{size} on disk, {outcome.Bytes} reported");
        }
        finally
        {
            TryDelete(folder);
        }
    }

    /// <summary>
    /// Run with GLIMMER_FULL_DISK pointing at a small, nearly full volume: a step that does not fit is refused before
    /// anything is written, says how much it needed, and the entry kept before is whole.
    /// </summary>
    [Fact]
    public async Task AStepThatDoesNotFitTouchesNothingAndSaysSo()
    {
        var root = Environment.GetEnvironmentVariable("GLIMMER_FULL_DISK");
        if (string.IsNullOrEmpty(root)) return;
        var folder = EntryFolder(root);
        try
        {
            var small = DocumentState.New(300, 200, DocumentState.Background.White);
            var writer = new LinkJournalWriter(folder);
            await writer.WriteAsync(Record(0), small);
            var old = OnDisk(folder);
            var big = small with { Layers = small.Layers.Add(NoiseLayer("Too much", 9, tiles: 40)) };
            var failure = await Assert.ThrowsAsync<LinkJournalWriter.NoRoomException>(() => writer.WriteAsync(Record(1), big));
            Assert.True(failure.Needed > failure.Free && failure.Needed > 5_000_000, $"needed {failure.Needed}, free {failure.Free}");
            Assert.Contains("isn't enough room", failure.Message);
            var found = OnDisk(folder);
            Assert.True(SameFiles(found.Files, old.Files) && found.State.Layers.Length == old.State.Layers.Length, "the entry kept before is whole");
            Assert.False(found.Record.Unreturned, "nothing of the entry was touched, the record included");
            Assert.False(Directory.Exists(Path.Combine(folder, LinkJournalWriter.StagingName)), "nothing half-built is left");
            // A small step still fits.
            var little = small with { Layers = small.Layers.Add(new Layer("Note", new RasterContent(TileGrid.Empty))) };
            await writer.WriteAsync(Record(1), little);
            Assert.Equal(old.State.Layers.Length + 1, OnDisk(folder).State.Layers.Length);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    [Fact]
    public void TheRecordReadsAsTheMacWritesIt()
    {
        const string mac = """{"fileName":"photo.png","id":"E1","keptKnowingly":true,"link":{"path":"\/tmp\/photo.png","requester":"Flimmer"},"linkedAt":"2027-01-15T08:00:00Z","unreturned":true,"unreturnedSteps":2,"writtenAt":"2027-01-15T09:30:00Z"}""";
        var r = LinkJournalRecord.Decode(System.Text.Encoding.UTF8.GetBytes(mac));
        Assert.True(r.Unreturned && r.UnreturnedSteps == 2 && r.KeptKnowingly == true && r.Link.Path == "/tmp/photo.png");
        Assert.Equal(new DateTimeOffset(2027, 1, 15, 8, 0, 0, TimeSpan.Zero), r.LinkedAt);
        var written = System.Text.Encoding.UTF8.GetString(r.Encode());
        Assert.Equal("""{"fileName":"photo.png","id":"E1","keptKnowingly":true,"link":{"path":"/tmp/photo.png","requester":"Flimmer"},"linkedAt":"2027-01-15T08:00:00Z","unreturned":true,"unreturnedSteps":2,"writtenAt":"2027-01-15T09:30:00Z"}""", written);
    }
}

/// <summary>The tight bounds of a layer's content, tile by tile.</summary>
public class TileBoundsTests
{
    /// <summary>A tile that is transparent except for one opaque rectangle.</summary>
    private static Tile OpaqueTile(int rx, int ry, int rw, int rh)
    {
        const int s = TileGeometry.Size;
        var px = new Half[s * s * 4];
        for (int y = ry; y < ry + rh; y++)
            for (int x = rx; x < rx + rw; x++)
            {
                int i = (y * s + x) * 4;
                px[i] = (Half)0.5f;
                px[i + 3] = (Half)1;
            }
        return Tile.Make(px)!;
    }

    [Fact]
    public void TheBoundsAreTightAndOnlyChangedTilesAreReadAgain()
    {
        const int s = TileGeometry.Size;
        var b = new TileGrid.Builder();
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++) b[new TileCoord(x, y)] = GradientTile((x + y) * 0.01f);
        b[new TileCoord(0, 0)] = OpaqueTile(10, 20, 30, 40);        // the top-left corner starts at (10, 20)
        b[new TileCoord(3, 3)] = OpaqueTile(0, 0, 100, 50);         // the bottom-right ends at (3s + 100, 3s + 50)
        b[new TileCoord(5, 1)] = Tile.Solid(Vector4.Zero);          // a transparent solid tile outside counts for nothing
        var grid = b.ToImmutable();
        var cache = new TileBoundsCache();
        var all = grid.ContentBounds(cache);
        // Row 0 and column 0 have opaque gradient tiles, so the bounds start at 0, 0; they end where the gradient tiles end.
        Assert.Equal(new PixelRect(0, 0, 4 * s, 4 * s), all);
        int readFirst = cache.TilesRead;
        Assert.Equal(16, readFirst);
        // The same grid again: nothing is read.
        _ = grid.ContentBounds(cache);
        Assert.Equal(readFirst, cache.TilesRead);
        // One tile changed (a step): one tile is read.
        var changed = grid.SetItem(new TileCoord(2, 2), GradientTile(0.3f));
        _ = changed.ContentBounds(cache);
        Assert.Equal(readFirst + 1, cache.TilesRead);
        // Tight, when the edges are partly transparent.
        var sparse = TileGrid.Empty.SetItem(new TileCoord(0, 0), OpaqueTile(10, 20, 30, 40)).SetItem(new TileCoord(1, 1), OpaqueTile(0, 0, 100, 50));
        Assert.Equal(new PixelRect(10, 20, s + 100 - 10, s + 50 - 20), sparse.ContentBounds(cache));
        Assert.Null(TileGrid.Empty.SetItem(new TileCoord(0, 0), Tile.Solid(Vector4.Zero)).ContentBounds(cache));
    }
}
