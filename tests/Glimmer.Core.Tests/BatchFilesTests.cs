// Ported from Packages/GlimmerKit/Tests/GlimmerCoreTests/BatchFilesTests.swift

using static Glimmer.Core.Tests.TestTiles;

namespace Glimmer.Core.Tests;

public class BatchFilesTests
{
    /// <summary>A folder with images (one in a subfolder), a text file and a hidden image; removed afterwards.</summary>
    private static string Folder()
    {
        var dir = TempFolder("batch-files");
        Directory.CreateDirectory(Path.Combine(dir, "inner"));
        foreach (var name in new[] { "b10.jpg", "b2.png", "a.heic", "notes.txt", ".hidden.jpg", "inner/deep.jpg" })
            File.WriteAllBytes(Path.Combine(dir, name), [0]);
        return dir;
    }

    [Fact]
    public void AFolderGivesTheImagesDirectlyInsideItSortedByName()
    {
        var dir = Folder();
        try
        {
            Assert.Equal(["a.heic", "b2.png", "b10.jpg"], BatchFiles.Images([dir]).Select(Path.GetFileName));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void FilesKeepTheirOrderAndOnlyImagesCount()
    {
        var dir = Folder();
        try
        {
            string[] picked = [Path.Combine(dir, "b2.png"), Path.Combine(dir, "notes.txt"), Path.Combine(dir, "a.heic")];
            Assert.Equal(["b2.png", "a.heic"], BatchFiles.Images(picked).Select(Path.GetFileName));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void MergeAddsEachFileOnceAfterWhatIsThere()
    {
        string a = "/x/a.jpg", b = "/x/b.jpg", c = "/x/c.jpg";
        Assert.Equal([a, b, c], BatchFiles.Merge([a, b], [b, c, c, a]));
    }

    [Fact]
    public void MergeOfTwoLargeListsKeepsEveryFileOnce()
    {
        // A hundred thousand files added to a hundred thousand, half of them already there.
        var first = Enumerable.Range(0, 100_000).Select(i => $"/x/{i}.jpg").ToList();
        var second = Enumerable.Range(50_000, 100_000).Select(i => $"/x/{i}.jpg");
        var merged = BatchFiles.Merge(first, second);
        Assert.Equal(150_000, merged.Count);
        Assert.Equal("/x/149999.jpg", merged[^1]);
    }

    /// <summary>A .glimmer document counts, as a ZIP file (Windows) or as a package folder (from a Mac). Not in the Mac's tests.</summary>
    [Fact]
    public void GlimmerDocumentsCountAsFilesAndAsFolders()
    {
        var dir = Folder();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "c.glimmer"), [0]);
            Directory.CreateDirectory(Path.Combine(dir, "d.glimmer"));
            Assert.Equal(["a.heic", "b2.png", "b10.jpg", "c.glimmer", "d.glimmer"], BatchFiles.Images([dir]).Select(Path.GetFileName));
            Assert.Single(BatchFiles.Images([Path.Combine(dir, "d.glimmer")]));
        }
        finally
        {
            TryDelete(dir);
        }
    }
}
