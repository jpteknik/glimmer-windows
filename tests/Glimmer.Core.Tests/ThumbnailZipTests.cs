// New, no Swift original: the File Explorer thumbnail handler's ZIP reader (src/Glimmer.Thumbnails/glimmer_zip.c), the
// native code itself. On Linux it is the freestanding build of the same C file (Native/libglimmer_zip.so); on Windows it is
// the very DLL the app installs for Explorer (src/Glimmer.App/Shell/GlimmerThumbnails-*.dll), which exports the reader.
// The reader must find a saved document's QuickLook images, refuse what it can't use, and never ask for a byte outside the
// archive, whatever the archive holds.
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static Glimmer.Core.Tests.TestTiles;

namespace Glimmer.Core.Tests;

/// <summary>A test that needs the native reader; skipped on a platform it isn't built for.</summary>
public sealed class NativeZipFactAttribute : FactAttribute
{
    public NativeZipFactAttribute()
    {
        if (ThumbnailZipTests.LibraryFile is null) Skip = "The thumbnail handler's ZIP reader is built for Linux x64 and Windows x64/ARM64 only.";
    }
}

public unsafe class ThumbnailZipTests
{
    internal static string? LibraryFile =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 when OperatingSystem.IsLinux() => "libglimmer_zip.so",
            Architecture.X64 when OperatingSystem.IsWindows() => "GlimmerThumbnails-x64.dll",
            Architecture.Arm64 when OperatingSystem.IsWindows() => "GlimmerThumbnails-arm64.dll",
            _ => null,
        };

    private static readonly Lazy<IntPtr> Function = new(() =>
        NativeLibrary.GetExport(NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Native", LibraryFile!)), "glimmer_zip_find_stored"));

    private sealed class Source(byte[] data)
    {
        public byte[] Data { get; } = data;
        public List<string> Outside { get; } = [];
        public int Reads;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadAt(IntPtr context, ulong offset, byte* buffer, uint size)
    {
        var source = (Source)GCHandle.FromIntPtr(context).Target!;
        source.Reads += 1;
        if (offset > (ulong)source.Data.Length || size > (ulong)source.Data.Length - offset)
        {
            source.Outside.Add($"{size} bytes at {offset} of {source.Data.Length}");
            return 0;
        }
        source.Data.AsSpan((int)offset, (int)size).CopyTo(new Span<byte>(buffer, (int)size));
        return 1;
    }

    /// <summary>The entry's bytes as the native reader locates them, or null; fails the test if it read outside.</summary>
    private static byte[]? Find(byte[] archive, string name, uint maxSize = 64 << 20)
    {
        var source = new Source(archive);
        var handle = GCHandle.Alloc(source);
        try
        {
            var find = (delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, uint, int>, IntPtr, byte*, uint, ulong*, uint*, int>)Function.Value;
            byte[] text = [.. Encoding.UTF8.GetBytes(name), 0];
            ulong offset = 0;
            uint size = 0;
            int found;
            fixed (byte* n = text) found = find((ulong)archive.Length, &ReadAt, GCHandle.ToIntPtr(handle), n, maxSize, &offset, &size);
            Assert.True(source.Outside.Count == 0, "read outside the archive: " + string.Join("; ", source.Outside));
            if (found != 1) return null;
            Assert.True(offset + size <= (ulong)archive.Length, $"{size} bytes at {offset} of {archive.Length}");
            return archive.AsSpan((int)offset, (int)size).ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    private static byte[] Bytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A document saved the way the app saves it, with its QuickLook images.</summary>
    private static (byte[] Archive, byte[] Thumbnail, byte[] Preview) SavedDocument()
    {
        byte[] thumbnail = Bytes(5_000, 1), preview = Bytes(21_000, 2);
        var dir = TempFolder("thumbnail-zip");
        try
        {
            var path = Path.Combine(dir, "doc.glimmer");
            GlimmerPackage.Save(SampleState(), path, quickLook: (thumbnail, preview));
            return (File.ReadAllBytes(path), thumbnail, preview);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [NativeZipFact]
    public void ASavedDocumentsQuickLookImagesAreFound()
    {
        var (archive, thumbnail, preview) = SavedDocument();
        Assert.Equal(thumbnail, Find(archive, "QuickLook/Thumbnail.png"));
        Assert.Equal(preview, Find(archive, "QuickLook/Preview.png"));
        // What isn't there, or is there compressed (the manifest is deflated), gives nothing.
        Assert.Null(Find(archive, "QuickLook/Missing.png"));
        Assert.Null(Find(archive, "quicklook/thumbnail.png"));
        Assert.Null(Find(archive, GlimmerPackage.ManifestFilename));
        // Larger than the reader was told to take.
        Assert.Null(Find(archive, "QuickLook/Thumbnail.png", maxSize: 4_999));
        Assert.Equal(thumbnail, Find(archive, "QuickLook/Thumbnail.png", maxSize: 5_000));
    }

    [NativeZipFact]
    public void ACommentAfterTheEndRecordIsSkipped()
    {
        byte[] thumbnail = Bytes(3_000, 3);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // "PK\5\6" inside the comment must not be taken for the end record.
            zip.Comment = "PK\u0005\u0006 " + new string('c', 5_000);
            using var entry = zip.CreateEntry("QuickLook/Thumbnail.png", CompressionLevel.NoCompression).Open();
            entry.Write(thumbnail);
        }
        Assert.Equal(thumbnail, Find(stream.ToArray(), "QuickLook/Thumbnail.png"));
    }

    /// <summary>More than 65,535 entries make .NET write a ZIP64 end record; the reader takes the real values from it.</summary>
    [NativeZipFact]
    public void ArchivesWithManyEntriesAreReadThroughTheirZip64Records()
    {
        byte[] thumbnail = Bytes(2_000, 4);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int i = 0; i < 70_000; i++) zip.CreateEntry($"Layers/{i:x5}", CompressionLevel.NoCompression);
            using var entry = zip.CreateEntry("QuickLook/Thumbnail.png", CompressionLevel.NoCompression).Open();
            entry.Write(thumbnail);
        }
        var archive = stream.ToArray();
        Assert.True(archive.AsSpan().IndexOf("PK\u0006\u0006"u8) >= 0, "no ZIP64 end record: the test didn't test it");
        Assert.Equal(thumbnail, Find(archive, "QuickLook/Thumbnail.png"));
        Assert.Null(Find(archive, "Layers/fffff"));
    }

    [NativeZipFact]
    public void ACompressedThumbnailIsNotUsed()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("QuickLook/Thumbnail.png", CompressionLevel.Optimal).Open();
            entry.Write(new byte[10_000]);   // zeros: deflated to almost nothing
        }
        Assert.Null(Find(stream.ToArray(), "QuickLook/Thumbnail.png"));
    }

    /// <summary>Cut short anywhere or with bytes changed: the image or nothing, never a read outside the archive (Find
    /// fails the test if the reader asks for one) and never a range outside it.</summary>
    [NativeZipFact]
    public void DamagedArchivesGiveTheImageOrNothing()
    {
        var (archive, thumbnail, _) = SavedDocument();
        for (int length = 0; length < archive.Length; length += Math.Max(1, archive.Length / 300))
        {
            var found = Find(archive[..length], "QuickLook/Thumbnail.png");
            Assert.True(found is null || found.AsSpan().SequenceEqual(thumbnail) || found.Length <= length, $"cut at {length}");
        }
        var random = new Random(5);
        for (int i = 0; i < 3_000; i++)
        {
            var bad = (byte[])archive.Clone();
            for (int k = 0; k < 4; k++) bad[random.Next(bad.Length)] = (byte)random.Next(256);
            _ = Find(bad, "QuickLook/Thumbnail.png");
        }
        // Nothing that is no ZIP at all.
        Assert.Null(Find([], "QuickLook/Thumbnail.png"));
        Assert.Null(Find(Bytes(70_000, 6), "QuickLook/Thumbnail.png"));
    }
}
