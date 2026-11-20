// New in the port, no Swift file of its own. The Mac's on-device code draws a CGImage into a small bitmap context
// (`CGContext.draw` with an interpolation quality) or a CVPixelBuffer before it measures or runs a model; this is that
// step for an RGBAImage: resampling to a given size, the 8-bit premultiplied bytes such a context holds, a grey copy, and
// the planar float tensors ONNX Runtime takes.
//
// Core Graphics' resampling is not documented exactly; here shrinking averages the area each output pixel covers (a box
// filter, like CG's medium quality) and enlarging is bilinear. Values can differ from the Mac's by a level or two.
using Glimmer.Core;

namespace Glimmer.AI;

internal static class PixelSampling
{
    /// <summary>One output position along an axis: the first source index and the weights from there.</summary>
    private readonly record struct Taps(int Start, float[] Weights);

    /// <summary>Weights along one axis: box over the covered area when shrinking, bilinear when enlarging.</summary>
    private static Taps[] AxisTaps(int source, int target)
    {
        var taps = new Taps[target];
        double scale = (double)source / target;
        for (int i = 0; i < target; i++)
        {
            if (scale >= 1)
            {
                double a = i * scale, b = (i + 1) * scale;
                int first = (int)Math.Floor(a), last = Math.Min(source - 1, (int)Math.Ceiling(b) - 1);
                var w = new float[last - first + 1];
                double total = 0;
                for (int s = first; s <= last; s++)
                {
                    double overlap = Math.Min(b, s + 1) - Math.Max(a, s);
                    w[s - first] = (float)Math.Max(overlap, 0);
                    total += Math.Max(overlap, 0);
                }
                for (int k = 0; k < w.Length; k++) w[k] = (float)(w[k] / total);
                taps[i] = new Taps(first, w);
            }
            else
            {
                double c = (i + 0.5) * scale - 0.5;
                int s0 = (int)Math.Floor(c);
                float t = (float)(c - s0);
                int a = Math.Clamp(s0, 0, source - 1), b = Math.Clamp(s0 + 1, 0, source - 1);
                taps[i] = a == b ? new Taps(a, [1f]) : new Taps(a, [1 - t, t]);
            }
        }
        return taps;
    }

    /// <summary>
    /// <paramref name="image"/> at <paramref name="w"/> × <paramref name="h"/>, premultiplied (the colour over black,
    /// as the Mac's contexts without alpha draw it), RGBA float 0…255.
    /// </summary>
    public static float[] ResamplePremultiplied(RGBAImage image, int w, int h)
    {
        int sw = image.Width, sh = image.Height;
        var src = image.Pixels;
        if (w == sw && h == sh)
        {
            var pre = new float[src.Length];
            for (int i = 0; i < src.Length; i += 4)
            {
                float a = Math.Clamp(src[i + 3], 0, 255), k = a / 255;
                pre[i] = src[i] * k;
                pre[i + 1] = src[i + 1] * k;
                pre[i + 2] = src[i + 2] * k;
                pre[i + 3] = a;
            }
            return pre;
        }
        var xt = AxisTaps(sw, w);
        var yt = AxisTaps(sh, h);
        // Horizontal pass: sh rows of w pixels, premultiplying as the source is read (no full-size copy).
        var tmp = new float[(long)sh * w * 4];
        Imaging.ForRows(sh, (long)sw * sh, y =>
        {
            int sRow = y * sw * 4, dRow = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                var tap = xt[x];
                float r = 0, g = 0, b = 0, a = 0;
                for (int k = 0; k < tap.Weights.Length; k++)
                {
                    int s = sRow + (tap.Start + k) * 4;
                    float alpha = Math.Clamp(src[s + 3], 0, 255);
                    float wk = tap.Weights[k], wa = wk * alpha / 255;
                    r += src[s] * wa; g += src[s + 1] * wa; b += src[s + 2] * wa; a += alpha * wk;
                }
                int d = dRow + x * 4;
                tmp[d] = r; tmp[d + 1] = g; tmp[d + 2] = b; tmp[d + 3] = a;
            }
        });
        var o = new float[(long)w * h * 4];
        int rowLen = w * 4;
        Imaging.ForRows(h, (long)w * sh, y =>
        {
            var tap = yt[y];
            int dRow = y * rowLen;
            for (int k = 0; k < tap.Weights.Length; k++)
            {
                float wk = tap.Weights[k];
                int sRow = (tap.Start + k) * rowLen;
                for (int i = 0; i < rowLen; i++) o[dRow + i] += tmp[sRow + i] * wk;
            }
        });
        return o;
    }

    /// <summary>A resampled copy (straight colour again), what the Mac's <c>downscaled(_:maxSide:)</c> draws.</summary>
    public static RGBAImage Resized(RGBAImage image, int w, int h)
    {
        if (w == image.Width && h == image.Height) return image;
        var pre = ResamplePremultiplied(image, w, h);
        for (int i = 0; i < pre.Length; i += 4)
        {
            float a = pre[i + 3];
            float k = a > 0 ? 255 / a : 0;
            pre[i] = Math.Min(255, pre[i] * k);
            pre[i + 1] = Math.Min(255, pre[i + 1] * k);
            pre[i + 2] = Math.Min(255, pre[i + 2] * k);
        }
        return new RGBAImage(w, h, pre);
    }

    /// <summary>
    /// The bytes of an 8-bit sRGB context with premultiplied alpha (<c>premultipliedLast</c>) that
    /// <paramref name="image"/> was drawn into at <paramref name="w"/> × <paramref name="h"/>.
    /// <paramref name="nearest"/> is CG's <c>interpolationQuality = .none</c>: one source pixel per output pixel.
    /// </summary>
    public static byte[] Draw8(RGBAImage image, int w, int h, bool nearest = false)
    {
        var bytes = new byte[(long)w * h * 4];
        if (nearest)
        {
            int sw = image.Width, sh = image.Height;
            var src = image.Pixels;
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(sh - 1, (int)((y + 0.5) * sh / h));
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(sw - 1, (int)((x + 0.5) * sw / w));
                    int s = (sy * sw + sx) * 4, d = (y * w + x) * 4;
                    float a = Math.Clamp(src[s + 3], 0, 255), k = a / 255;
                    bytes[d] = Byte(src[s] * k);
                    bytes[d + 1] = Byte(src[s + 1] * k);
                    bytes[d + 2] = Byte(src[s + 2] * k);
                    bytes[d + 3] = Byte(a);
                }
            }
            return bytes;
        }
        var pre = ResamplePremultiplied(image, w, h);
        for (int i = 0; i < pre.Length; i++) bytes[i] = Byte(pre[i]);
        return bytes;
    }

    /// <summary>
    /// A grey 8-bit context without alpha (<c>DeviceGray</c>) that <paramref name="image"/> was drawn into: the light of
    /// each pixel (Rec. 709 weights on linear light, encoded again), transparent parts over black.
    /// </summary>
    public static byte[] DrawGray8(RGBAImage image, int w, int h)
    {
        var pre = ResamplePremultiplied(image, w, h);
        var gray = new byte[(long)w * h];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
        {
            float r = Imaging.SrgbDecode(pre[p] / 255), g = Imaging.SrgbDecode(pre[p + 1] / 255), b = Imaging.SrgbDecode(pre[p + 2] / 255);
            gray[i] = Byte(Imaging.SrgbEncode(0.2126f * r + 0.7152f * g + 0.0722f * b) * 255);
        }
        return gray;
    }

    /// <summary>
    /// The planar 1 × 3 × <paramref name="h"/> × <paramref name="w"/> input of an image model:
    /// <c>(value / 255 − mean[c]) / std[c]</c> of the image stretched to that size (colour over black).
    /// </summary>
    public static float[] Tensor(RGBAImage image, int w, int h, ReadOnlySpan<float> mean, ReadOnlySpan<float> std)
    {
        var pre = ResamplePremultiplied(image, w, h);
        int plane = w * h;
        var t = new float[3 * plane];
        float m0 = mean[0], m1 = mean[1], m2 = mean[2], s0 = 1 / std[0], s1 = 1 / std[1], s2 = 1 / std[2];
        for (int i = 0; i < plane; i++)
        {
            t[i] = (Math.Clamp(pre[i * 4], 0, 255) / 255 - m0) * s0;
            t[plane + i] = (Math.Clamp(pre[i * 4 + 1], 0, 255) / 255 - m1) * s1;
            t[2 * plane + i] = (Math.Clamp(pre[i * 4 + 2], 0, 255) / 255 - m2) * s2;
        }
        return t;
    }

    /// <summary>A plane of floats stretched to <paramref name="W"/> × <paramref name="H"/> (bilinear, pixel centres aligned).</summary>
    public static float[] ResizePlane(ReadOnlySpan<float> source, int w, int h, int W, int H)
    {
        if (w == W && h == H) return source.ToArray();
        var src = source.ToArray();
        var xt = AxisTaps(w, W);
        var yt = AxisTaps(h, H);
        var tmp = new float[(long)h * W];
        Imaging.ForRows(h, (long)w * h, y =>
        {
            for (int x = 0; x < W; x++)
            {
                var tap = xt[x];
                float acc = 0;
                for (int k = 0; k < tap.Weights.Length; k++) acc += src[y * w + tap.Start + k] * tap.Weights[k];
                tmp[y * W + x] = acc;
            }
        });
        var o = new float[(long)W * H];
        Imaging.ForRows(H, (long)W * H, y =>
        {
            var tap = yt[y];
            for (int k = 0; k < tap.Weights.Length; k++)
            {
                float wk = tap.Weights[k];
                int sRow = (tap.Start + k) * W, dRow = y * W;
                for (int x = 0; x < W; x++) o[dRow + x] += tmp[sRow + x] * wk;
            }
        });
        return o;
    }

    /// <summary>Swift's <c>UInt8(clamping:)</c> of a rounded value.</summary>
    public static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>The size of a copy whose longer side is at most <paramref name="maxSide"/> (never enlarged).</summary>
    public static (int Width, int Height) Fit(int width, int height, int maxSide)
    {
        int side = Math.Max(width, height);
        if (side <= maxSide) return (width, height);
        double k = (double)maxSide / side;
        return (Math.Max(1, (int)(width * k)), Math.Max(1, (int)(height * k)));
    }
}
