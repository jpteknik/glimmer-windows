// Ported from Packages/GlimmerKit/Sources/GlimmerRender/LensBlur.swift (MPSImageLanczosScale with edgeMode .clamp)
using System.Numerics;

namespace Glimmer.Render;

/// <summary>
/// Lanczos (a = 3) resampling of a whole image to a new size, separable, edges clamped (the Mac set the edge mode to
/// clamp because the default faded the last rows and columns). Downscaling widens the kernel by the scale factor so it
/// also filters.
/// </summary>
internal static class LanczosScale
{
    private const int A = 3;

    public static RegionImage Scale(RegionImage src, int width, int height)
    {
        var horizontal = new RegionImage(width, src.Height);
        var hw = Weights(src.Width, width);
        Parallel.For(0, src.Height, y =>
        {
            var row = src.Row(y);
            var output = horizontal.Row(y);
            for (int x = 0; x < width; x++)
            {
                var (start, w) = hw[x];
                Vector4 acc = Vector4.Zero;
                for (int i = 0; i < w.Length; i++)
                {
                    int sx = Math.Clamp(start + i, 0, src.Width - 1);
                    acc += row[sx] * w[i];
                }
                output[x] = acc;
            }
        });
        var result = new RegionImage(width, height);
        var vw = Weights(src.Height, height);
        Parallel.For(0, height, y =>
        {
            var (start, w) = vw[y];
            var output = result.Row(y);
            for (int i = 0; i < w.Length; i++)
            {
                int sy = Math.Clamp(start + i, 0, src.Height - 1);
                var row = horizontal.Row(sy);
                float k = w[i];
                for (int x = 0; x < width; x++) output[x] += row[x] * k;
            }
        });
        return result;
    }

    private static (int start, float[] weights)[] Weights(int srcSize, int dstSize)
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
