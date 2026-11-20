// New file, no Swift original: on the Mac ImageIO writes HEIC (GlimmerRender/ImageExporter.swift, encodeOnce).
// SkiaSharp has no HEIC encoder; Windows has one in WIC when the HEIF and HEVC Video Extensions are installed, so the
// Windows app provides it through this interface, the way it provides decoding through IPlatformImageDecoder.

namespace Glimmer.Formats;

/// <summary>An image encoder the operating system provides, for a format SkiaSharp can't write (HEIC).</summary>
public interface IPlatformImageEncoder
{
    /// <summary>True when this computer can write <paramref name="format"/> now.</summary>
    bool CanEncode(ExportFormat format);

    /// <summary>
    /// Encodes 8-bit RGBA, straight alpha, row-major, top-down (alpha is 255 where the picture is opaque; HEIC keeps
    /// transparency), in the colour space of <paramref name="iccProfile"/>, which the file should carry.
    /// <paramref name="quality"/> is 0…1 for lossy formats.
    /// </summary>
    byte[] Encode(ExportFormat format, ReadOnlySpan<byte> rgba, int width, int height, ReadOnlySpan<byte> iccProfile, double quality);
}
