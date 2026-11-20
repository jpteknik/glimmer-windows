// Ported from Packages/GlimmerKit/Sources/GlimmerAI/Imaging.swift
using System.Numerics;
using Glimmer.Core;
using SkiaSharp;

namespace Glimmer.AI;

/// <summary>
/// Straight (non-premultiplied) 8-bit-range RGBA image as floats 0...255.
/// Mirrors the Pillow/numpy representation the web app's pipelines use, so the
/// ported maths behaves the same.
///
/// This is deliberately NOT the document's pixel format (premultiplied, linear, extended sRGB):
/// the pipelines' constants (colour-match clamps, Lab transfer, 8-bit truncation) are defined
/// on sRGB-encoded 0...255 values, exactly as in the Swift original. The boundary to the
/// document is <see cref="FromLinearPremultiplied"/> and <see cref="ToLinearPremultiplied"/>.
/// The Swift type is a value type; this one is a class, so every operation that returns an image
/// returns a new one and callers that want a copy to mutate use <see cref="Clone"/>.
/// </summary>
public sealed class RGBAImage : IEquatable<RGBAImage>
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Interleaved RGBA, row-major.</summary>
    public float[] Pixels { get; }

    public RGBAImage(int width, int height, float[] pixels)
    {
        if (pixels.Length != width * height * 4) throw new ArgumentException("pixels.Length must be width * height * 4");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public RGBAImage(int width, int height, Vector4 fill)
    {
        Width = width;
        Height = height;
        var p = new float[width * height * 4];
        for (int i = 0; i < p.Length; i += 4) { p[i] = fill.X; p[i + 1] = fill.Y; p[i + 2] = fill.Z; p[i + 3] = fill.W; }
        Pixels = p;
    }

    public RGBAImage Clone() => new(Width, Height, (float[])Pixels.Clone());

    public bool Equals(RGBAImage? other) =>
        other is not null && other.Width == Width && other.Height == Height && Pixels.AsSpan().SequenceEqual(other.Pixels);
    public override bool Equals(object? obj) => Equals(obj as RGBAImage);
    public override int GetHashCode() => HashCode.Combine(Width, Height, Pixels.Length > 0 ? Pixels[0] : 0);

    // MARK: Encoded images (ImageIO / CGImage in the original; SkiaSharp here)

    /// <summary>Decodes an encoded image (PNG, JPEG, WebP, …) to straight sRGB RGBA, as <c>init(_ image: CGImage)</c> does.</summary>
    public static RGBAImage Decode(ReadOnlySpan<byte> encoded)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("The image couldn't be read.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            throw new InvalidDataException("The image couldn't be read.");
        return FromSKBitmap(bitmap);
    }

    /// <summary>Converts any bitmap to straight sRGB RGBA.</summary>
    public static RGBAImage FromSKBitmap(SKBitmap source)
    {
        int w = source.Width, h = source.Height;
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var bytes = new byte[w * h * 4];
        // ReadPixels converts colour type, alpha type and colour space to what `info` says.
        using (var image = SKImage.FromBitmap(source))
        {
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                if (!image.ReadPixels(info, handle.AddrOfPinnedObject(), w * 4, 0, 0))
                    throw new InvalidDataException("The image couldn't be read.");
            }
            finally { handle.Free(); }
        }
        var p = new float[bytes.Length];
        for (int i = 0; i < bytes.Length; i += 4)
        {
            float a = bytes[i + 3];
            float k = a > 0 ? 255 / a : 0;  // un-premultiply
            p[i] = MathF.Min(255, bytes[i] * k);
            p[i + 1] = MathF.Min(255, bytes[i + 1] * k);
            p[i + 2] = MathF.Min(255, bytes[i + 2] * k);
            p[i + 3] = a;
        }
        return new RGBAImage(w, h, p);
    }

    /// <summary>The image as an 8-bit premultiplied sRGB bitmap (the original's <c>cgImage</c>).</summary>
    public SKBitmap ToSKBitmap()
    {
        var bytes = new byte[Pixels.Length];
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            float a = MathF.Min(MathF.Max(Pixels[i + 3], 0), 255) / 255;
            bytes[i] = Byte(Pixels[i] * a);
            bytes[i + 1] = Byte(Pixels[i + 1] * a);
            bytes[i + 2] = Byte(Pixels[i + 2] * a);
            bytes[i + 3] = (byte)Imaging.RoundAway(a * 255);
        }
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var bitmap = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
        return bitmap;

        static byte Byte(float v) => (byte)Imaging.RoundAway(MathF.Min(MathF.Max(v, 0), 255));
    }

    public byte[] PngData()
    {
        using var bitmap = ToSKBitmap();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // MARK: The document's pixel format (premultiplied, linear, extended sRGB)

    /// <summary>
    /// From the document's float RGBA (premultiplied, linear light), the format of a tile, to straight sRGB
    /// 0...255. Un-premultiplies, applies the sRGB transfer curve and clamps: values outside 0...1 (extended
    /// sRGB) do not survive, as they did not through the original's 8-bit bitmap.
    /// </summary>
    public static RGBAImage FromLinearPremultiplied(ReadOnlySpan<float> rgba, int width, int height)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("rgba.Length must be width * height * 4");
        var p = new float[rgba.Length];
        for (int i = 0; i < p.Length; i += 4)
        {
            float a = Math.Clamp(rgba[i + 3], 0f, 1f);
            if (a > 0)
            {
                p[i] = Imaging.SrgbEncode(rgba[i] / a) * 255;
                p[i + 1] = Imaging.SrgbEncode(rgba[i + 1] / a) * 255;
                p[i + 2] = Imaging.SrgbEncode(rgba[i + 2] / a) * 255;
            }
            p[i + 3] = a * 255;
        }
        return new RGBAImage(width, height, p);
    }

    /// <summary>The reverse of <see cref="FromLinearPremultiplied"/>: linear light, premultiplied, 0...1.</summary>
    public float[] ToLinearPremultiplied()
    {
        var o = new float[Pixels.Length];
        for (int i = 0; i < o.Length; i += 4)
        {
            float a = Math.Clamp(Pixels[i + 3], 0f, 255f) / 255;
            o[i] = Imaging.SrgbDecode(Pixels[i] / 255) * a;
            o[i + 1] = Imaging.SrgbDecode(Pixels[i + 1] / 255) * a;
            o[i + 2] = Imaging.SrgbDecode(Pixels[i + 2] / 255) * a;
            o[i + 3] = a;
        }
        return o;
    }

    // MARK: Geometry

    public RGBAImage Cropped(PixelRect r)
    {
        var rect = r.Intersection(new PixelRect(0, 0, Width, Height));
        var o = new float[rect.Width * rect.Height * 4];
        for (int y = 0; y < rect.Height; y++)
        {
            int src = ((rect.Y + y) * Width + rect.X) * 4;
            Array.Copy(Pixels, src, o, y * rect.Width * 4, rect.Width * 4);
        }
        return new RGBAImage(rect.Width, rect.Height, o);
    }

    /// <summary>Copies <paramref name="other"/> with its top-left at (x, y), overwriting (like Pillow <c>paste</c>). Mutates this image.</summary>
    public void Paste(RGBAImage other, int x, int y)
    {
        for (int row = 0; row < other.Height; row++)
        {
            int ty = y + row;
            if (ty < 0 || ty >= Height) continue;
            for (int col = 0; col < other.Width; col++)
            {
                int tx = x + col;
                if (tx < 0 || tx >= Width) continue;
                int s = (row * other.Width + col) * 4, d = (ty * Width + tx) * 4;
                Pixels[d] = other.Pixels[s]; Pixels[d + 1] = other.Pixels[s + 1];
                Pixels[d + 2] = other.Pixels[s + 2]; Pixels[d + 3] = other.Pixels[s + 3];
            }
        }
    }

    /// <summary>
    /// High-quality resample (never trust a model's output size). Lanczos-3 on premultiplied colour, as the
    /// original's high-quality context resample works on premultiplied pixels; widened by the shrink factor
    /// when reducing, so it filters rather than aliases.
    /// </summary>
    public RGBAImage Resized(int width, int height)
    {
        if (width == Width && height == Height) return this;
        int n = Width * Height;
        var pre = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            float a = Pixels[i * 4 + 3] / 255;
            pre[i * 4] = Pixels[i * 4] * a; pre[i * 4 + 1] = Pixels[i * 4 + 1] * a; pre[i * 4 + 2] = Pixels[i * 4 + 2] * a;
            pre[i * 4 + 3] = Pixels[i * 4 + 3];
        }
        var res = Lanczos.Resample(pre, Width, Height, width, height, 4);
        int m = width * height;
        var o = new float[m * 4];
        for (int i = 0; i < m; i++)
        {
            float a = Math.Clamp(res[i * 4 + 3], 0f, 255f);
            float k = a > 0 ? 255 / a : 0;  // un-premultiply
            o[i * 4] = MathF.Min(255, MathF.Max(0, res[i * 4]) * k);
            o[i * 4 + 1] = MathF.Min(255, MathF.Max(0, res[i * 4 + 1]) * k);
            o[i * 4 + 2] = MathF.Min(255, MathF.Max(0, res[i * 4 + 2]) * k);
            o[i * 4 + 3] = a;
        }
        return new RGBAImage(width, height, o);
    }

    /// <summary>Scales so the longer side is at most <paramref name="maxSide"/> (keeps aspect).</summary>
    public RGBAImage Limited(int maxSide)
    {
        int longest = Math.Max(Width, Height);
        if (longest <= maxSide) return this;
        double k = (double)maxSide / longest;
        return Resized(Math.Max(1, Imaging.RoundAway(Width * k)), Math.Max(1, Imaging.RoundAway(Height * k)));
    }

    /// <summary>Replaces the alpha channel with <paramref name="mask"/> (0...255).</summary>
    public RGBAImage WithAlpha(GrayImage mask)
    {
        var o = Clone();
        for (int i = 0; i < Width * Height; i++) o.Pixels[i * 4 + 3] = mask.Values[i];
        return o;
    }
}

/// <summary>Single-channel image as floats 0...255 (Pillow mode "L").</summary>
public sealed class GrayImage : IEquatable<GrayImage>
{
    public int Width { get; }
    public int Height { get; }
    public float[] Values { get; }

    public GrayImage(int width, int height, float[] values)
    {
        if (values.Length != width * height) throw new ArgumentException("values.Length must be width * height");
        Width = width;
        Height = height;
        Values = values;
    }

    public GrayImage(int width, int height, float fill)
        : this(width, height, Filled(width * height, fill)) { }

    static float[] Filled(int n, float v) { var a = new float[n]; Array.Fill(a, v); return a; }

    public GrayImage Clone() => new(Width, Height, (float[])Values.Clone());

    public bool Equals(GrayImage? other) =>
        other is not null && other.Width == Width && other.Height == Height && Values.AsSpan().SequenceEqual(other.Values);
    public override bool Equals(object? obj) => Equals(obj as GrayImage);
    public override int GetHashCode() => HashCode.Combine(Width, Height);

    /// <summary>Grey RGBA rendering (for sending masks to models).</summary>
    public RGBAImage Rgba()
    {
        var p = new float[Values.Length * 4];
        for (int i = 0; i < Values.Length; i++)
        {
            float v = Values[i];
            p[i * 4] = v; p[i * 4 + 1] = v; p[i * 4 + 2] = v; p[i * 4 + 3] = 255;
        }
        return new RGBAImage(Width, Height, p);
    }

    public GrayImage Inverted()
    {
        var o = new float[Values.Length];
        for (int i = 0; i < o.Length; i++) o[i] = 255 - Values[i];
        return new GrayImage(Width, Height, o);
    }

    /// <summary>Resample as an opaque grey RGBA image would be (the original goes through its RGBA resize).</summary>
    public GrayImage Resized(int width, int height)
    {
        if (width == Width && height == Height) return this;
        var r = Lanczos.Resample(Values, Width, Height, width, height, 1);
        for (int i = 0; i < r.Length; i++) r[i] = Math.Clamp(r[i], 0f, 255f);
        return new GrayImage(width, height, r);
    }

    /// <summary>Mutates this image.</summary>
    public void Paste(GrayImage other, int x, int y)
    {
        for (int row = 0; row < other.Height; row++)
        {
            if (y + row < 0 || y + row >= Height) continue;
            for (int col = 0; col < other.Width; col++)
            {
                if (x + col < 0 || x + col >= Width) continue;
                Values[(y + row) * Width + x + col] = other.Values[row * other.Width + col];
            }
        }
    }
}

/// <summary>Separable Lanczos-3 resampling of interleaved float channels (the Pillow formulation: support widened by the shrink factor).</summary>
static class Lanczos
{
    const double A = 3;

    static double Kernel(double x)
    {
        x = Math.Abs(x);
        if (x >= A) return 0;
        if (x < 1e-9) return 1;
        double px = Math.PI * x;
        return A * Math.Sin(px) * Math.Sin(px / A) / (px * px);
    }

    sealed class Taps
    {
        public int[] Start = [];
        public float[][] Weights = [];
    }

    static Taps Build(int srcSize, int dstSize)
    {
        double scale = (double)srcSize / dstSize;
        double filterScale = Math.Max(scale, 1);
        double support = A * filterScale;
        var t = new Taps { Start = new int[dstSize], Weights = new float[dstSize][] };
        for (int i = 0; i < dstSize; i++)
        {
            double center = (i + 0.5) * scale;
            int min = Math.Max(0, (int)Math.Floor(center - support + 0.5));
            int max = Math.Min(srcSize, (int)Math.Floor(center + support + 0.5));
            if (max <= min) { min = Math.Clamp((int)center, 0, srcSize - 1); max = min + 1; }
            var w = new double[max - min];
            double sum = 0;
            for (int j = 0; j < w.Length; j++)
            {
                w[j] = Kernel((j + min - center + 0.5) / filterScale);
                sum += w[j];
            }
            var f = new float[w.Length];
            for (int j = 0; j < w.Length; j++) f[j] = (float)(sum != 0 ? w[j] / sum : (j == w.Length / 2 ? 1 : 0));
            t.Start[i] = min;
            t.Weights[i] = f;
        }
        return t;
    }

    public static float[] Resample(float[] src, int sw, int sh, int dw, int dh, int ch)
    {
        var xt = Build(sw, dw);
        var yt = Build(sh, dh);
        // Horizontal pass: sh rows of dw pixels.
        var tmp = new float[sh * dw * ch];
        Imaging.ForRows(sh, sw * dw, y =>
        {
            int sRow = y * sw * ch, dRow = y * dw * ch;
            for (int x = 0; x < dw; x++)
            {
                var w = xt.Weights[x];
                int s0 = xt.Start[x];
                for (int c = 0; c < ch; c++)
                {
                    float acc = 0;
                    for (int j = 0; j < w.Length; j++) acc += src[sRow + (s0 + j) * ch + c] * w[j];
                    tmp[dRow + x * ch + c] = acc;
                }
            }
        });
        // Vertical pass.
        var o = new float[dw * dh * ch];
        int rowLen = dw * ch;
        Imaging.ForRows(dh, dw * sh, y =>
        {
            var w = yt.Weights[y];
            int s0 = yt.Start[y];
            int dRow = y * rowLen;
            for (int j = 0; j < w.Length; j++)
            {
                float wj = w[j];
                int sRow = (s0 + j) * rowLen;
                for (int i = 0; i < rowLen; i++) o[dRow + i] += tmp[sRow + i] * wj;
            }
        });
        return o;
    }
}


/// <summary>
/// The web app's image primitives (<c>workers/aips/pipelines/imaging.py</c>).
/// All of them work on <see cref="RGBAImage"/> / <see cref="GrayImage"/>: straight sRGB-encoded values 0...255,
/// as the original does (the colour match's gain/offset and clamps, the Lab transfer's sRGB→Lab, and the 8-bit
/// truncation in <see cref="BlendMasked"/> all assume that scale).
/// </summary>
public static class Imaging
{
    // MARK: Helpers

    /// <summary>Swift's <c>.rounded()</c>: halves away from zero.</summary>
    internal static int RoundAway(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
    internal static int RoundAway(float v) => (int)MathF.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>Runs <paramref name="body"/> for every row, in parallel when <paramref name="work"/> (a rough pixel count) is large enough to pay for it.</summary>
    internal static void ForRows(int rows, long work, Action<int> body)
    {
        if (work < 32_768 || rows < 2) { for (int y = 0; y < rows; y++) body(y); }
        else Parallel.For(0, rows, body);
    }

    internal static float SrgbEncode(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;
    }

    internal static float SrgbDecode(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    // MARK: Feather

    /// <summary>Erode (min filter) then Gaussian-blur a mask into a soft alpha.</summary>
    public static GrayImage FeatherMask(GrayImage mask, int erode = 2, double blur = 3)
    {
        var values = mask.Values;
        int w = mask.Width, h = mask.Height;
        if (erode > 0)
        {
            int k = Math.Max(3, erode * 2 + 1);
            values = MinFilter(values, w, h, k);
        }
        if (blur > 0)
        {
            values = Gaussian(values, w, h, blur);
        }
        return new GrayImage(w, h, ReferenceEquals(values, mask.Values) ? (float[])values.Clone() : values);
    }

    /// <summary>k × k minimum, edges extended (vImageMin_PlanarF with kvImageEdgeExtend); separable.</summary>
    internal static float[] MinFilter(float[] v, int w, int h, int k)
    {
        int r = k / 2;  // k is odd
        var tmp = new float[v.Length];
        ForRows(h, (long)w * h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float m = float.PositiveInfinity;
                for (int d = -r; d <= r; d++) m = MathF.Min(m, v[row + Math.Clamp(x + d, 0, w - 1)]);
                tmp[row + x] = m;
            }
        });
        var dst = new float[v.Length];
        ForRows(h, (long)w * h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float m = float.PositiveInfinity;
                for (int d = -r; d <= r; d++) m = MathF.Min(m, tmp[Math.Clamp(y + d, 0, h - 1) * w + x]);
                dst[y * w + x] = m;
            }
        });
        return dst;
    }

    /// <summary>Separable Gaussian, kernel radius ceil(3σ), edges extended (vImageConvolve_PlanarF with kvImageEdgeExtend).</summary>
    internal static float[] Gaussian(float[] v, int w, int h, double sigma)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var kernel = new float[radius * 2 + 1];
        for (int i = -radius; i <= radius; i++) kernel[i + radius] = (float)Math.Exp(-(double)(i * i) / (2 * sigma * sigma));
        float sum = 0;
        foreach (var f in kernel) sum += f;
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;

        var tmp = new float[v.Length];
        ForRows(h, (long)w * h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int d = -radius; d <= radius; d++) acc += v[row + Math.Clamp(x + d, 0, w - 1)] * kernel[d + radius];
                tmp[row + x] = acc;
            }
        });
        var dst = new float[v.Length];
        ForRows(h, (long)w * h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int d = -radius; d <= radius; d++) acc += tmp[Math.Clamp(y + d, 0, h - 1) * w + x] * kernel[d + radius];
                dst[y * w + x] = acc;
            }
        });
        return dst;
    }

    // MARK: Colour match

    /// <summary>Weighted least squares <c>ref ≈ gain·src + offset</c> (one channel, 0...255 scale).</summary>
    internal static (double Gain, double Offset) FitGainOffset(float[] src, float[] reference, float[] weights)
    {
        double sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = 0; i < src.Length; i++)
        {
            double w = weights[i], x = src[i], y = reference[i];
            sw += w; sx += w * x; sy += w * y; sxx += w * x * x; sxy += w * x * y;
        }
        if (!(sw >= 1)) return (1, 0);
        double denom = sw * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-6)
        {
            return (1, Math.Min(Math.Max((sy - sx) / sw, -64), 64));
        }
        double gain = (sw * sxy - sx * sy) / denom;
        double offset = (sy - gain * sx) / sw;
        return (Math.Min(Math.Max(gain, 0.5), 2), Math.Min(Math.Max(offset, -64), 64));
    }

    /// <summary>
    /// Per-channel gain/offset match of <paramref name="result"/> to <paramref name="original"/>, fitted only where
    /// <paramref name="trusted"/> is high (255 = untouched pixel). Keeps <paramref name="result"/>'s alpha.
    /// </summary>
    public static RGBAImage ColorMatch(RGBAImage result, RGBAImage original, GrayImage trusted)
    {
        int n = result.Width * result.Height;
        var weights = new float[n];
        for (int i = 0; i < n; i++) weights[i] = trusted.Values[i] / 255;
        var o = result.Clone();
        var src = new float[n];
        var reference = new float[n];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < n; i++) { src[i] = result.Pixels[i * 4 + c]; reference[i] = original.Pixels[i * 4 + c]; }
            var (gain, offset) = FitGainOffset(src, reference, weights);
            for (int i = 0; i < n; i++)
            {
                o.Pixels[i * 4 + c] = MathF.Truncate((float)Math.Min(Math.Max(src[i] * gain + offset, 0), 255));
            }
        }
        return o;
    }

    // MARK: Blend

    /// <summary><c>original·(1−a) + result·a</c> in RGBA, <paramref name="alpha"/> 0...255. Truncated, as the 8-bit original.</summary>
    public static RGBAImage BlendMasked(RGBAImage original, RGBAImage result, GrayImage alpha)
    {
        var o = original.Clone();
        int n = original.Width * original.Height;
        ForRows(original.Height, n, y =>
        {
            for (int x = 0; x < original.Width; x++)
            {
                int i = y * original.Width + x;
                float a = alpha.Values[i] / 255;
                for (int c = 0; c < 4; c++)
                {
                    float v = original.Pixels[i * 4 + c] * (1 - a) + result.Pixels[i * 4 + c] * a;
                    o.Pixels[i * 4 + c] = MathF.Truncate(Math.Min(Math.Max(v, 0), 255));
                }
            }
        });
        return o;
    }

    // MARK: ROI

    /// <summary>Pads an ROI by <paramref name="padding"/> (fraction of its size) on each side and clamps it to the image.</summary>
    public static PixelRect ClampROI(PixelRect roi, int width, int height, double padding = 0.3)
    {
        int px = RoundAway(roi.Width * padding), py = RoundAway(roi.Height * padding);
        var r = new PixelRect(roi.X - px, roi.Y - py, roi.Width + 2 * px, roi.Height + 2 * py);
        return r.Intersection(new PixelRect(0, 0, width, height));
    }

    // MARK: Lab colour transfer (Reinhard)

    internal static (double L, double A, double B) ToLab(float r, float g, float b)
    {
        static double Lin(float c) { double d = c / 255.0; return d <= 0.04045 ? d / 12.92 : Math.Pow((d + 0.055) / 1.055, 2.4); }
        double R = Lin(r), G = Lin(g), B = Lin(b);
        double x = (0.4124564 * R + 0.3575761 * G + 0.1804375 * B) / 0.95047;
        double y = 0.2126729 * R + 0.7151522 * G + 0.0721750 * B;
        double z = (0.0193339 * R + 0.1191920 * G + 0.9503041 * B) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    internal static (float R, float G, float B) FromLab(double l, double a, double b)
    {
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        static double Inv(double t) => t * t * t > 0.008856 ? t * t * t : (t - 16.0 / 116) / 7.787;
        double x = Inv(fx) * 0.95047, y = Inv(fy), z = Inv(fz) * 1.08883;
        double R = 3.2404542 * x - 1.5371385 * y - 0.4985314 * z;
        double G = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z;
        double B = 0.0556434 * x - 0.2040259 * y + 1.0572252 * z;
        static float Enc(double c)
        {
            c = Math.Min(Math.Max(c, 0), 1);
            return (float)((c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055) * 255);
        }
        return (Enc(R), Enc(G), Enc(B));
    }

    sealed record LabStats(double[] Labs, (double L, double A, double B) Mean, (double L, double A, double B) Std);

    static LabStats Stats(RGBAImage img)
    {
        int n = img.Width * img.Height;
        var labs = new double[n * 3];
        ForRows(img.Height, n, y =>
        {
            for (int x = 0; x < img.Width; x++)
            {
                int i = y * img.Width + x;
                var (l, a, b) = ToLab(img.Pixels[i * 4], img.Pixels[i * 4 + 1], img.Pixels[i * 4 + 2]);
                labs[i * 3] = l; labs[i * 3 + 1] = a; labs[i * 3 + 2] = b;
            }
        });
        double s0 = 0, s1 = 0, s2 = 0;
        for (int i = 0; i < n; i++) { s0 += labs[i * 3]; s1 += labs[i * 3 + 1]; s2 += labs[i * 3 + 2]; }
        double d = Math.Max(n, 1);
        var mean = (s0 / d, s1 / d, s2 / d);
        double q0 = 0, q1 = 0, q2 = 0;
        for (int i = 0; i < n; i++)
        {
            double a0 = labs[i * 3] - mean.Item1, a1 = labs[i * 3 + 1] - mean.Item2, a2 = labs[i * 3 + 2] - mean.Item3;
            q0 += a0 * a0; q1 += a1 * a1; q2 += a2 * a2;
        }
        double t0 = Math.Sqrt(q0 / d), t1 = Math.Sqrt(q1 / d), t2 = Math.Sqrt(q2 / d);
        if (t0 < 1e-6) t0 = 1;
        if (t1 < 1e-6) t1 = 1;
        if (t2 < 1e-6) t2 = 1;
        return new LabStats(labs, mean, (t0, t1, t2));
    }

    /// <summary>
    /// Moves <paramref name="source"/>'s colours toward <paramref name="reference"/> by matching Lab mean and
    /// standard deviation, blended by <paramref name="strength"/>. Keeps <paramref name="source"/>'s alpha.
    /// </summary>
    public static RGBAImage LabColorTransfer(RGBAImage source, RGBAImage reference, double strength = 1)
    {
        var s = Stats(source);
        var r = Stats(reference);
        var o = source.Clone();
        float k = (float)Math.Min(Math.Max(strength, 0), 1);
        int n = source.Width * source.Height;
        ForRows(source.Height, n, y =>
        {
            for (int x = 0; x < source.Width; x++)
            {
                int i = y * source.Width + x;
                double t0 = (s.Labs[i * 3] - s.Mean.L) / s.Std.L * r.Std.L + r.Mean.L;
                double t1 = (s.Labs[i * 3 + 1] - s.Mean.A) / s.Std.A * r.Std.A + r.Mean.A;
                double t2 = (s.Labs[i * 3 + 2] - s.Mean.B) / s.Std.B * r.Std.B + r.Mean.B;
                var (cr, cg, cb) = FromLab(t0, t1, t2);
                float orig0 = source.Pixels[i * 4], orig1 = source.Pixels[i * 4 + 1], orig2 = source.Pixels[i * 4 + 2];
                o.Pixels[i * 4] = orig0 + (cr - orig0) * k;
                o.Pixels[i * 4 + 1] = orig1 + (cg - orig1) * k;
                o.Pixels[i * 4 + 2] = orig2 + (cb - orig2) * k;
            }
        });
        return o;
    }
}
