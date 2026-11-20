// New file, no Swift original: what CoreGraphics does for GlimmerRender/ImageExporter.swift (wrapping a Metal buffer
// as a CGImage, drawing any CGImage into a linear float context, high-quality interpolation) done for SKBitmaps.
using System.Numerics;
using System.Runtime.InteropServices;
using Glimmer.Render;
using SkiaSharp;

namespace Glimmer.Formats;

/// <summary>Bitmaps in the document's pixel format: Float16 RGBA, premultiplied, linear extended sRGB.</summary>
internal static class LinearImages
{
    public static SKColorSpace LinearSrgb { get; } = SKColorSpace.CreateSrgbLinear();

    /// <summary>A bitmap over <paramref name="pixels"/> (no copy): the array stays pinned until the bitmap is
    /// disposed or finalized.</summary>
    public static SKBitmap Bitmap(Half[] pixels, int width, int height)
    {
        if ((long)width * height * 4 > pixels.Length) throw new ArgumentException("Too few pixels for the size.", nameof(pixels));
        var info = new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul, LinearSrgb);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        var bitmap = new SKBitmap();
        if (!bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), width * 8, (_, context) => ((GCHandle)context!).Free(), handle))
        {
            handle.Free();
            bitmap.Dispose();
            throw RenderError.ResourceCreationFailed("linear image");
        }
        return bitmap;
    }

    /// <summary>A new transparent bitmap of the size, with its pixels.</summary>
    public static SKBitmap Make(int width, int height, out Half[] pixels)
    {
        pixels = new Half[(long)width * height * 4];
        return Bitmap(pixels, width, height);
    }

    public static bool IsLinearSrgb(SKColorSpace? space)
    {
        if (space is null || !space.GammaIsLinear || !space.ToColorSpaceXyz(out var xyz)) return false;
        var a = xyz.Values;
        var b = SKColorSpaceXyz.Srgb.Values;
        for (int i = 0; i < 9; i++) if (Math.Abs(a[i] - b[i]) > 1e-4f) return false;
        return true;
    }

    /// <summary>True when <paramref name="image"/> already is in the document's pixel format.</summary>
    public static bool IsLinear(SKBitmap image) =>
        image.ColorType == SKColorType.RgbaF16 && image.AlphaType == SKAlphaType.Premul && IsLinearSrgb(image.ColorSpace);
}

/// <summary>
/// An image's pixels as premultiplied linear extended-sRGB Halves: the bitmap's own memory when it is in that format,
/// otherwise a copy Skia converts (8-bit sRGB, Display P3 and the rest, as CoreGraphics converts a CGImage drawn into a
/// linear float context). A bitmap without a colour space is taken as sRGB.
/// </summary>
internal sealed unsafe class LinearPixels : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    private readonly IntPtr pixels;
    private readonly long rowBytes;
    private readonly GCHandle pin;
    private readonly SKBitmap image;

    private LinearPixels(SKBitmap image, IntPtr pixels, long rowBytes, GCHandle pin)
    {
        this.image = image;
        Width = image.Width;
        Height = image.Height;
        this.pixels = pixels;
        this.rowBytes = rowBytes;
        this.pin = pin;
    }

    public static LinearPixels Of(SKBitmap image)
    {
        if (image.Width < 1 || image.Height < 1 || image.GetPixels() == IntPtr.Zero) throw RenderError.ResourceCreationFailed("empty image");
        if (LinearImages.IsLinear(image)) return new LinearPixels(image, image.GetPixels(), image.RowBytes, default);
        var copy = new Half[(long)image.Width * image.Height * 4];
        var pin = GCHandle.Alloc(copy, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.RgbaF16, SKAlphaType.Premul, LinearImages.LinearSrgb);
            var sourceInfo = image.ColorSpace is null ? image.Info.WithColorSpace(SKColorSpace.CreateSrgb()) : image.Info;
            using var source = new SKPixmap(sourceInfo, image.GetPixels(), image.RowBytes);
            if (!source.ReadPixels(info, pin.AddrOfPinnedObject(), image.Width * 8)) throw RenderError.ResourceCreationFailed("image conversion");
            return new LinearPixels(image, pin.AddrOfPinnedObject(), image.Width * 8L, pin);
        }
        catch
        {
            pin.Free();
            throw;
        }
    }

    /// <summary>Row <paramref name="y"/>: R, G, B, A per pixel.</summary>
    public ReadOnlySpan<Half> Row(int y)
    {
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
        GC.KeepAlive(image);
        return new ReadOnlySpan<Half>((void*)(pixels + y * rowBytes), Width * 4);
    }

    public void Dispose()
    {
        if (pin.IsAllocated) pin.Free();
    }
}

/// <summary>
/// Lanczos (a = 3) resampling of premultiplied linear pixels, separable, edges clamped; downscaling widens the kernel by
/// the scale factor so it also filters. The kernel of Glimmer.Render's LanczosScale (which is internal to that
/// project), here over Halves and a source rectangle. CoreGraphics' "high" interpolation on the Mac is Apple's own
/// filter, so results differ from the Mac's in the last bits.
/// </summary>
internal static class LinearResample
{
    private const int A = 3;

    /// <summary>The <paramref name="crop"/> rectangle of <paramref name="source"/> scaled to
    /// <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static Half[] Scale(LinearPixels source, (int X, int Y, int Width, int Height) crop, int width, int height)
    {
        var horizontal = new float[(long)width * crop.Height * 4];
        var hw = Weights(crop.Width, width);
        Parallel.For(0, crop.Height, () => new float[crop.Width * 4], (y, _, row) =>
        {
            var src = source.Row(crop.Y + y).Slice(crop.X * 4, crop.Width * 4);
            System.Numerics.Tensors.TensorPrimitives.ConvertToSingle(src, row);
            var output = horizontal.AsSpan((int)((long)y * width * 4), width * 4);
            for (int x = 0; x < width; x++)
            {
                var (start, w) = hw[x];
                Vector4 acc = Vector4.Zero;
                for (int i = 0; i < w.Length; i++)
                {
                    int sx = Math.Clamp(start + i, 0, crop.Width - 1) * 4;
                    acc += new Vector4(row[sx], row[sx + 1], row[sx + 2], row[sx + 3]) * w[i];
                }
                output[x * 4] = acc.X;
                output[x * 4 + 1] = acc.Y;
                output[x * 4 + 2] = acc.Z;
                output[x * 4 + 3] = acc.W;
            }
            return row;
        }, _ => { });
        var result = new Half[(long)width * height * 4];
        var vw = Weights(crop.Height, height);
        Parallel.For(0, height, () => new float[width * 4], (y, _, acc) =>
        {
            Array.Clear(acc);
            var (start, w) = vw[y];
            for (int i = 0; i < w.Length; i++)
            {
                int sy = Math.Clamp(start + i, 0, crop.Height - 1);
                var row = horizontal.AsSpan((int)((long)sy * width * 4), width * 4);
                float k = w[i];
                for (int x = 0; x < acc.Length; x++) acc[x] += row[x] * k;
            }
            // Ringing can't make coverage: alpha stays in 0...1.
            for (int x = 3; x < acc.Length; x += 4) acc[x] = Math.Clamp(acc[x], 0f, 1f);
            System.Numerics.Tensors.TensorPrimitives.ConvertToHalf(acc, result.AsSpan((int)((long)y * width * 4), width * 4));
            return acc;
        }, _ => { });
        return result;
    }

    private static (int Start, float[] Weights)[] Weights(int srcSize, int dstSize)
    {
        double scale = (double)dstSize / srcSize;
        double support = scale < 1 ? A / scale : A;
        double stretch = scale < 1 ? scale : 1;
        var result = new (int, float[])[dstSize];
        for (int x = 0; x < dstSize; x++)
        {
            double centre = (x + 0.5) / scale - 0.5;
            int start = (int)Math.Floor(centre - support) + 1;
            int end = (int)Math.Ceiling(centre + support) - 1;
            var w = new double[end - start + 1];
            double sum = 0;
            for (int i = start; i <= end; i++)
            {
                double v = Kernel((i - centre) * stretch);
                w[i - start] = v;
                sum += v;
            }
            var f = new float[w.Length];
            for (int i = 0; i < w.Length; i++) f[i] = (float)(w[i] / sum);
            result[x] = (start, f);
        }
        return result;
    }

    private static double Kernel(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-12) return 1;
        if (x >= A) return 0;
        double px = Math.PI * x;
        return A * Math.Sin(px) * Math.Sin(px / A) / (px * px);
    }
}
