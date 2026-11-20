// Ported from Packages/GlimmerKit/Sources/GlimmerRender (MPSImageEuclideanDistanceTransform, as LayerStyleRenderer.swift
// and SelectionOps.swift use it)
namespace Glimmer.Render;

/// <summary>
/// The exact Euclidean distance transform: for every pixel, the distance (pixel centre to pixel centre) to the nearest
/// non-zero pixel of the input; zero on non-zero pixels. What MPSImageEuclideanDistanceTransform computes on the Mac.
/// </summary>
/// <remarks>
/// Felzenszwalb and Huttenlocher, "Distance Transforms of Sampled Functions", Theory of Computing 8 (2012): two passes
/// of the 1-D lower envelope of parabolas, linear in the pixel count. An input without any non-zero pixel gives
/// <see cref="NoFeature"/> everywhere.
/// </remarks>
internal static class DistanceTransform
{
    public const float NoFeature = 1e20f;
    private const double Infinity = 1e30;

    public static void Compute(ReadOnlySpan<float> input, int width, int height, Span<float> output)
    {
        var squared = new double[(long)width * height];
        for (int i = 0; i < squared.Length; i++) squared[i] = input[i] != 0f ? 0 : Infinity;

        // Columns.
        Parallel.For(0, width, () => new Buffers(height), (x, _, b) =>
        {
            for (int y = 0; y < height; y++) b.F[y] = squared[(long)y * width + x];
            Envelope(b, height);
            for (int y = 0; y < height; y++) squared[(long)y * width + x] = b.D[y];
            return b;
        }, _ => { });

        // Rows.
        var result = new float[squared.Length];
        Parallel.For(0, height, () => new Buffers(width), (y, _, b) =>
        {
            long row = (long)y * width;
            for (int x = 0; x < width; x++) b.F[x] = squared[row + x];
            Envelope(b, width);
            for (int x = 0; x < width; x++)
            {
                double d = b.D[x];
                result[row + x] = d >= Infinity * 0.5 ? NoFeature : (float)Math.Sqrt(d);
            }
            return b;
        }, _ => { });
        result.CopyTo(output);
    }

    private sealed class Buffers(int n)
    {
        public readonly double[] F = new double[n];
        public readonly double[] D = new double[n];
        public readonly int[] V = new int[n];
        public readonly double[] Z = new double[n + 1];
    }

    /// <summary>1-D squared distance transform of <c>b.F</c> into <c>b.D</c>.</summary>
    private static void Envelope(Buffers b, int n)
    {
        double[] f = b.F, d = b.D, z = b.Z;
        int[] v = b.V;
        // Only finite samples define parabolas; a line with none stays infinite.
        int first = -1;
        for (int q = 0; q < n; q++)
            if (f[q] < Infinity) { first = q; break; }
        if (first < 0)
        {
            for (int q = 0; q < n; q++) d[q] = Infinity;
            return;
        }
        int k = 0;
        v[0] = first;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;
        for (int q = first + 1; q < n; q++)
        {
            if (f[q] >= Infinity) continue;
            double s = Intersection(f, v[k], q);
            while (s <= z[k])  // z[0] = −∞ ends the loop at k = 0
            {
                k--;
                s = Intersection(f, v[k], q);
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            double dq = q - v[k];
            d[q] = dq * dq + f[v[k]];
        }
    }

    /// <summary>Where the parabolas rooted at p and q (p &lt; q) intersect.</summary>
    private static double Intersection(double[] f, int p, int q) =>
        ((f[q] + (double)q * q) - (f[p] + (double)p * p)) / (2.0 * q - 2.0 * p);
}
