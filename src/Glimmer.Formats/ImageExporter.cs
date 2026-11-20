// Ported from Packages/GlimmerKit/Sources/GlimmerRender/ImageExporter.swift. The Mac's CGImage is an SKBitmap here, as
// in the rest of the port. CoreGraphics' colour conversion is Glimmer's own (Convert, below); ImageIO's encoders are
// SkiaSharp's for 8-bit PNG, JPEG and WebP (libwebp, where the Mac has its own WebP encoder), Glimmer's PngWriter for
// 16-bit PNG, Glimmer's TiffWriter for TIFF, and the operating system's for HEIC (IPlatformImageEncoder).
using System.Numerics;
using System.Numerics.Tensors;
using Glimmer.Core;
using Glimmer.Render;
using SkiaSharp;

namespace Glimmer.Formats;

/// <summary>ExportOptions.Format (a nested type can't share the property's name in C#).</summary>
public enum ExportFormat
{
    [RawValue("png")] Png,
    [RawValue("jpeg")] Jpeg,
    [RawValue("heic")] Heic,
    [RawValue("tiff")] Tiff,
    [RawValue("webp")] Webp,
}

/// <summary>ExportOptions.BitDepth.</summary>
public enum ExportBitDepth
{
    Eight = 8,
    Sixteen = 16,
    /// <summary>32-bit float, linear light (HDR values above 1 are kept).</summary>
    Float32 = 32,
}

public static class ExportFormatExtensions
{
    public static string RawValue(this ExportFormat format) => RawValues<ExportFormat>.Of(format);

    public static ExportFormat? FormatFromRawValue(string? raw) => RawValues<ExportFormat>.Parse(raw);

    public static string Id(this ExportFormat format) => format.RawValue();

    public static string Title(this ExportFormat format) => format switch
    {
        ExportFormat.Png => "PNG",
        ExportFormat.Jpeg => "JPEG",
        ExportFormat.Heic => "HEIC",
        ExportFormat.Tiff => "TIFF",
        _ => "WebP",
    };

    /// <summary>The usual file name extension (the Mac's contentType.preferredFilenameExtension).</summary>
    public static string FileExtension(this ExportFormat format) => format.RawValue();

    /// <summary>The media type (the Mac's contentType).</summary>
    public static string MimeType(this ExportFormat format) => "image/" + format.RawValue();

    public static bool SupportsAlpha(this ExportFormat format) => format != ExportFormat.Jpeg;

    /// <summary>Bit depths the format can store (WebP is written 8-bit).</summary>
    public static IReadOnlyList<ExportBitDepth> BitDepths(this ExportFormat format) => format switch
    {
        ExportFormat.Png => [ExportBitDepth.Eight, ExportBitDepth.Sixteen],
        ExportFormat.Tiff => [ExportBitDepth.Eight, ExportBitDepth.Sixteen, ExportBitDepth.Float32],
        _ => [ExportBitDepth.Eight],
    };

    public static bool Supports16Bit(this ExportFormat format) => format.BitDepths().Contains(ExportBitDepth.Sixteen);

    public static bool IsLossy(this ExportFormat format) => format is ExportFormat.Jpeg or ExportFormat.Heic;

    /// <summary>Shows the quality slider (WebP is lossless at 100 % and lossy below).</summary>
    public static bool HasQuality(this ExportFormat format) => format.IsLossy() || format == ExportFormat.Webp;
}

public static class ExportBitDepthExtensions
{
    public static int Id(this ExportBitDepth depth) => (int)depth;

    public static string Title(this ExportBitDepth depth) => depth switch
    {
        ExportBitDepth.Eight => "8-bit",
        ExportBitDepth.Sixteen => "16-bit",
        _ => "32-bit float (linear)",
    };
}

public sealed record ExportOptions
{
    public ExportOptions(ExportFormat format = ExportFormat.Png, ExportBitDepth bitDepth = ExportBitDepth.Eight, double quality = 0.92,
                         WorkingColorSpace colorSpace = WorkingColorSpace.SRGB, Vec3? matte = null, int? maxPixelSide = null,
                         int? maxBytes = null)
    {
        Format = format;
        BitDepth = bitDepth;
        Quality = quality;
        ColorSpace = colorSpace;
        Matte = matte ?? new Vec3(1, 1, 1);
        MaxPixelSide = maxPixelSide;
        MaxBytes = maxBytes;
    }

    public ExportFormat Format { get; init; }
    public ExportBitDepth BitDepth { get; init; }

    /// <summary>The depth actually written: the requested one, or the deepest the format supports below it.</summary>
    public ExportBitDepth EffectiveBitDepth => Format.BitDepths().LastOrDefault(d => (int)d <= (int)BitDepth, ExportBitDepth.Eight);

    /// <summary>0...1, lossy formats only.</summary>
    public double Quality { get; init; }
    public WorkingColorSpace ColorSpace { get; init; }
    /// <summary>Straight sRGB colour used behind transparent pixels for formats without alpha.</summary>
    public Vec3 Matte { get; init; }
    /// <summary>Scale down so the longer side is at most this many pixels (null = full size).</summary>
    public int? MaxPixelSide { get; init; }
    /// <summary>Lossy formats: lower the quality (and if need be the size) until the file fits in this many bytes (null
    /// = no limit).</summary>
    public int? MaxBytes { get; init; }

    /// <summary>Settings for the web: JPEG, sRGB, 2048 px, under 500 kB.</summary>
    public static ExportOptions Web { get; } = new(ExportFormat.Jpeg, quality: 0.85, maxPixelSide: 2048, maxBytes: 500_000);
}

/// <summary>A fixed-size export can't get under its byte limit, even at the lowest quality.</summary>
public sealed class ExportLimitError(int smallest, int limit)
    : Exception($"Even at the lowest quality the image is {smallest} bytes, over the limit of {limit}.")
{
    public int Smallest { get; } = smallest;
    public int Limit { get; } = limit;
}

public static class ImageExporter
{
    /// <summary>The system's encoder for HEIC (the Windows app sets it). Without one, HEIC can't be written.</summary>
    public static IPlatformImageEncoder? PlatformEncoder { get; set; }

    /// <summary>True when this computer can write <paramref name="format"/> (all but HEIC always; HEIC when the system's
    /// encoder is there). The Mac writes all five.</summary>
    public static bool CanWrite(ExportFormat format) =>
        format != ExportFormat.Heic || PlatformEncoder?.CanEncode(format) == true;

    /// <summary>Flattens the document to a bitmap in linear extended sRGB (Float16, premultiplied).</summary>
    public static SKBitmap Flatten(DocumentState state)
    {
        var (buffer, _, rect) = new DocumentCompositor().RenderPixels(state);
        return LinearImages.Bitmap(buffer, rect.Width, rect.Height);
    }

    /// <summary>The document composite as 8-bit sRGB, premultiplied as the Mac's (what AI models receive).</summary>
    public static SKBitmap FlattenedSRGB(DocumentState state)
    {
        using var flat = Flatten(state);
        return SrgbPremultiplied(flat);
    }

    /// <summary>Any picture as 8-bit sRGB, premultiplied (the form of <see cref="FlattenedSRGB"/>).</summary>
    internal static SKBitmap SrgbPremultiplied(SKBitmap image) => Convert(image, new ExportOptions(ExportFormat.Png), premultiplied: true);

    /// <summary>One layer on its own (normal blending, full opacity, no mask) over the document bounds.</summary>
    public static SKBitmap LayerImageSRGB(Layer layer, DocumentState state)
    {
        var solo = layer with
        {
            IsVisible = true,
            Opacity = 1,
            BlendMode = layer.IsGroup ? BlendMode.PassThrough : BlendMode.Normal,
            Mask = null,
            IsClipped = false,
        };
        return FlattenedSRGB(state with { Layers = [solo] });
    }

    /// <summary>
    /// Converts an image (any bitmap; the flattened one is linear) to the export colour space and bit depth, and
    /// flattens it over the matte when the format has no alpha. Integer depths come out gamma-encoded with straight
    /// alpha, which is what PNG, TIFF and WebP store (the Mac's are premultiplied and its encoders divide again; here
    /// soft edges keep their colour exactly); 32-bit float stays linear, premultiplied and unclamped.
    /// </summary>
    public static SKBitmap Convert(SKBitmap image, ExportOptions options) => Convert(image, options, premultiplied: false);

    private static unsafe SKBitmap Convert(SKBitmap image, ExportOptions options, bool premultiplied)
    {
        var depth = options.EffectiveBitDepth;
        bool isFloat = depth == ExportBitDepth.Float32;
        bool keepAlpha = options.Format.SupportsAlpha();
        using var source = LinearPixels.Of(image);
        int w = source.Width, h = source.Height;
        var type = depth switch
        {
            ExportBitDepth.Eight => SKColorType.Rgba8888,
            ExportBitDepth.Sixteen => SKColorType.Rgba16161616,
            _ => SKColorType.RgbaF32,
        };
        var alpha = !keepAlpha ? SKAlphaType.Opaque : isFloat || premultiplied ? SKAlphaType.Premul : SKAlphaType.Unpremul;
        // Float output stays linear and unclamped; integer output is gamma-encoded.
        var info = new SKImageInfo(w, h, type, alpha, IccProfile.ColorSpace(options.ColorSpace, linear: isFloat));
        var result = new SKBitmap();
        if (!result.TryAllocPixels(info))
        {
            result.Dispose();
            throw RenderError.ResourceCreationFailed("export context");
        }
        var toP3 = options.ColorSpace == WorkingColorSpace.DisplayP3 ? P3FromSrgb.Value : null;
        var matte = MatteIn(options);
        IntPtr destination = result.GetPixels();
        long rowBytes = result.RowBytes;
        bool multiply = keepAlpha && premultiplied;
        Parallel.For(0, h, () => new float[w * 4], (y, _, row) =>
        {
            TensorPrimitives.ConvertToSingle(source.Row(y), row);
            for (int i = 0; i < row.Length; i += 4)
            {
                float a = Finite(row[i + 3]);
                a = Math.Clamp(a, 0f, 1f);
                var c = new Vector3(Finite(row[i]), Finite(row[i + 1]), Finite(row[i + 2]));
                if (isFloat)
                {
                    if (toP3 is not null) c = Apply(toP3, c);
                }
                else
                {
                    c = a > 0 ? c / a : Vector3.Zero;
                    if (toP3 is not null) c = Apply(toP3, c);
                    c = Vector3.Clamp(c, Vector3.Zero, Vector3.One);
                    c = new Vector3(SrgbTransfer.EncodeFast(c.X), SrgbTransfer.EncodeFast(c.Y), SrgbTransfer.EncodeFast(c.Z));
                    if (!keepAlpha)
                    {
                        // Over the matte, in the encoded values, as CoreGraphics blends in a gamma-encoded context.
                        c = c * a + matte * (1 - a);
                        a = 1;
                    }
                    else if (multiply)
                    {
                        c *= a;
                    }
                }
                row[i] = c.X;
                row[i + 1] = c.Y;
                row[i + 2] = c.Z;
                row[i + 3] = a;
            }
            var target = (byte*)destination + y * rowBytes;
            switch (depth)
            {
                case ExportBitDepth.Eight:
                    for (int i = 0; i < row.Length; i++) target[i] = (byte)(row[i] * 255f + 0.5f);
                    break;
                case ExportBitDepth.Sixteen:
                    var words = (ushort*)target;
                    for (int i = 0; i < row.Length; i++) words[i] = (ushort)(row[i] * 65535f + 0.5f);
                    break;
                default:
                    row.AsSpan().CopyTo(new Span<float>(target, row.Length));
                    break;
            }
            return row;
        }, _ => { });
        return result;
    }

    private static float Finite(float v) => float.IsFinite(v) ? v : (float.IsNaN(v) ? 0 : Math.Clamp(v, -65504f, 65504f));

    private static Vector3 Apply(float[] m, Vector3 c) =>
        new(m[0] * c.X + m[1] * c.Y + m[2] * c.Z, m[3] * c.X + m[4] * c.Y + m[5] * c.Z, m[6] * c.X + m[7] * c.Y + m[8] * c.Z);

    /// <summary>Linear sRGB to linear Display P3, through Skia's D50-adapted matrices (row-major).</summary>
    internal static readonly Lazy<float[]> P3FromSrgb = new(() =>
    {
        static double[] Rows(SKColorSpaceXyz m) => [.. m.Values.Select(v => (double)v)];
        var m = SourceColor.Multiply(SourceColor.Invert(Rows(SKColorSpaceXyz.DisplayP3)), Rows(SKColorSpaceXyz.Srgb));
        return [.. m.Select(v => (float)v)];
    });

    /// <summary>The matte in the output's encoded values.</summary>
    private static Vector3 MatteIn(ExportOptions options)
    {
        var m = options.Matte;
        var c = new Vector3((float)Math.Clamp(m.X, 0, 1), (float)Math.Clamp(m.Y, 0, 1), (float)Math.Clamp(m.Z, 0, 1));
        if (options.ColorSpace != WorkingColorSpace.DisplayP3) return c;
        var linear = Apply(P3FromSrgb.Value, new Vector3(SrgbTransfer.Decode(c.X), SrgbTransfer.Decode(c.Y), SrgbTransfer.Decode(c.Z)));
        linear = Vector3.Clamp(linear, Vector3.Zero, Vector3.One);
        return new Vector3(SrgbTransfer.Encode(linear.X), SrgbTransfer.Encode(linear.Y), SrgbTransfer.Encode(linear.Z));
    }

    /// <summary>
    /// Encodes <paramref name="image"/>, honouring MaxPixelSide and MaxBytes. With a byte limit, the quality is found by
    /// bisection; when even a low quality is too big, the image is scaled down too, so the limit is always met when
    /// possible. With <paramref name="keepSize"/>, the size is fixed (a podcast cover must stay 3000 × 3000): only the
    /// quality goes down, and <see cref="ExportLimitError"/> says when that isn't enough.
    /// </summary>
    public static byte[] Encode(SKBitmap image, ExportOptions options, bool keepSize)
    {
        if (!keepSize || options.MaxBytes is not { } limit || limit <= 0) return Encode(image, options);
        var opts = options with { MaxPixelSide = null };
        // Converted once: only the quality changes between the tries.
        using var converted = Convert(image, opts);
        if (!options.Format.HasQuality())
        {
            var data = EncodeConverted(converted, opts);
            if (data.Length > limit) throw new ExportLimitError(data.Length, limit);
            return data;
        }
        opts = opts with { Quality = Math.Min(options.Quality, 0.98) };
        var first = EncodeConverted(converted, opts);
        if (first.Length <= limit) return first;
        double lo = 0.05, hi = opts.Quality;
        var best = EncodeConverted(converted, opts with { Quality = lo });
        if (best.Length > limit) throw new ExportLimitError(best.Length, limit);
        for (int i = 0; i < 7; i++)
        {
            double q = (lo + hi) / 2;
            var data = EncodeConverted(converted, opts with { Quality = q });
            if (data.Length <= limit) { best = data; lo = q; } else hi = q;
        }
        return best;
    }

    /// <summary>
    /// The centre square of <paramref name="image"/>, resampled in linear light to exactly <paramref name="side"/> ×
    /// <paramref name="side"/> (up or down): cover art for podcasts and albums. <paramref name="background"/> (straight
    /// sRGB) fills transparent areas, since cover art is opaque.
    /// </summary>
    public static SKBitmap SquareCover(SKBitmap image, int side, Vec3? background = null)
    {
        int edge = Math.Min(image.Width, image.Height);
        if (side <= 0 || edge <= 0) throw RenderError.ResourceCreationFailed("cover context");
        using var source = LinearPixels.Of(image);
        var pixels = LinearResample.Scale(source, ((image.Width - edge) / 2, (image.Height - edge) / 2, edge, edge), side, side);
        if (background is { } b)
        {
            var under = new Vector3((float)ColorMath.SrgbToLinear(Math.Clamp(b.X, 0, 1)), (float)ColorMath.SrgbToLinear(Math.Clamp(b.Y, 0, 1)),
                                    (float)ColorMath.SrgbToLinear(Math.Clamp(b.Z, 0, 1)));
            Parallel.For(0, side, y =>
            {
                var row = pixels.AsSpan((int)((long)y * side * 4), side * 4);
                for (int i = 0; i < row.Length; i += 4)
                {
                    float a = (float)row[i + 3];
                    if (a >= 1) continue;
                    float k = 1 - a;
                    row[i] = (Half)((float)row[i] + under.X * k);
                    row[i + 1] = (Half)((float)row[i + 1] + under.Y * k);
                    row[i + 2] = (Half)((float)row[i + 2] + under.Z * k);
                    row[i + 3] = (Half)1f;
                }
            });
        }
        return LinearImages.Bitmap(pixels, side, side);
    }

    public static byte[] Encode(SKBitmap image, ExportOptions options)
    {
        var owned = new List<SKBitmap>();
        try
        {
            var source = image;
            if (options.MaxPixelSide is { } side && side > 0 && Math.Max(image.Width, image.Height) > side)
                owned.Add(source = Resized(image, side));
            if (options.MaxBytes is not { } limit || limit <= 0 || !options.Format.HasQuality()) return EncodeOnce(source, options);
            var opts = options with { Quality = Math.Min(options.Quality, 0.98) };  // WebP at 100 % is lossless, never smaller
            byte[]? smallest = null;
            for (int round = 0; round < 4; round++)
            {
                byte[]? best = null;
                byte[] floor;
                using (var converted = Convert(source, opts))
                {
                    var first = EncodeConverted(converted, opts);
                    if (first.Length <= limit) return first;
                    double lo = 0.05, hi = opts.Quality;
                    floor = EncodeConverted(converted, opts with { Quality = lo });
                    if (floor.Length <= limit)
                    {
                        best = floor;
                        for (int i = 0; i < 6; i++)
                        {
                            double q = (lo + hi) / 2;
                            var data = EncodeConverted(converted, opts with { Quality = q });
                            if (data.Length <= limit) { best = data; lo = q; } else hi = q;
                        }
                    }
                }
                if (best is not null) return best;
                if (floor.Length < (smallest?.Length ?? int.MaxValue)) smallest = floor;
                // Too big even at low quality: shrink and try again.
                double k = Math.Max(0.3, Math.Min(0.9, Math.Sqrt((double)limit / floor.Length) * 0.95));
                int newSide = Math.Max(16, (int)(Math.Max(source.Width, source.Height) * k));
                var smaller = Resized(source, newSide);
                if (!ReferenceEquals(smaller, source)) owned.Add(smaller);
                source = smaller;
            }
            return smallest ?? EncodeOnce(source, opts);
        }
        finally
        {
            foreach (var bitmap in owned) bitmap.Dispose();
        }
    }

    /// <summary><paramref name="image"/> scaled so its longer side is <paramref name="maxSide"/>, resampled in linear
    /// light (Float16, premultiplied). Returns <paramref name="image"/> itself when it is no larger.</summary>
    public static SKBitmap Resized(SKBitmap image, int maxSide)
    {
        double k = (double)maxSide / Math.Max(image.Width, image.Height);
        if (!(k < 1)) return image;
        int w = Math.Max(1, (int)Math.Round(image.Width * k, MidpointRounding.AwayFromZero));
        int h = Math.Max(1, (int)Math.Round(image.Height * k, MidpointRounding.AwayFromZero));
        using var source = LinearPixels.Of(image);
        return LinearImages.Bitmap(LinearResample.Scale(source, (0, 0, image.Width, image.Height), w, h), w, h);
    }

    internal static byte[] EncodeOnce(SKBitmap image, ExportOptions options)
    {
        using var converted = Convert(image, options);
        return EncodeConverted(converted, options);
    }

    /// <summary>Encodes what <see cref="Convert(SKBitmap, ExportOptions)"/> made with the same options.</summary>
    private static byte[] EncodeConverted(SKBitmap converted, ExportOptions options)
    {
        using var stream = new MemoryStream();
        WriteConverted(converted, options, stream);
        return stream.ToArray();
    }

    /// <summary>Writes what <see cref="Convert(SKBitmap, ExportOptions)"/> made with the same options to
    /// <paramref name="destination"/>. (TIFF and 16-bit PNG are written as they are made, so a file larger than an
    /// array can hold is still written when it goes straight to a file.)</summary>
    private static unsafe void WriteConverted(SKBitmap converted, ExportOptions options, Stream destination)
    {
        int w = converted.Width, h = converted.Height;
        var depth = options.EffectiveBitDepth;
        bool p3 = options.ColorSpace == WorkingColorSpace.DisplayP3;
        byte* pixels = (byte*)converted.GetPixels();
        long rowBytes = converted.RowBytes;
        switch (options.Format)
        {
            case ExportFormat.Png when depth == ExportBitDepth.Sixteen:
                PngWriter.Write16(destination, w, h, (y, row) => new ReadOnlySpan<ushort>(pixels + y * rowBytes, row.Length).CopyTo(row),
                                  p3 ? IccProfile.Rgb(WorkingColorSpace.DisplayP3) : default);
                break;
            case ExportFormat.Png:
                Skia(converted, pixmap => pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)), "PNG", destination);
                break;
            case ExportFormat.Jpeg:
            {
                int quality = (int)Math.Clamp(Math.Round(options.Quality * 100, MidpointRounding.AwayFromZero), 1, 100);
                Skia(converted, pixmap => pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420,
                                                                                 SKJpegEncoderAlphaOption.Ignore)), "JPEG", destination);
                break;
            }
            case ExportFormat.Webp:
            {
                // 100 % is lossless (VP8L); anything lower is lossy (VP8). The profile is embedded for Display P3 only.
                var rgba = new ReadOnlySpan<byte>(pixels, checked((int)(rowBytes * h)));
                var space = p3 ? IccProfile.ColorSpace(WorkingColorSpace.DisplayP3) : null;
                destination.Write(options.Quality >= 0.995 ? WebPEncoder.Encode(rgba, w, h, space) : WebPEncoder.EncodeLossy(rgba, w, h, options.Quality, space));
                break;
            }
            case ExportFormat.Tiff:
            {
                bool isFloat = depth == ExportBitDepth.Float32;
                var format = depth switch
                {
                    ExportBitDepth.Eight => TiffWriter.SampleFormat.UInt8,
                    ExportBitDepth.Sixteen => TiffWriter.SampleFormat.UInt16,
                    _ => TiffWriter.SampleFormat.Float32,
                };
                int bytes = w * 4 * (depth == ExportBitDepth.Eight ? 1 : depth == ExportBitDepth.Sixteen ? 2 : 4);
                TiffWriter.Write(destination, w, h, format, isFloat ? TiffWriter.Alpha.Associated : TiffWriter.Alpha.Unassociated,
                                 (y, row) => new ReadOnlySpan<byte>(pixels + y * rowBytes, bytes).CopyTo(row),
                                 new TiffWriter.Options { IccProfile = IccProfile.Rgb(options.ColorSpace, linear: isFloat) });
                break;
            }
            default:
            {
                if (PlatformEncoder is not { } encoder || !encoder.CanEncode(options.Format))
                    throw RenderError.ResourceCreationFailed($"no encoder for {options.Format.Title()}");
                destination.Write(encoder.Encode(options.Format, new ReadOnlySpan<byte>(pixels, checked((int)(rowBytes * h))), w, h,
                                                 IccProfile.Rgb(options.ColorSpace), options.Quality));
                break;
            }
        }
        GC.KeepAlive(converted);
    }

    private static void Skia(SKBitmap converted, Func<SKPixmap, SKData?> encode, string title, Stream destination)
    {
        using var pixmap = converted.PeekPixels();
        using var data = encode(pixmap) ?? throw RenderError.ResourceCreationFailed($"encoding {title} failed");
        destination.Write(data.AsSpan());
    }

    public static void Export(DocumentState state, ExportOptions options, string path)
    {
        using var flat = Flatten(state);
        if (options.MaxBytes is > 0 && options.Format.HasQuality())
        {
            var data = Encode(flat, options);
            WriteAtomically(path, stream => stream.Write(data));
            return;
        }
        // Without a byte limit the file is written as it is made.
        SKBitmap? smaller = options.MaxPixelSide is { } side && side > 0 && Math.Max(flat.Width, flat.Height) > side ? Resized(flat, side) : null;
        try
        {
            using var converted = Convert(smaller ?? flat, options);
            WriteAtomically(path, stream => WriteConverted(converted, options, stream));
        }
        finally
        {
            smaller?.Dispose();
        }
    }

    /// <summary>Writes beside the destination and moves into place, so a failed write never leaves half a file.</summary>
    internal static void WriteAtomically(string path, Action<Stream> write)
    {
        string full = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(full) ?? ".", "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
