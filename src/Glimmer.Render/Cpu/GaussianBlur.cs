// Ported from Packages/GlimmerKit/Sources/GlimmerRender (MPSImageGaussianBlur with edgeMode .clamp, as FilterEngine.swift,
// LayerStyleRenderer.swift, SelectionOps.swift and MaskFeatherRenderer use it)
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Glimmer.Render;

/// <summary>
/// A Gaussian blur of any radius with clamped edges: what MPSImageGaussianBlur does on the Mac (the web app capped
/// blurs at 32 px; the Mac and this port don't).
/// </summary>
/// <remarks>
/// Separable FIR with the sampled, normalized Gaussian over ±⌈3σ⌉ (the mass beyond is 0.27 %). Both passes run over
/// the interleaved channels with <see cref="Vector{T}"/>; rows of the first pass and column strips of the second run
/// in parallel.
/// </remarks>
internal static class GaussianBlur
{
    /// <summary>Normalized half kernel: <c>k[0]</c> is the centre weight, <c>k[i]</c> the weight at ±i.</summary>
    public static float[] Kernel(float sigma)
    {
        if (!(sigma > 0f)) return [1f];
        int radius = Math.Max(1, (int)MathF.Ceiling(sigma * 3f));
        var w = new double[radius + 1];
        double sum = 0;
        for (int i = 0; i <= radius; i++)
        {
            w[i] = Math.Exp(-(double)i * i / (2.0 * sigma * sigma));
            sum += i == 0 ? w[i] : 2 * w[i];
        }
        var k = new float[radius + 1];
        for (int i = 0; i <= radius; i++) k[i] = (float)(w[i] / sum);
        return k;
    }

    /// <summary>Blurs <paramref name="src"/> (premultiplied RGBA) into <paramref name="dst"/> (may not alias).</summary>
    public static void Blur(RegionImage src, RegionImage dst, float sigma)
    {
        if (src.Width != dst.Width || src.Height != dst.Height) throw new ArgumentException("The images differ in size.");
        Blur(src.Pixels, dst.Pixels, src.Width, src.Height, 4, sigma);
    }

    /// <summary>Blurs a one-channel image into <paramref name="dst"/> (may not alias).</summary>
    public static void Blur(FloatImage src, FloatImage dst, float sigma)
    {
        if (src.Width != dst.Width || src.Height != dst.Height) throw new ArgumentException("The images differ in size.");
        Blur(src.Pixels, dst.Pixels, src.Width, src.Height, 1, sigma);
    }

    private static void Blur<T>(T[] source, T[] destination, int width, int height, int channels, float sigma) where T : struct
    {
        float[] k = Kernel(sigma);
        long count = (long)width * height * channels;
        if (k.Length == 1)
        {
            MemoryMarshal.Cast<T, float>(source.AsSpan())[..(int)count].CopyTo(MemoryMarshal.Cast<T, float>(destination.AsSpan()));
            return;
        }
        var temp = ArrayPool<float>.Shared.Rent((int)count);
        try
        {
            Horizontal(source, temp, width, height, channels, k);
            Vertical(temp, destination, width * channels, height, k);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(temp);
        }
    }

    private static void Horizontal<T>(T[] source, float[] dst, int width, int height, int channels, float[] k) where T : struct
    {
        int r = k.Length - 1;
        int rowFloats = width * channels;
        Parallel.For(0, height, () => new float[(width + 2 * r) * channels], (y, _, pad) =>
        {
            var src = MemoryMarshal.Cast<T, float>(source.AsSpan());
            var row = src.Slice(y * rowFloats, rowFloats);
            var first = row[..channels];
            var last = row.Slice(rowFloats - channels, channels);
            for (int i = 0; i < r; i++)
            {
                first.CopyTo(pad.AsSpan(i * channels));
                last.CopyTo(pad.AsSpan((r + width + i) * channels));
            }
            row.CopyTo(pad.AsSpan(r * channels));
            Convolve(pad, dst.AsSpan(y * rowFloats, rowFloats), r * channels, channels, k);
            return pad;
        }, _ => { });
    }

    /// <summary>out[j] = Σ k[|i|] · pad[centre + j + i · stride], vectorized along j.</summary>
    private static void Convolve(ReadOnlySpan<float> pad, Span<float> output, int centre, int stride, float[] k)
    {
        int n = output.Length;
        int r = k.Length - 1;
        int j = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int v = Vector<float>.Count;
            for (; j <= n - v; j += v)
            {
                int c = centre + j;
                var acc = new Vector<float>(pad.Slice(c)) * k[0];
                for (int i = 1; i <= r; i++)
                {
                    int off = i * stride;
                    acc += (new Vector<float>(pad.Slice(c - off)) + new Vector<float>(pad.Slice(c + off))) * k[i];
                }
                acc.CopyTo(output.Slice(j));
            }
        }
        for (; j < n; j++)
        {
            int c = centre + j;
            float acc = pad[c] * k[0];
            for (int i = 1; i <= r; i++) acc += (pad[c - i * stride] + pad[c + i * stride]) * k[i];
            output[j] = acc;
        }
    }

    private const int Strip = 256;  // floats per strip of the vertical pass

    /// <summary>The vertical pass; rows are <paramref name="rowFloats"/> floats long.</summary>
    private static void Vertical<T>(float[] src, T[] destination, int rowFloats, int h, float[] k) where T : struct
    {
        int r = k.Length - 1;
        int strips = (rowFloats + Strip - 1) / Strip;
        Parallel.For(0, strips, s =>
        {
            var dst = MemoryMarshal.Cast<T, float>(destination.AsSpan());
            int x0 = s * Strip, n = Math.Min(Strip, rowFloats - x0);
            for (int y = 0; y < h; y++)
            {
                var acc = dst.Slice(y * rowFloats + x0, n);
                Scale(src.AsSpan(y * rowFloats + x0, n), k[0], acc);
                for (int i = 1; i <= r; i++)
                {
                    int ya = Math.Max(0, y - i), yb = Math.Min(h - 1, y + i);
                    AddPair(src.AsSpan(ya * rowFloats + x0, n), src.AsSpan(yb * rowFloats + x0, n), k[i], acc);
                }
            }
        });
    }

    private static void Scale(ReadOnlySpan<float> a, float weight, Span<float> output)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var wv = new Vector<float>(weight);
            int n = Vector<float>.Count;
            for (; i <= output.Length - n; i += n) (new Vector<float>(a.Slice(i)) * wv).CopyTo(output.Slice(i));
        }
        for (; i < output.Length; i++) output[i] = a[i] * weight;
    }

    /// <summary>acc += (a + b) · weight, vectorized.</summary>
    private static void AddPair(ReadOnlySpan<float> a, ReadOnlySpan<float> b, float weight, Span<float> acc)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var wv = new Vector<float>(weight);
            int n = Vector<float>.Count;
            for (; i <= acc.Length - n; i += n)
            {
                var sum = new Vector<float>(a.Slice(i)) + new Vector<float>(b.Slice(i));
                (new Vector<float>(acc.Slice(i)) + sum * wv).CopyTo(acc.Slice(i));
            }
        }
        for (; i < acc.Length; i++) acc[i] += (a[i] + b[i]) * weight;
    }
}
