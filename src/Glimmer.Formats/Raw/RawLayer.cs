// Ported from Packages/GlimmerKit/Sources/GlimmerRender/RawLayer.swift. The file size in the refusal is written as
// Foundation's ByteCountFormatter writes a file size (decimal units).
#if GLIMMER_RAW   // Directory.Build.props switches this (Config/Features.xcconfig on the Mac)
using System.Diagnostics;
using System.Globalization;
using Glimmer.Core;

namespace Glimmer.Formats;

/// <summary>A layer developed from a RAW file: made, and developed again with other settings.</summary>
public static class RawLayer
{
    /// <summary>A layer holding the RAW file and its developed picture, and the picture's size.</summary>
    public static (Layer Layer, int Width, int Height) Make(byte[] data, string fileName, RawSettings? settings = null)
    {
        if (data.Length > RawSource.MaxBytes)
            throw new RawDevelopment.Problem($"This RAW file is {FileSize(data.Length)}, more than Glimmer keeps in a document.");
        var (pixels, width, height, applied) = RawDevelopment.DevelopPixels(data, settings ?? new RawSettings());
        var tiles = Tiles(pixels, width, height);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        var layer = new Layer(stem.Length == 0 ? fileName : stem, new RasterContent(tiles))
        {
            Raw = new RawSource(data, fileName, applied, tiles.ContentSignature),
        };
        return (layer, width, height);
    }

    /// <summary>The same layer developed with other settings: new pixels, the same RAW file. Everything else about the
    /// layer (mask, opacity, blend mode, effects) stays. Throws for a layer that has no RAW file.</summary>
    public static Layer Developed(Layer layer, RawSettings settings) => DevelopedTimed(layer, settings).Layer;

    /// <summary>The same, with how long the decoder took and how long the picture took to become Glimmer's tiles.</summary>
    public static (Layer Layer, double DecoderSeconds, double TileSeconds) DevelopedTimed(Layer layer, RawSettings settings)
    {
        if (layer.Raw is not { } raw) throw new RawDevelopment.Problem("This layer has no RAW file to develop.");
        var clock = Stopwatch.StartNew();
        var (pixels, width, height, applied) = RawDevelopment.DevelopPixels(raw.Data, settings);
        double decoded = clock.Elapsed.TotalSeconds;
        var tiles = Tiles(pixels, width, height);
        double tiled = clock.Elapsed.TotalSeconds;
        var output = layer with
        {
            Content = new RasterContent(tiles),
            Raw = new RawSource(raw.Data, raw.FileName, applied, tiles.ContentSignature),
        };
        return (output, decoded, tiled - decoded);
    }

    /// <summary>Whether the layer's pixels are still the ones its RAW file was developed to. False when something was
    /// painted or pasted on the layer since: developing again replaces that.</summary>
    public static bool PixelsAreAsDeveloped(Layer layer) =>
        layer.Raw is { } raw && layer.Content is RasterContent r && raw.DevelopedSignature == r.Tiles.ContentSignature;

    /// <summary>Developed pixels (opaque, so straight and premultiplied are the same) as tiles.</summary>
    private static TileGrid Tiles(Half[] pixels, int width, int height) =>
        ImageImporter.Tiles(DecodedImage.Halves(width, height, pixels, SourceColor.LinearSrgb));

    /// <summary>A file size as Foundation's ByteCountFormatter writes one for files: decimal units.</summary>
    internal static string FileSize(long bytes)
    {
        var c = CultureInfo.InvariantCulture;
        if (bytes < 1_000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        if (bytes < 1_000_000) return (bytes / 1e3).ToString("0", c) + " KB";
        if (bytes < 1_000_000_000) return (bytes / 1e6).ToString("0.#", c) + " MB";
        return (bytes / 1e9).ToString("0.##", c) + " GB";
    }
}
#endif
