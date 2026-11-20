// Ported from Packages/GlimmerKit/Sources/GlimmerRender/RawDevelopment.swift, with the decoder and the development
// parted. The Mac's CIRAWFilter decodes and develops in one; here the system decodes (WIC's RAW codecs, through
// IPlatformImageDecoder: the picture as shot, in linear light) and Glimmer develops the settings on the decoded picture
// (RawColor). What that changes, against the Mac:
//   - Exposure, white balance (temperature and tint), shadows and contrast are Glimmer's own arithmetic on the picture
//     as shot, not the decoder's on the sensor data. The look differs from Apple's, and white balance can't bring back
//     what the decoder clipped.
//   - Highlight recovery, sharpness, noise reduction and lens correction are not offered (RawInfo says so, and RawRequest
//     refuses them by name, as it does on the Mac for a camera whose decoder lacks one).
//   - There is one process, "glimmer-1" (Process). A document developed on the Mac names Apple's decoder version
//     ("9.dng"), which this computer doesn't have: developing it again says so, as the Mac does after a system update.
//   - The camera's own white balance: a DNG's, read from the file by the DNG specification (DngWhiteBalance), as Apple's
//     decoder reads it; for other RAW files the decoder's when it gives one (IPlatformRawDecoder: WIC's IWICDevelopRaw);
//     otherwise the picture is taken as shot at daylight, 6500 K and tint 0.
//   - Contrast "as shot" is 0.5: the middle of its range, where Glimmer's contrast curve does nothing.
//   - Which files count as RAW: what the system decoder says (IPlatformRawDecoder.IsRaw), or, from a decoder that
//     can't say, files whose header is a camera RAW format (RawContainer).
//   - A draft is the as-shot picture made small once, developed per change; on smooth areas it is the finished
//     picture's development, at edges within the resampling's rounding.
#if GLIMMER_RAW   // Directory.Build.props switches this (Config/Features.xcconfig on the Mac)
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Numerics.Tensors;
using Glimmer.Core;
using Glimmer.Render;
using SkiaSharp;

namespace Glimmer.Formats;

/// <summary>What a RAW file's decoder says about it, before anything is developed.</summary>
public sealed record RawInfo
{
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>The development versions this computer offers for the file, oldest first.</summary>
    public IReadOnlyList<string> Processes { get; init; } = [];
    public bool SupportsLensCorrection { get; init; }
    public bool SupportsHighlightRecovery { get; init; }
    public bool SupportsLuminanceNoise { get; init; }
    public bool SupportsColorNoise { get; init; }
    public bool SupportsSharpness { get; init; }
    public bool SupportsContrast { get; init; }
    /// <summary>The file's own white balance and the development's defaults for it.</summary>
    public double Temperature { get; init; }
    public double Tint { get; init; }
    public double LuminanceNoise { get; init; }
    public double ColorNoise { get; init; }
    public double Sharpness { get; init; }
    public double Contrast { get; init; }
    public bool LensCorrection { get; init; }

    public bool Equals(RawInfo? other) => other is not null && Width == other.Width && Height == other.Height
        && Processes.SequenceEqual(other.Processes) && SupportsLensCorrection == other.SupportsLensCorrection
        && SupportsHighlightRecovery == other.SupportsHighlightRecovery && SupportsLuminanceNoise == other.SupportsLuminanceNoise
        && SupportsColorNoise == other.SupportsColorNoise && SupportsSharpness == other.SupportsSharpness
        && SupportsContrast == other.SupportsContrast && Temperature == other.Temperature && Tint == other.Tint
        && LuminanceNoise == other.LuminanceNoise && ColorNoise == other.ColorNoise && Sharpness == other.Sharpness
        && Contrast == other.Contrast && LensCorrection == other.LensCorrection;

    public override int GetHashCode() => HashCode.Combine(Width, Height, Processes.Count, Temperature, Tint);
}

public static class RawDevelopment
{
    public sealed class Problem(string message, bool tooLarge = false) : Exception(message)
    {
        /// <summary>The file is larger than Glimmer or this computer takes. Said here, so that nothing has to read it out of
        /// the message's words.</summary>
        public bool TooLarge { get; } = tooLarge;
    }

    /// <summary>The one development this version offers.</summary>
    public const string Process = "glimmer-1";

    /// <summary>The system's decoder for RAW files. Null: <see cref="ImageImporter.PlatformDecoder"/>.</summary>
    public static IPlatformImageDecoder? Decoder { get; set; }

    private static IPlatformImageDecoder? Current => Decoder ?? ImageImporter.PlatformDecoder;

    internal static Problem NotRaw() => new("Windows gives no picture for this RAW file. Which cameras and recording modes work is decided by Windows and its Raw Image Extension; another recording mode of the same camera may work.");

    private static Problem Failed() => new("Windows couldn't develop this RAW file.");

    /// <summary>Contrast where Glimmer's contrast curve does nothing.</summary>
    private const double NeutralContrast = 0.5;

    private static IPlatformImageDecoder DecoderFor(ReadOnlyMemory<byte> data)
    {
        // The decoder is the system's, inside this process: Glimmer checks what it can before and after.
        if (data.Length < 64 || Current is not { } decoder) throw NotRaw();
        bool raw;
        try { raw = decoder is IPlatformRawDecoder r ? r.IsRaw(data) : RawContainer.Looks(data.Span); }
        catch (Exception e) when (e is not OutOfMemoryException) { raw = false; }
        if (!raw) throw NotRaw();
        return decoder;
    }

    /// <summary>The file's size and what can be set, read from its header: nothing is developed. A file larger than this
    /// computer handles is refused here, before the first decode.</summary>
    public static RawInfo Info(ReadOnlyMemory<byte> data, long? maxPixels = null)
    {
        var decoder = DecoderFor(data);
        (int Width, int Height)? size;
        try { size = decoder.ReadSize(data); }
        catch (Exception e) when (e is not OutOfMemoryException) { size = null; }
        if (size is not { Width: >= 1, Height: >= 1 } s) throw NotRaw();
        CheckSize(s.Width, s.Height, maxPixels);
        var (temperature, tint) = AsShot(decoder, data);
        return new RawInfo
        {
            Width = s.Width, Height = s.Height, Processes = [Process],
            Temperature = temperature, Tint = tint, Contrast = NeutralContrast, SupportsContrast = true,
        };
    }

    private static void CheckSize(long width, long height, long? maxPixels)
    {
        long limit = maxPixels ?? DocumentLimits.MaxCanvasPixels();
        if (width * height > limit || width > DocumentManifest.MaxDimension || height > DocumentManifest.MaxDimension)
        {
            throw new Problem(DocumentLimits.Refusal(width, height)
                              ?? string.Create(CultureInfo.InvariantCulture, $"This RAW file is {width} × {height} pixels, more than this computer handles."), tooLarge: true);
        }
    }

    /// <summary>The white balance the picture was shot with: a DNG's own (its colour matrices and as-shot neutral, which
    /// is what Apple's decoder reports for it on the Mac), else what the system decoder says, else daylight.</summary>
    private static (double Temperature, double Tint) AsShot(IPlatformImageDecoder decoder, ReadOnlyMemory<byte> data)
    {
        static (double, double)? Usable((double Temperature, double Tint)? wb) =>
            wb is { } w && double.IsFinite(w.Temperature) && double.IsFinite(w.Tint) ? (Math.Clamp(w.Temperature, 2_000, 50_000), Math.Clamp(w.Tint, -150, 150)) : null;
        if (Usable(DngWhiteBalance.AsShot(data.Span)) is { } fromFile) return fromFile;
        try
        {
            if (decoder is IPlatformRawDecoder r && Usable(r.AsShotWhiteBalance(data)) is { } fromDecoder) return fromDecoder;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        return (RawColor.ReferenceTemperature, RawColor.ReferenceTint);
    }

    /// <summary>Whether a file's header is one of the camera RAW formats (for a system decoder that implements
    /// <see cref="IPlatformRawDecoder.IsRaw"/> without an opinion of its own).</summary>
    public static bool LooksLikeRaw(ReadOnlySpan<byte> data) => RawContainer.Looks(data);

    /// <summary>The settings as they will be applied (the process filled in); refused when they name a process this
    /// computer doesn't have.</summary>
    private static RawSettings Applied(RawSettings settings)
    {
        if (settings.Process is { } wanted && wanted != Process)
        {
            throw new Problem($"This picture was developed with process {wanted}, which this computer doesn't offer. Develop it again to use the current one; it may look a little different.");
        }
        return settings with { Process = Process };
    }

    /// <summary>The decoded picture as shot: linear sRGB, premultiplied, R, G, B, A per pixel.</summary>
    private static (float[] Pixels, int Width, int Height, double Temperature, double Tint) AsShotPicture(ReadOnlyMemory<byte> data, long? maxPixels)
    {
        var info = Info(data, maxPixels);
        var decoder = Current!;
        DecodedImage image;
        try { image = decoder.Decode(data); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw Failed(); }
        // The decoder's picture is checked as well as its header: it is what is held in memory.
        CheckSize(image.Width, image.Height, maxPixels);
        int w = image.Width, h = image.Height;
        var pixels = new float[(long)w * h * 4];
        Parallel.For(0, h, y => image.Row(y, pixels.AsSpan((int)((long)y * w * 4), w * 4)));
        return (pixels, w, h, info.Temperature, info.Tint);
    }

    /// <summary>Develops <paramref name="settings"/> on the as-shot <paramref name="pixels"/> into Halves.</summary>
    private static Half[] Developed(float[] pixels, int width, int height, RawSettings settings, double asShotTemperature, double asShotTint)
    {
        double temperature = Math.Clamp(settings.Temperature ?? asShotTemperature, 2_000, 50_000);
        double tint = Math.Clamp(settings.Tint ?? asShotTint, -150, 150);
        float[]? balance = temperature == asShotTemperature && tint == asShotTint ? null : RawColor.Balance(asShotTemperature, asShotTint, temperature, tint);
        float gain = (float)Math.Pow(2, Math.Clamp(settings.Exposure, -5, 5));
        float shadows = (float)(Math.Clamp(settings.Shadows, 0, 2) - 1);
        float contrast = settings.Contrast is { } c ? (float)Math.Clamp(Math.Clamp(c, 0, 1) - NeutralContrast, -0.5, 0.5) : 0;
        bool toned = shadows != 0 || contrast != 0;
        var output = new Half[pixels.LongLength];
        Parallel.For(0, height, () => new float[width * 4], (y, _, row) =>
        {
            pixels.AsSpan((int)((long)y * width * 4), width * 4).CopyTo(row);
            for (int i = 0; i < row.Length; i += 4)
            {
                float a = Math.Clamp(row[i + 3], 0f, 1f);
                if (!(a > 0)) { row[i] = row[i + 1] = row[i + 2] = row[i + 3] = 0; continue; }
                var c = new Vector3(row[i], row[i + 1], row[i + 2]) / a;
                if (balance is not null)
                {
                    c = new Vector3(balance[0] * c.X + balance[1] * c.Y + balance[2] * c.Z, balance[3] * c.X + balance[4] * c.Y + balance[5] * c.Z,
                                    balance[6] * c.X + balance[7] * c.Y + balance[8] * c.Z);
                }
                c *= gain;
                if (toned)
                {
                    float luminance = Vector3.Dot(c, RawColor.Luminance);
                    if (luminance > 0 && luminance < 1) c *= RawColor.Tone(luminance, shadows, contrast) / luminance;
                }
                c *= a;
                row[i] = c.X; row[i + 1] = c.Y; row[i + 2] = c.Z; row[i + 3] = a;
            }
            TensorPrimitives.ConvertToHalf(row, output.AsSpan((int)((long)y * width * 4), width * 4));
            return row;
        }, _ => { });
        return output;
    }

    /// <summary>The developed picture as Halves, its size, and the settings as applied (internal: RawLayer makes tiles
    /// from it without a bitmap in between).</summary>
    internal static (Half[] Pixels, int Width, int Height, RawSettings Applied) DevelopPixels(ReadOnlyMemory<byte> data, RawSettings settings, double scale = 1,
                                                                                       long? maxPixels = null)
    {
        var applied = Applied(settings);
        var (asShot, w, h, t0, tint0) = AsShotPicture(data, maxPixels);
        var developed = Developed(asShot, w, h, applied, t0, tint0);
        if (scale < 1)
        {
            // The picture is made smaller after developing, as on the Mac.
            double s = Math.Clamp(scale, 0.02, 1);
            int sw = Math.Max(1, (int)Math.Round(w * s, MidpointRounding.AwayFromZero)), sh = Math.Max(1, (int)Math.Round(h * s, MidpointRounding.AwayFromZero));
            using var full = LinearImages.Bitmap(developed, w, h);
            using var source = LinearPixels.Of(full);
            return (LinearResample.Scale(source, (0, 0, w, h), sw, sh), sw, sh, applied);
        }
        return (developed, w, h, applied);
    }

    /// <summary>The developed picture, in Glimmer's own pixels (linear light, Float16, premultiplied), and the settings as
    /// they were applied (the process filled in). <paramref name="scale"/> below 1 is a quick, smaller draft.</summary>
    public static (SKBitmap Image, RawSettings Applied) Develop(ReadOnlyMemory<byte> data, RawSettings settings, double scale = 1, long? maxPixels = null)
    {
        var (pixels, w, h, applied) = DevelopPixels(data, settings, scale, maxPixels);
        return (LinearImages.Bitmap(pixels, w, h), applied);
    }

    /// <summary>White balance from something that should be grey: the temperature and tint that make the pixel at
    /// <paramref name="point"/> (in the developed picture's pixels, from the top left) neutral.</summary>
    public static (double Temperature, double Tint) WhiteBalance(ReadOnlyMemory<byte> data, RawSettings settings, Vec2 point, long? maxPixels = null)
    {
        _ = Applied(settings);
        var (pixels, w, h, t0, tint0) = AsShotPicture(data, maxPixels);
        int x = (int)Math.Clamp(point.X, 0, w - 1), y = (int)Math.Clamp(point.Y, 0, h - 1);
        long i = ((long)y * w + x) * 4;
        float a = pixels[i + 3];
        if (!(a > 0)) return (Math.Clamp(settings.Temperature ?? t0, 2_000, 50_000), Math.Clamp(settings.Tint ?? tint0, -150, 150));
        var colour = new Vector3(pixels[i], pixels[i + 1], pixels[i + 2]) / a;
        var (temperature, tint) = RawColor.Neutral(colour, t0, tint0);
        return (Math.Clamp(temperature, 2_000, 50_000), Math.Clamp(tint, -150, 150));
    }

    /// <summary>The largest side of a draft, in pixels.</summary>
    public const int DraftSide = 1600;

    /// <summary>The camera models this computer's decoder knows, for showing in the app instead of listing them in the
    /// manual (empty when the decoder doesn't say).</summary>
    public static IReadOnlyList<string> Cameras =>
        Current is IPlatformRawDecoder r ? [.. r.Cameras.Order(StringComparer.Ordinal)] : [];

    // MARK: Drafts

    internal sealed record DraftPicture(float[] Pixels, int Width, int Height, double Temperature, double Tint);

    internal static DraftPicture DraftSource(ReadOnlyMemory<byte> data, int maxSide, long? maxPixels, out double scale)
    {
        var (pixels, w, h, t0, tint0) = AsShotPicture(data, maxPixels);
        scale = Math.Min(1, (double)maxSide / Math.Max(w, h));
        if (scale >= 1) return new DraftPicture(pixels, w, h, t0, tint0);
        int sw = Math.Max(1, (int)Math.Round(w * scale, MidpointRounding.AwayFromZero)), sh = Math.Max(1, (int)Math.Round(h * scale, MidpointRounding.AwayFromZero));
        var halves = new Half[pixels.LongLength];
        TensorPrimitives.ConvertToHalf(pixels, halves);
        using var full = LinearImages.Bitmap(halves, w, h);
        using var source = LinearPixels.Of(full);
        var small = LinearResample.Scale(source, (0, 0, w, h), sw, sh);
        var floats = new float[small.LongLength];
        TensorPrimitives.ConvertToSingle(small, floats);
        return new DraftPicture(floats, sw, sh, t0, tint0);
    }

    internal static Half[] DevelopDraft(DraftPicture source, RawSettings settings) =>
        Developed(source.Pixels, source.Width, source.Height, Applied(settings), source.Temperature, source.Tint);
}

/// <summary>
/// What a request to develop may change, checked against what the file allows. For the agent API's
/// <c>develop_raw</c>: a value outside its range is refused with the range, never clamped, and a control the file doesn't
/// have is refused by name.
/// </summary>
public static class RawRequest
{
    public sealed class Refusal(string reason, string message) : Exception(message)
    {
        public string Reason { get; } = reason;
    }

    public sealed record Control(string Name, (double Lower, double Upper) Range, bool Supported, double AsShot);

    /// <summary>Every number a request can set, with its range for this file.</summary>
    public static IReadOnlyList<Control> Controls(RawInfo info) =>
    [
        new("exposure", (-5, 5), true, 0),
        new("temperature", (2_000, 50_000), true, info.Temperature),
        new("tint", (-150, 150), true, info.Tint),
        new("shadows", (0, 2), true, 1),
        new("contrast", (0, 1), info.SupportsContrast, info.Contrast),
        new("sharpness", (0, 1), info.SupportsSharpness, info.Sharpness),
        new("luminanceNoise", (0, 1), info.SupportsLuminanceNoise, info.LuminanceNoise),
        new("colorNoise", (0, 1), info.SupportsColorNoise, info.ColorNoise),
    ];

    /// <summary>The two switches, with whether the file has them.</summary>
    public static IReadOnlyList<(string Name, bool Supported, bool AsShot)> Switches(RawInfo info) =>
        [("highlightRecovery", info.SupportsHighlightRecovery, true), ("lensCorrection", info.SupportsLensCorrection, info.LensCorrection)];

    /// <summary><paramref name="current"/> with the request's values put in. Only what is given changes;
    /// <paramref name="asShot"/> first goes back to the camera's white balance and the development's own values (the
    /// process stays).</summary>
    public static RawSettings Settings(RawSettings current, RawInfo info, IReadOnlyDictionary<string, double> numbers,
                                       IReadOnlyDictionary<string, bool> switches, bool asShot)
    {
        var output = asShot ? new RawSettings { Process = current.Process } : current;
        var known = Controls(info);
        foreach (var (name, value) in numbers.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (known.FirstOrDefault(c => c.Name == name) is not { } control)
                throw new Refusal("unknown_control", $"develop_raw has no control named {name}. It has: {string.Join(", ", known.Select(c => c.Name))}, highlightRecovery, lensCorrection.");
            if (!control.Supported)
                throw new Refusal("unsupported_control", $"This RAW file has no {name} control: Glimmer doesn't offer it on this computer.");
            if (!double.IsFinite(value) || value < control.Range.Lower || value > control.Range.Upper)
                throw new Refusal("out_of_range", $"{name} must be {Number(control.Range.Lower)}…{Number(control.Range.Upper)} for this file; {Number(value)} is outside that.");
            output = name switch
            {
                "exposure" => output with { Exposure = value },
                "temperature" => output with { Temperature = value },
                "tint" => output with { Tint = value },
                "shadows" => output with { Shadows = value },
                "contrast" => output with { Contrast = value },
                "sharpness" => output with { Sharpness = value },
                "luminanceNoise" => output with { LuminanceNoise = value },
                _ => output with { ColorNoise = value },
            };
        }
        foreach (var (name, value) in switches.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var match = Switches(info).Where(s => s.Name == name).ToList();
            if (match.Count == 0) throw new Refusal("unknown_control", $"develop_raw has no switch named {name}.");
            if (!match[0].Supported) throw new Refusal("unsupported_control", $"This RAW file has no {name} switch: Glimmer doesn't offer it on this computer.");
            output = name == "highlightRecovery" ? output with { HighlightRecovery = value } : output with { LensCorrection = value };
        }
        return output;
    }

    private static string Number(double v) =>
        v == Math.Round(v) && Math.Abs(v) < 1e15 ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("F2", CultureInfo.InvariantCulture);
}

/// <summary>
/// The picture while a control is being moved. The as-shot picture is decoded once, when the Develop sheet opens, and
/// kept at the draft's size; each change develops that small picture, which is what the finished development gives,
/// scaled (on smooth areas exactly; at edges within the resampling's rounding). Not for use from several threads at once.
/// </summary>
public sealed class RawDraft
{
    private readonly RawDevelopment.DraftPicture source;

    public double Scale { get; }
    /// <summary>The draft's size in pixels.</summary>
    public int Width => source.Width;
    public int Height => source.Height;

    public RawDraft(ReadOnlyMemory<byte> data, int maxSide = RawDevelopment.DraftSide, long? maxPixels = null)
    {
        source = RawDevelopment.DraftSource(data, maxSide, maxPixels, out double scale);
        Scale = scale;
    }

    /// <summary>Develops once at <paramref name="settings"/> and throws the picture away, so that the first change a
    /// person makes doesn't pay for the work's first run.</summary>
    public void WarmUp(RawSettings settings)
    {
        try { _ = RawDevelopment.DevelopDraft(source, settings); }
        catch (RawDevelopment.Problem) { }
    }

    /// <summary>The draft for these settings, in sRGB with 8 bits per channel (premultiplied), for showing.</summary>
    public SKBitmap Picture(RawSettings settings)
    {
        var pixels = RawDevelopment.DevelopDraft(source, settings);
        using var linear = LinearImages.Bitmap(pixels, Width, Height);
        return ImageExporter.SrgbPremultiplied(linear);
    }
}

/// <summary>New: whether a file's header is one of the camera RAW formats, for a system decoder that can't say
/// (IPlatformRawDecoder.IsRaw).</summary>
internal static class RawContainer
{
    public static bool Looks(ReadOnlySpan<byte> d)
    {
        if (d.Length < 16) return false;
        if (d.StartsWith("FUJIFILMCCD-RAW"u8) || d.StartsWith("FOVb"u8) || d.StartsWith((ReadOnlySpan<byte>)[0, (byte)'M', (byte)'R', (byte)'M'])) return true;
        if (d.Length >= 12 && d.Slice(4, 4).SequenceEqual("ftyp"u8) && d.Slice(8, 4).SequenceEqual("crx "u8)) return true;   // Canon CR3
        if (d.StartsWith("IIRO"u8) || d.StartsWith("IIRS"u8) || d.StartsWith("MMOR"u8)) return true;                         // Olympus ORF
        if (d.StartsWith((ReadOnlySpan<byte>)[(byte)'I', (byte)'I', (byte)'U', 0])) return true;                              // Panasonic RW2
        bool little = d.StartsWith((ReadOnlySpan<byte>)[(byte)'I', (byte)'I', 42, 0]), big = d.StartsWith((ReadOnlySpan<byte>)[(byte)'M', (byte)'M', 0, 42]);
        if (!little && !big) return false;
        if (d[8] == 'C' && d[9] == 'R') return true;                                                                            // Canon CR2
        return TiffHoldsRaw(d, little);
    }

    /// <summary>A TIFF-based RAW file (DNG, NEF, ARW, PEF, SRW …): a DNG version tag, or an image directory whose
    /// photometric interpretation is a colour filter array or linear raw. Directories are followed to a small depth.</summary>
    private static bool TiffHoldsRaw(ReadOnlySpan<byte> d, bool little)
    {
        uint U16(ReadOnlySpan<byte> s, long at) => at + 2 > s.Length ? 0u
            : little ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(s[(int)at..]) : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(s[(int)at..]);
        uint U32(ReadOnlySpan<byte> s, long at) => at + 4 > s.Length ? 0u
            : little ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s[(int)at..]) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(s[(int)at..]);
        var pending = new Stack<(long Offset, int Depth)>();
        pending.Push((U32(d, 4), 0));
        int visited = 0;
        while (pending.Count > 0 && visited < 32)
        {
            var (ifd, depth) = pending.Pop();
            visited += 1;
            if (ifd < 8 || ifd + 2 > d.Length) continue;
            uint count = U16(d, ifd);
            if (count == 0 || count > 1000 || ifd + 2 + count * 12L > d.Length) continue;
            for (int i = 0; i < count; i++)
            {
                long entry = ifd + 2 + i * 12L;
                uint tag = U16(d, entry), type = U16(d, entry + 2), n = U32(d, entry + 4);
                if (tag == 50706) return true;                                                     // DNGVersion
                if (tag == 262 && (U16(d, entry + 8) is 32803 or 34892)) return true;              // CFA or LinearRaw
                if (tag == 330 && depth < 3 && (type is 4 or 13))                                  // SubIFDs
                {
                    if (n == 1) pending.Push((U32(d, entry + 8), depth + 1));
                    else
                    {
                        long at = U32(d, entry + 8);
                        for (int k = 0; k < Math.Min(n, 8u); k++) pending.Push((U32(d, at + k * 4), depth + 1));
                    }
                }
            }
            if (depth == 0) pending.Push((U32(d, ifd + 2 + count * 12L), 0));   // the next directory
        }
        return false;
    }
}
#endif
