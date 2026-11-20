// Ported from Packages/GlimmerKit/Sources/GlimmerCore/BatchFiles.swift

using System.Globalization;

namespace Glimmer.Core;

/// <summary>
/// The images a batch takes from what was dropped or chosen: image files, <c>.glimmer</c> documents, and the images
/// directly inside a folder (sorted by name, not its subfolders). Pure file work, so it can run off the UI thread: a
/// folder of a hundred thousand pictures must not stop the window while it is read.
/// </summary>
/// <remarks>
/// Paths are compared as written, without case on Windows (its file systems ignore case) and with case elsewhere.
/// </remarks>
public static class BatchFiles
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The images among <paramref name="paths"/>, in the order given, a folder's images sorted by name.</summary>
    public static List<string> Images(IEnumerable<string> paths)
    {
        var found = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path) && !IsGlimmer(path))
            {
                IEnumerable<string> items;
                try
                {
                    items = Directory.EnumerateFiles(path, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                        IgnoreInaccessible = true,
                    }).Concat(Directory.EnumerateDirectories(path, "*.glimmer", new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                        IgnoreInaccessible = true,
                    }));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                found.AddRange(items
                    .Where(item => !Path.GetFileName(item).StartsWith('.'))   // hidden on the Mac and on Linux
                    .Where(IsImage)
                    .OrderBy(item => Path.GetFileName(item), NaturalOrder.Instance));
            }
            else if (IsImage(path))
            {
                found.Add(path);
            }
        }
        return found;
    }

    /// <summary>
    /// <paramref name="existing"/> followed by the files of <paramref name="added"/> that are in neither, each once. A
    /// set, not a scan of the list per file: adding a second large folder to a large list stays quick.
    /// </summary>
    public static List<string> Merge(IReadOnlyList<string> existing, IEnumerable<string> added)
    {
        var known = new HashSet<string>(existing, PathComparer);
        var result = new List<string>(existing);
        foreach (var path in added)
            if (known.Add(path)) result.Add(path);
        return result;
    }

    private static bool IsGlimmer(string path) =>
        string.Equals(Path.GetExtension(path.TrimEnd('/', '\\')), ".glimmer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The extensions macOS gives a type that conforms to <c>public.image</c> (what the Mac checks), plus the RAW
    /// formats Glimmer develops.
    /// </summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".heic", ".heics", ".heif", ".hif",
        ".webp", ".avif", ".jxl", ".ico", ".icns", ".cur", ".psd", ".psb", ".tga", ".exr", ".hdr", ".jp2", ".j2k", ".jpf", ".jpx",
        ".pbm", ".pgm", ".ppm", ".pnm", ".pict", ".pct", ".pic", ".sgi", ".rgb", ".xbm", ".svg", ".astc", ".ktx", ".ktx2", ".dds",
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".orf", ".raf", ".rw2", ".rwl", ".pef", ".ptx",
        ".srw", ".x3f", ".erf", ".mrw", ".mos", ".mef", ".kdc", ".dcr", ".3fr", ".fff", ".iiq", ".raw", ".gpr",
    };

    public static bool IsImage(string path)
    {
        if (IsGlimmer(path)) return true;
        return ImageExtensions.Contains(Path.GetExtension(path));
    }

    /// <summary>Finder's name order (<c>localizedStandardCompare</c>): without case, and runs of digits by value ("b2" before "b10").</summary>
    internal sealed class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            int i = 0, j = 0;
            var compare = CultureInfo.CurrentCulture.CompareInfo;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    var a = x.AsSpan(si, i - si).TrimStart('0');
                    var b = y.AsSpan(sj, j - sj).TrimStart('0');
                    if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
                    int c = a.SequenceCompareTo(b);
                    if (c != 0) return Math.Sign(c);
                    continue;
                }
                int ei = i, ej = j;
                while (ei < x.Length && !char.IsDigit(x[ei])) ei++;
                while (ej < y.Length && !char.IsDigit(y[ej])) ej++;
                int text = compare.Compare(x, i, ei - i, y, j, ej - j, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth);
                if (text != 0) return Math.Sign(text);
                i = ei;
                j = ej;
            }
            int rest = (x.Length - i).CompareTo(y.Length - j);
            return rest != 0 ? Math.Sign(rest) : Math.Sign(string.CompareOrdinal(x, y));
        }
    }
}
