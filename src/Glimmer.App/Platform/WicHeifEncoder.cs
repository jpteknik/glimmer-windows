// New file, no Swift original. On the Mac ImageIO writes HEIC (GlimmerRender/ImageExporter.swift, encodeOnce). Windows
// writes it through WIC's HEIF encoder, which is there when the Microsoft Store's "HEIF Image Extensions" is installed and
// which encodes with the "HEVC Video Extensions" (Windows has no HEVC encoder of its own). Glimmer.Formats asks through
// IPlatformImageEncoder; on a computer without both extensions HEIC is not offered (ImageExporter.CanWrite), so the
// export sheet, Batch Process, the command line and the agent API never promise a file they can't write.
//
// What the file gets: the picture at the export's quality (the encoder's "ImageQuality" option, 0…1), its colour profile
// when the encoder takes one (a Display P3 picture whose profile the encoder refuses is refused rather than written with
// the wrong colours), and its transparency when the encoder keeps alpha (otherwise it is flattened over white, the
// export's default matte, rather than letting the colour under transparent pixels show).
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Glimmer.Core;

namespace Glimmer.App.Platform;

public sealed class WicHeifEncoder : IPlatformImageEncoder
{
    private readonly object gate = new();
    private bool? works;
    private long checkedAt;

    /// <summary>How long a "no" stands before it is tried again (the extensions can be installed while Glimmer runs).</summary>
    private const long RecheckMilliseconds = 30_000;

    /// <summary>
    /// True when this computer writes HEIC now. Found by encoding a small picture and checking that a HEIF file comes
    /// out: creating the encoder succeeds as soon as the HEIF extension is there, but encoding needs the HEVC one too.
    /// Remembered (a "no" for 30 seconds).
    /// </summary>
    public bool CanEncode(ExportFormat format)
    {
        if (format != ExportFormat.Heic) return false;
        lock (gate)
        {
            if (works == true || (works == false && Environment.TickCount64 - checkedAt < RecheckMilliseconds)) return works.Value;
        }
        bool result;
        try
        {
            var grey = new byte[16 * 16 * 4];
            for (int i = 0; i < grey.Length; i += 4) { grey[i] = 128; grey[i + 1] = 128; grey[i + 2] = 128; grey[i + 3] = 255; }
            var file = InApartment(grey, 16, 16, IccProfile.Rgb(WorkingColorSpace.SRGB), 0.9);
            result = file.Length > 12 && file.AsSpan(4, 4).SequenceEqual("ftyp"u8);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Whatever stopped the small picture (no encoder, no HEVC codec, a codec that fails) stops a real one too.
            result = false;
        }
        lock (gate)
        {
            works = result;
            checkedAt = Environment.TickCount64;
        }
        return result;
    }

    public byte[] Encode(ExportFormat format, ReadOnlySpan<byte> rgba, int width, int height, ReadOnlySpan<byte> iccProfile, double quality)
    {
        if (format != ExportFormat.Heic) throw new ArgumentException($"{format.Title()} is not written by Windows' HEIC encoder.", nameof(format));
        if (width < 1 || height < 1 || rgba.Length < (long)width * height * 4) throw new ArgumentException("The picture's pixels don't match its size.", nameof(rgba));
        // The work runs in the multithreaded apartment; from a UI thread that means another thread, and so a copy.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return InApartment(rgba.ToArray(), width, height, iccProfile.ToArray(), quality);
        return Write(rgba, width, height, iccProfile, quality);
    }

    private static byte[] InApartment(byte[] rgba, int width, int height, byte[] iccProfile, double quality) =>
        Wic.InMultithreadedApartment(() => Write(rgba, width, height, iccProfile, quality));

    private static unsafe byte[] Write(ReadOnlySpan<byte> rgba, int width, int height, ReadOnlySpan<byte> iccProfile, double quality)
    {
        IWICImagingFactory? factory = null;
        IStream? stream = null;
        IWICBitmapEncoder? encoder = null;
        IWICBitmapFrameEncode? frame = null;
        IPropertyBag2? options = null;
        IWICColorContext? profile = null;
        IWICBitmapSource? source = null;
        IWICFormatConverter? converter = null;
        try
        {
            factory = Wic.Factory();
            stream = Wic.NewStream();
            var container = Wic.ContainerFormatHeif;
            Wic.Check(factory.CreateEncoder(ref container, IntPtr.Zero, out encoder), "creating the HEIC encoder");
            Wic.Check(encoder.Initialize(stream, Wic.EncoderNoCache), "starting the HEIC file");
            Wic.Check(encoder.CreateNewFrame(out frame, ref options), "adding the picture");
            if (options is not null)
            {
                // An encoder that has no quality option keeps its own default; the file is still made.
                var name = new PROPBAG2 { Name = "ImageQuality" };
                object value = (float)Math.Clamp(quality, 0, 1);
                _ = options.Write(1, ref name, ref value);
            }
            Wic.Check(frame.Initialize(options), "setting the HEIC options");
            Wic.Check(frame.SetSize((uint)width, (uint)height), "setting the picture's size");
            // Asked for 8-bit BGRA with alpha; the encoder answers with the format it writes.
            var written = Wic.PixelFormat32bppBGRA;
            Wic.Check(frame.SetPixelFormat(ref written), "choosing the pixel format");
            if (iccProfile.Length > 0)
            {
                Wic.Check(factory.CreateColorContext(out profile), "making the colour profile");
                Wic.Check(profile.InitializeFromMemory(iccProfile.ToArray(), (uint)iccProfile.Length), "reading the colour profile");
                int hr = frame.SetColorContexts(1, [profile]);
                if (hr < 0 && !iccProfile.SequenceEqual(IccProfile.Rgb(WorkingColorSpace.SRGB)))
                    throw new InvalidOperationException("Windows' HEIC encoder can't store this picture's colour profile. Export HEIC in sRGB, or choose PNG or TIFF to keep Display P3.");
            }
            // The pixels: straight RGBA as given, or flattened over white when the encoder writes no alpha.
            byte[]? flat = Wic.HasAlpha(written) ? null : OverWhite(rgba, width, height);
            ReadOnlySpan<byte> pixels = flat ?? rgba[..(width * height * 4)];
            var given = Wic.PixelFormat32bppRGBA;
            fixed (byte* p = pixels)
            {
                // WIC copies the pixels into its own bitmap here.
                Wic.Check(factory.CreateBitmapFromMemory((uint)width, (uint)height, ref given, (uint)(width * 4), (uint)pixels.Length, (IntPtr)p, out source),
                          "taking the picture's pixels");
            }
            IWICBitmapSource toWrite = source;
            if (written != given)
            {
                Wic.Check(factory.CreateFormatConverter(out converter), "making a pixel converter");
                Wic.Check(converter.Initialize(source, ref written, Wic.DitherTypeNone, IntPtr.Zero, 0, Wic.PaletteTypeCustom), "converting the pixels");
                toWrite = (IWICBitmapSource)converter;
            }
            Wic.Check(frame.WriteSource(toWrite, IntPtr.Zero), "encoding the picture");
            Wic.Check(frame.Commit(), "finishing the picture");
            Wic.Check(encoder.Commit(), "finishing the HEIC file");
            return Wic.Bytes(stream);
        }
        finally
        {
            Wic.Release(converter);
            Wic.Release(source);
            Wic.Release(profile);
            Wic.Release(options);
            Wic.Release(frame);
            Wic.Release(encoder);
            Wic.Release(stream);
            Wic.Release(factory);
        }
    }

    /// <summary>Straight 8-bit RGBA flattened over white in the encoded values (as ImageExporter flattens over its matte),
    /// alpha set to opaque.</summary>
    private static byte[] OverWhite(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var flat = rgba[..(width * height * 4)].ToArray();
        for (int i = 0; i < flat.Length; i += 4)
        {
            int a = flat[i + 3];
            if (a == 255) continue;
            for (int c = 0; c < 3; c++) flat[i + c] = (byte)((flat[i + c] * a + 255 * (255 - a) + 127) / 255);
            flat[i + 3] = 255;
        }
        return flat;
    }
}
