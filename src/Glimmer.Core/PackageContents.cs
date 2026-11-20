// The FileWrapper the Mac's GlimmerPackage.swift reads and writes, as Windows needs it. No Swift file of its own.
//
// On the Mac a .glimmer document is a package: a folder Finder shows as one file. Windows has no such folder, so a
// document here is a ZIP file whose root holds the package's files (manifest.json, layers/…, QuickLook/…). Reading
// also takes a package folder (as copied from a Mac) and a ZIP whose single top-level folder is a package (what
// Finder's Compress makes).

using System.Collections.Immutable;
using System.IO.Compression;

namespace Glimmer.Core;

/// <summary>
/// The files of a <c>.glimmer</c> package in memory, by path inside the package (<c>manifest.json</c>,
/// <c>layers/&lt;ID&gt;.tiles</c>, …; always <c>/</c> as the separator). Immutable.
/// </summary>
public sealed class PackageContents
{
    public static PackageContents Empty { get; } = new([]);

    public PackageContents(IEnumerable<KeyValuePair<string, ReadOnlyMemory<byte>>> files, IEnumerable<string>? directories = null)
    {
        var b = ImmutableSortedDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var (path, data) in files) b[Normalize(path)] = data;
        Files = b.ToImmutable();
        var dirs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var d in directories ?? []) if (Normalize(d) is { Length: > 0 } n) dirs.Add(n);
        foreach (var path in Files.Keys)
        {
            for (int cut = path.LastIndexOf('/'); cut > 0; cut = path.LastIndexOf('/', cut - 1)) dirs.Add(path[..cut]);
        }
        Directories = [.. dirs];
    }

    private PackageContents(ImmutableSortedDictionary<string, ReadOnlyMemory<byte>> files, ImmutableArray<string> directories)
    {
        Files = files;
        Directories = directories;
    }

    /// <summary>The files by path inside the package.</summary>
    public ImmutableSortedDictionary<string, ReadOnlyMemory<byte>> Files { get; }

    /// <summary>Every folder inside the package, including empty ones the writer asked for (<c>layers</c>).</summary>
    public ImmutableArray<string> Directories { get; }

    public ReadOnlyMemory<byte>? this[string path] =>
        Files.TryGetValue(Normalize(path), out var data) ? data : default(ReadOnlyMemory<byte>?);   // not `null`: that becomes an empty memory

    /// <summary>The bytes of every file (Swift's <c>DocumentSaver.byteCount(of:)</c>).</summary>
    public long ByteCount => Files.Values.Sum(d => (long)d.Length);

    /// <summary>The files directly inside <paramref name="directory"/>, by name.</summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> FilesIn(string directory)
    {
        var prefix = Normalize(directory) + "/";
        var result = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var (path, data) in Files)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var name = path[prefix.Length..];
            if (!name.Contains('/')) result[name] = data;
        }
        return result;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    // MARK: Reading

    /// <summary>
    /// Reads a document from disk: a ZIP file (the package's files at its root, or in one top-level <c>*.glimmer</c>
    /// folder), or a package folder.
    /// </summary>
    /// <exception cref="GlimmerPackageException">Not a package (<see cref="GlimmerPackageError.NotAPackage"/>).</exception>
    public static PackageContents Read(string path)
    {
        if (Directory.Exists(path)) return ReadDirectory(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The document doesn't exist.", path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        return ReadZip(stream);
    }

    /// <summary>A package folder, read whole (hidden files such as <c>.DS_Store</c> left out).</summary>
    public static PackageContents ReadDirectory(string directory)
    {
        var files = new List<KeyValuePair<string, ReadOnlyMemory<byte>>>();
        var dirs = new List<string>();
        var root = Path.GetFullPath(directory);
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
            if (!IsMacMetadata(rel)) dirs.Add(rel);
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsMacMetadata(rel)) continue;
            files.Add(new(rel, File.ReadAllBytes(file)));
        }
        return new PackageContents(files, dirs);
    }

    /// <summary>A package in a ZIP: its files at the root, or in a single top-level folder (Finder's Compress).</summary>
    /// <exception cref="GlimmerPackageException">The data is not a ZIP (<see cref="GlimmerPackageError.NotAPackage"/>).</exception>
    public static PackageContents ReadZip(Stream stream)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw GlimmerPackageException.NotAPackage();
        }
        using (archive)
        {
            var entries = new List<(string Path, ZipArchiveEntry Entry)>();
            var dirs = new List<string>();
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (IsMacMetadata(name)) continue;
                if (name.EndsWith('/')) dirs.Add(name.TrimEnd('/'));
                else entries.Add((name, entry));
            }
            // Finder's Compress puts the package in one folder: take that folder as the root.
            string prefix = "";
            if (!entries.Any(e => e.Path == GlimmerPackage.ManifestFilename))
            {
                var tops = entries.Select(e => e.Path.Split('/')[0]).Concat(dirs.Select(d => d.Split('/')[0])).Distinct().ToList();
                if (tops.Count == 1 && entries.All(e => e.Path.Contains('/'))) prefix = tops[0] + "/";
            }
            var files = new List<KeyValuePair<string, ReadOnlyMemory<byte>>>();
            foreach (var (path, entry) in entries)
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                using var s = entry.Open();
                var bytes = new byte[entry.Length];
                s.ReadExactly(bytes);
                files.Add(new(path[prefix.Length..], bytes));
            }
            return new PackageContents(files, dirs.Where(d => d.Length > prefix.Length && d.StartsWith(prefix, StringComparison.Ordinal))
                                                   .Select(d => d[prefix.Length..]));
        }
    }

    public static PackageContents ReadZip(byte[] zip)
    {
        using var stream = new MemoryStream(zip, writable: false);
        return ReadZip(stream);
    }

    /// <summary>Finder's own files, which are not part of a package: <c>__MACOSX/…</c>, <c>.DS_Store</c>, <c>._name</c>.</summary>
    private static bool IsMacMetadata(string path)
    {
        if (path.StartsWith("__MACOSX/", StringComparison.Ordinal) || path == "__MACOSX") return true;
        var name = path.TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name == ".DS_Store" || name.StartsWith("._", StringComparison.Ordinal);
    }

    // MARK: Writing

    /// <summary>
    /// Writes the package as a ZIP with its files at the root: pixel tiles and RAW files stored as they are (they are
    /// compressed already), the manifest deflated.
    /// </summary>
    public void WriteZip(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var dir in Directories) archive.CreateEntry(dir + "/");
        foreach (var (path, data) in Files)
        {
            bool compressible = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            var entry = archive.CreateEntry(path, compressible ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
            using var s = entry.Open();
            s.Write(data.Span);
        }
    }

    public byte[] ToZip()
    {
        using var stream = new MemoryStream();
        WriteZip(stream);
        return stream.ToArray();
    }

    /// <summary>Writes the package as a folder (the Mac's layout; undo snapshots and the link journal use it).</summary>
    public void WriteDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var dir in Directories) Directory.CreateDirectory(Path.Combine(directory, dir));
        foreach (var (path, data) in Files)
        {
            var target = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            file.Write(data.Span);
        }
    }
}
