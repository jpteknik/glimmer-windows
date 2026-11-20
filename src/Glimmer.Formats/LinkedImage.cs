// Ported from Packages/GlimmerKit/Sources/GlimmerRender/LinkedImage.swift (moved here from Glimmer.Render with the rest
// of export). The Mac coordinates the write with NSFileCoordinator; Windows has no counterpart, so the file is simply
// opened, truncated and written in place. HEIC counts as writable only when the system's encoder is there.
using SkiaSharp;

namespace Glimmer.Formats;

/// <summary>
/// An image another app (Flimmer, Jämmer) handed Glimmer to edit, Photoshop-link style: the other app watches the file
/// and picks up the change when Glimmer saves.
/// </summary>
/// <remarks>
/// The image is never opened *as* a document: it becomes an untitled Glimmer document, so autosave can't touch the
/// file. Only an explicit Save writes it, flattened, in the same format, into the same file (in place, so the other
/// app's file watch keeps working).
/// </remarks>
public sealed record LinkedImage
{
    private LinkedImage(string path, ExportFormat format, string requester)
    {
        Path = path;
        Format = format;
        Requester = requester;
    }

    /// <summary>Null for a file whose format Glimmer can't write (Swift's failable initializer).</summary>
    public static LinkedImage? Create(string path, string requester) =>
        FormatFor(path) is { } format ? new LinkedImage(path, format, requester) : null;

    public string Path { get; init; }
    public ExportFormat Format { get; init; }
    /// <summary>The app that asked, for display ("Flimmer").</summary>
    public string Requester { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The format a file is written back in, from its extension; null for formats Glimmer can't write (such as
    /// PSD, and HEIC on a computer without the system's encoder), which open as ordinary new documents instead.</summary>
    public static ExportFormat? FormatFor(string path)
    {
        ExportFormat? format = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ExportFormat.Png,
            ".jpg" or ".jpeg" => ExportFormat.Jpeg,
            ".heic" or ".heif" => ExportFormat.Heic,
            ".tif" or ".tiff" => ExportFormat.Tiff,
            ".webp" => ExportFormat.Webp,
            _ => null,
        };
        return format is { } f && ImageExporter.CanWrite(f) ? f : null;
    }

    /// <summary>Encodes <paramref name="image"/> in the file's format and writes it into the same file, replacing its
    /// contents in place (no temporary file beside it, no package).</summary>
    public void Write(SKBitmap image, double quality = 0.92)
    {
        var data = ImageExporter.Encode(image, new ExportOptions(Format, quality: quality));
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.SetLength(0);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }
}
