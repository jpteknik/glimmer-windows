// Not a port: the three ways a .glimmer document reaches Windows (a ZIP made here, a package folder from a Mac, and a
// ZIP that macOS Finder's Compress made of a package), and the atomic save.

using System.IO.Compression;
using System.Text;
using static Glimmer.Core.Tests.TestTiles;

namespace Glimmer.Core.Tests;

public class PackageContainerTests
{
    [Fact]
    public void AZipMadeHereHasThePackageAtItsRootWithTilesStored()
    {
        var contents = GlimmerPackage.Contents(SampleState(), quickLook: ([1, 2], [3, 4]));
        var zip = contents.ToZip();
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains(GlimmerPackage.ManifestFilename, names);
        Assert.Contains("layers/", names);
        Assert.Contains("QuickLook/Thumbnail.png", names);
        Assert.Contains("QuickLook/Preview.png", names);
        var tiles = archive.Entries.First(e => e.FullName.EndsWith(".tiles", StringComparison.Ordinal));
        Assert.Equal(tiles.Length, tiles.CompressedLength);   // already LZFSE: stored as it is
        var back = PackageContents.ReadZip(zip);
        Assert.Equal(contents.Files.Keys, back.Files.Keys);
        foreach (var (path, data) in contents.Files) Assert.True(back[path]!.Value.Span.SequenceEqual(data.Span), path);
    }

    [Fact]
    public void APackageFolderFromAMacReads()
    {
        var dir = TempFolder("mac-package");
        try
        {
            var package = Path.Combine(dir, "Photo.glimmer");
            GlimmerPackage.Contents(SampleState()).WriteDirectory(package);
            File.WriteAllBytes(Path.Combine(package, ".DS_Store"), [0, 0, 0, 1]);
            Assert.True(File.Exists(Path.Combine(package, "manifest.json")));
            var state = GlimmerPackage.Load(package);
            AssertSamePixels(SampleState().Layers[1].Tiles, state.Layers[1].Tiles);
            Assert.DoesNotContain(".DS_Store", PackageContents.Read(package).Files.Keys);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void AFinderCompressedPackageReads()
    {
        // What Finder's Compress makes of Photo.glimmer: the package in one folder, plus Finder's own files.
        var contents = GlimmerPackage.Contents(SampleState());
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("Photo.glimmer/");
            foreach (var (path, data) in contents.Files)
            {
                using var s = archive.CreateEntry("Photo.glimmer/" + path).Open();
                s.Write(data.Span);
            }
            archive.CreateEntry("__MACOSX/Photo.glimmer/._manifest.json").Open().Dispose();
            archive.CreateEntry("Photo.glimmer/.DS_Store").Open().Dispose();
        }
        var dir = TempFolder("finder-zip");
        try
        {
            var path = Path.Combine(dir, "Photo.glimmer.zip");
            File.WriteAllBytes(path, stream.ToArray());
            var state = GlimmerPackage.Load(path);
            Assert.Equal(SampleState().Layers.Select(l => l.Name), state.Layers.Select(l => l.Name));
            AssertSamePixels(SampleState().Layers[1].Tiles, state.Layers[1].Tiles);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void SavingOverAPackageFolderReplacesItWithTheFile()
    {
        var dir = TempFolder("replace-folder");
        try
        {
            var path = Path.Combine(dir, "Photo.glimmer");
            GlimmerPackage.Contents(DocumentState.New(10, 10, DocumentState.Background.White)).WriteDirectory(path);
            GlimmerPackage.Save(DocumentState.New(20, 10, DocumentState.Background.White), path);
            Assert.True(File.Exists(path) && !Directory.Exists(path));
            Assert.Equal(20, GlimmerPackage.Load(path).Width);
            Assert.Single(Directory.GetFileSystemEntries(dir));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void AZipWithoutAManifestIsMissingItsManifest()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = archive.CreateEntry("readme.txt").Open();
            s.Write(Encoding.UTF8.GetBytes("hello"));
        }
        var e = Assert.Throws<GlimmerPackageException>(() => GlimmerPackage.State(PackageContents.ReadZip(stream.ToArray())));
        Assert.Equal(GlimmerPackageError.MissingManifest, e.Error);
        Assert.Equal("The document is missing its manifest.", e.Message);
    }

    [Fact]
    public void ALayerWithoutItsPixelsIsDamaged()
    {
        var contents = GlimmerPackage.Contents(SampleState());
        var withoutTiles = new PackageContents(contents.Files.Where(f => !f.Key.EndsWith(".tiles", StringComparison.Ordinal)));
        var e = Assert.Throws<GlimmerPackageException>(() => GlimmerPackage.State(withoutTiles));
        Assert.Equal(GlimmerPackageError.CorruptLayer, e.Error);
        Assert.Contains("missing pixels for “Background”", e.Message);
    }

    [Fact]
    public void TheTileFormatIsByteForByteTheMacs()
    {
        // One solid tile at (-1, 2): header, then x, y, kind 0 and four little-endian floats.
        var grid = TileGrid.Empty.SetItem(new TileCoord(-1, 2), Tile.Solid(new System.Numerics.Vector4(1, 0.5f, 0, 1)));
        var data = TileCodec.Encode(grid);
        byte[] expected =
        [
            (byte)'G', (byte)'L', (byte)'T', (byte)'L', 1, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0,
            0xFF, 0xFF, 0xFF, 0xFF, 2, 0, 0, 0, 0,
            0, 0, 0x80, 0x3F, 0, 0, 0, 0x3F, 0, 0, 0, 0, 0, 0, 0x80, 0x3F,
        ];
        Assert.Equal(expected, data);
        // A pixel tile: kind 1, a byte count, then an LZFSE stream of the Half pixels.
        var pixel = TileCodec.Encode(TileGrid.Empty.SetItem(new TileCoord(0, 0), GradientTile()));
        Assert.Equal(1, pixel[24]);
        int length = BitConverter.ToInt32(pixel, 25);
        Assert.Equal(pixel.Length - 29, length);
        Assert.Equal("bvx", Encoding.ASCII.GetString(pixel, 29, 3));
        Assert.Equal(TileGeometry.ByteCount, Glimmer.Lzfse.Lzfse.Decode(pixel.AsSpan(29)).Length);
    }
}
