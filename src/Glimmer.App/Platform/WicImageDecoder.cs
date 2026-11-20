// Glimmer.Formats' IPlatformImageDecoder on Windows: the Windows Imaging Component, through WPF's BitmapDecoder, for the
// formats SkiaSharp doesn't read (TIFF, HEIC/HEIF, AVIF, JPEG XR, camera RAW; HEIF and RAW need codecs from the
// Microsoft Store: "HEIF Image Extensions" and "Raw Image Extension"). Frame 0 comes back as 16-bit RGBA with straight
// alpha (every value of an 8- or 16-bit file kept), with the file's ICC profile and its EXIF orientation; Glimmer turns
// the picture upright and converts its colours. A floating-point file (HDR) comes back as linear float RGBA.
// Every call makes its own decoder, so calls on different threads don't share anything.
// For camera RAW files it is also Glimmer.Formats' IPlatformRawDecoder: which files are RAW is decided by their header, as
// before; the white balance a file was shot with is what the RAW codec's IWICDevelopRaw reports for its as-shot settings,
// when the codec offers that interface (a DNG's own tags come first, read by Glimmer.Formats itself).
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glimmer.App.Localization;

namespace Glimmer.App.Platform;

public sealed class WicImageDecoder : IPlatformImageDecoder
#if GLIMMER_RAW
    , IPlatformRawDecoder
#endif
{
    /// <summary>HRESULTs WIC uses when no codec reads the data.</summary>
    private const int WINCODEC_ERR_COMPONENTNOTFOUND = unchecked((int)0x88982F50), WINCODEC_ERR_UNKNOWNIMAGEFORMAT = unchecked((int)0x88982F07);

    public (int Width, int Height)? ReadSize(ReadOnlyMemory<byte> data)
    {
        try
        {
            var decoder = BitmapDecoder.Create(Stream(data), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or COMException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    public DecodedImage Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            var decoder = BitmapDecoder.Create(Stream(data), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            int orientation = Orientation(frame);
            if (IsFloat(frame.Format))
            {
                // HDR: linear light, values above 1 kept (scRGB).
                var linear = new FormatConvertedBitmap(frame, PixelFormats.Rgba128Float, null, 0);
                var floats = new float[(long)width * height * 4];
                linear.CopyPixels(floats, width * 16, 0);
                return DecodedImage.FromLinearRgba(width, height, floats, orientation);
            }
            var wide = new FormatConvertedBitmap(frame, PixelFormats.Rgba64, null, 0);
            var shorts = new ushort[(long)width * height * 4];
            wide.CopyPixels(shorts, width * 8, 0);
            return DecodedImage.FromRgba16(width, height, shorts, Profile(frame), orientation);
        }
        catch (Exception ex) when (IsMissingCodec(ex))
        {
            throw new NotSupportedException(Loc.T("Windows has no reader for this kind of picture."), ex);
        }
    }

    /// <summary>The words for a file WIC has no codec for, by its extension: what to install (for the opener's message).</summary>
    public static string MissingCodecMessage(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".heic" or ".heif" or ".hif" or ".avif" => Loc.T("This computer can’t read HEIC and AVIF pictures yet. Install “HEIF Image Extensions” from the Microsoft Store, then open the file again."),
        ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".gif" or ".bmp" or ".webp" or ".jxr" or ".wdp" or ".hdp" => Loc.T("The picture is damaged or in a form Windows can’t read."),
        _ => Loc.T("This computer can’t read this camera’s RAW files yet. Install “Raw Image Extension” from the Microsoft Store, then open the file again."),
    };

    private static MemoryStream Stream(ReadOnlyMemory<byte> data) =>
        MemoryMarshal.TryGetArray(data, out var segment) && segment.Array is not null
            ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(data.ToArray(), writable: false);

    private static bool IsFloat(PixelFormat format) =>
        format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float || format == PixelFormats.Rgb128Float
        || format == PixelFormats.Gray32Float || format.ToString().Contains("Float", StringComparison.Ordinal);

    private static bool IsMissingCodec(Exception ex) => ex switch
    {
        COMException c => c.HResult is WINCODEC_ERR_COMPONENTNOTFOUND or WINCODEC_ERR_UNKNOWNIMAGEFORMAT,
        { InnerException: COMException c } => c.HResult is WINCODEC_ERR_COMPONENTNOTFOUND or WINCODEC_ERR_UNKNOWNIMAGEFORMAT,
        _ => false,
    };

    /// <summary>The embedded ICC profile, or null for sRGB.</summary>
    private static ReadOnlyMemory<byte>? Profile(BitmapFrame frame)
    {
        try
        {
            if (frame.ColorContexts is not { Count: > 0 } contexts) return null;
            using var profile = contexts[0].OpenProfileStream();
            using var copy = new MemoryStream();
            profile.CopyTo(copy);
            return copy.ToArray();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or COMException or ArgumentException)
        {
            return null;
        }
    }

#if GLIMMER_RAW
    /// <summary>The header decides, as it did before this decoder said anything about RAW files (WIC's container formats
    /// for RAW differ from codec to codec).</summary>
    public bool IsRaw(ReadOnlyMemory<byte> data) => RawDevelopment.LooksLikeRaw(data.Span);

    /// <summary>WIC has no list of the cameras a RAW codec reads.</summary>
    public IReadOnlyList<string> Cameras => [];

    /// <summary>
    /// The codec's as-shot white point in kelvin and its tint, or null when the codec has no development interface or
    /// doesn't give a temperature. WIC's tint runs −1…1, a full green to a full magenta bias (IWICDevelopRaw::GetTint);
    /// it is taken onto Glimmer's −150…150, and a value outside WIC's range is not trusted. Nothing is decoded.
    /// </summary>
    public (double Temperature, double Tint)? AsShotWhiteBalance(ReadOnlyMemory<byte> data) =>
        Wic.InMultithreadedApartment(() => DevelopRawAsShot(data));

    private static (double Temperature, double Tint)? DevelopRawAsShot(ReadOnlyMemory<byte> data)
    {
        IWICImagingFactory? factory = null;
        IStream? stream = null;
        IWICBitmapDecoder? decoder = null;
        IWICBitmapFrameDecode? frame = null;
        try
        {
            factory = Wic.Factory();
            stream = Wic.StreamOver(data.Span);
            if (factory.CreateDecoderFromStream(stream, IntPtr.Zero, Wic.DecodeMetadataCacheOnDemand, out decoder) < 0 || decoder is null) return null;
            if (decoder.GetFrame(0, out frame) < 0 || frame is null) return null;
            if (frame is not IWICDevelopRaw raw) return null;   // the codec develops nothing
            var info = new WICRawCapabilitiesInfo { Size = (uint)Marshal.SizeOf<WICRawCapabilitiesInfo>() };
            if (raw.QueryRawCapabilitiesInfo(ref info) < 0 || info.KelvinWhitePointSupport == 0) return null;
            // A codec may start from the settings last saved for the file; only the as-shot set says what the camera did.
            if (raw.LoadParameterSet(Wic.AsShotParameterSet) < 0) return null;
            if (raw.GetWhitePointKelvin(out uint kelvin) < 0 || kelvin is < 1_000 or > 100_000) return null;
            double tint = info.TintSupport != 0 && raw.GetTint(out double t) >= 0 && double.IsFinite(t) && Math.Abs(t) <= 1 ? t * 150 : 0;
            return (kelvin, tint);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            Wic.Release(frame);
            Wic.Release(decoder);
            Wic.Release(stream);
            Wic.Release(factory);
        }
    }
#endif

    /// <summary>EXIF orientation 1–8 (the photo property works for JPEG, TIFF, HEIF and RAW), or 1.</summary>
    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("System.Photo.Orientation") is ushort value && value is >= 1 and <= 8)
                return value;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or COMException)
        {
            // No metadata reader for this format: as stored.
        }
        return 1;
    }
}
