// New file, no Swift original. On the Mac, CIRAWFilter is Apple's decoder and development in one. On Windows the system
// decodes camera RAW files through WIC (the Raw Image Extension), which the app reaches through IPlatformImageDecoder;
// this optional interface, on the same object, adds what WIC also knows about a RAW file (IWICDevelopRaw's white point,
// the codec's camera list). A DNG's white balance is read from the file itself (DngWhiteBalance); for other RAW files,
// without this interface or its answer, development takes the file as shot at daylight (6500 K, tint 0).
#if GLIMMER_RAW   // Directory.Build.props switches this (Config/Features.xcconfig on the Mac)

namespace Glimmer.Formats;

/// <summary>What the system's RAW decoder knows about a file beyond its picture. Implemented, when it can be, by the
/// object that is <see cref="RawDevelopment.Decoder"/> (or <see cref="ImageImporter.PlatformDecoder"/>).</summary>
public interface IPlatformRawDecoder : IPlatformImageDecoder
{
    /// <summary>True when the decoder takes the file as a camera RAW file (not an ordinary picture).</summary>
    bool IsRaw(ReadOnlyMemory<byte> data);

    /// <summary>The white balance the camera recorded, as the light's colour temperature in kelvin and tint (Adobe's
    /// scale: positive for a light greener than daylight's curve, as under fluorescent tubes), or null when the file or
    /// decoder doesn't say.</summary>
    (double Temperature, double Tint)? AsShotWhiteBalance(ReadOnlyMemory<byte> data);

    /// <summary>The camera models the decoder knows, for showing in the app.</summary>
    IReadOnlyList<string> Cameras { get; }
}
#endif
