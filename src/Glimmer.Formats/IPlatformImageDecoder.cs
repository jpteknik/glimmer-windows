// New file, no Swift original. The Mac opens every picture ImageIO can read: TIFF, HEIC, AVIF, camera RAW and more.
// SkiaSharp reads PNG, JPEG, WebP, GIF, BMP and ICO; for the rest Glimmer asks the system, which on Windows is the
// Windows Imaging Component (WIC). The app implements this interface with WIC and sets ImageImporter.PlatformDecoder at
// start-up; the libraries never call Windows themselves.

namespace Glimmer.Formats;

/// <summary>
/// The system's image reader, for the formats SkiaSharp can't read. Asked only after SkiaSharp has declined a file.
/// Implementations must be safe to call from several threads at once: pictures are opened away from the UI thread.
/// </summary>
public interface IPlatformImageDecoder
{
    /// <summary>
    /// The picture's width and height as the file stores them (before any EXIF orientation is applied), read from its
    /// header without decoding any pixels; null when this decoder can't read the file at all.
    /// </summary>
    /// <remarks>Called first, so that a file claiming a huge picture is refused before anything is decoded. With WIC:
    /// <c>IWICBitmapDecoder</c> from a stream over the bytes, frame 0, <c>GetSize</c>.</remarks>
    (int Width, int Height)? ReadSize(ReadOnlyMemory<byte> data);

    /// <summary>
    /// The picture: frame 0, all of it, at the size <see cref="ReadSize"/> gave. Throw (any exception) when the file is
    /// damaged or incomplete: half a picture must never come back as a whole one.
    /// </summary>
    /// <remarks>
    /// Return it with <see cref="DecodedImage.FromRgba16"/> (WIC's <c>GUID_WICPixelFormat64bppRGBA</c>: straight alpha,
    /// 16 bits a sample, so 8- and 16-bit files both keep every value), with the file's ICC profile
    /// (<c>IWICColorContext</c>, or null for sRGB) and its EXIF orientation (<c>System.Photo.Orientation</c>, or 1); Glimmer
    /// turns the picture upright and converts its colours. A floating-point file (HDR) is returned with
    /// <see cref="DecodedImage.FromLinearRgba"/> (<c>GUID_WICPixelFormat128bppRGBAFloat</c>, linear, values above 1 kept).
    /// </remarks>
    DecodedImage Decode(ReadOnlyMemory<byte> data);
}
