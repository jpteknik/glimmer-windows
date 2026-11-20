// New file, no Swift original. The Mac reads a file whole as Data (memory-mapped where it can), which has no size
// limit. A .NET array holds at most about 2 GB, so a larger file is refused here, with words, before it is read
// (decided for the port: Photoshop files, and every other file, over 2 GB are refused).
namespace Glimmer.Formats;

internal static class FileInput
{
    /// <summary>The largest file read whole: the largest array .NET can make.</summary>
    internal static readonly long MaxBytes = Array.MaxLength;

    /// <summary>The file's bytes. <paramref name="tooLarge"/> is thrown, before anything is read, for a file larger
    /// than <paramref name="maxBytes"/> (left out: <see cref="MaxBytes"/>).</summary>
    public static byte[] Read(string path, Func<Exception> tooLarge, long? maxBytes = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
        if (stream.Length > (maxBytes ?? MaxBytes)) throw tooLarge();
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
