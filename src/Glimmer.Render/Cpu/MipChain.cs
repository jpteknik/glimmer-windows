// Ported from Packages/GlimmerKit/Sources/GlimmerRender (mipmapped textures: generateMipmaps, and the trilinear sampling
// of Composite.metal's smart_fragment)
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Glimmer.Render;

/// <summary>
/// A texture with its mip levels, and the GPU's sampling rules for it: the level of detail comes from how many texels
/// one output pixel spans (the uv derivatives); at or below zero the magnification filter applies to level 0, above it
/// the two nearest levels are each sampled with the minification filter and mixed linearly (<c>mip_filter::linear</c>).
/// </summary>
internal sealed class MipChain
{
    public readonly Vector4[][] Levels;
    public readonly int[] Widths;
    public readonly int[] Heights;

    public MipChain(Vector4[][] levels, int[] widths, int[] heights)
    {
        Levels = levels;
        Widths = widths;
        Heights = heights;
    }

    public int Width => Widths[0];
    public int Height => Heights[0];
    public int LevelCount => Levels.Length;

    /// <summary>
    /// Builds every level down to 1×1 (or <paramref name="maxLevels"/> levels) from <paramref name="level0"/> (not
    /// copied). Each texel of a level is the bilinear sample at its centre in the level above: a 2×2 box for even
    /// sizes, which is what generateMipmaps gives for power-of-two textures.
    /// </summary>
    public static MipChain Build(Vector4[] level0, int width, int height, int maxLevels = int.MaxValue)
    {
        var levels = new List<Vector4[]> { level0 };
        var widths = new List<int> { width };
        var heights = new List<int> { height };
        int w = width, h = height;
        while ((w > 1 || h > 1) && levels.Count < maxLevels)
        {
            int nw = Math.Max(1, w >> 1), nh = Math.Max(1, h >> 1);
            var dst = Reduce(levels[^1], w, h, nw, nh);
            levels.Add(dst);
            widths.Add(nw);
            heights.Add(nh);
            w = nw;
            h = nh;
        }
        return new MipChain([.. levels], [.. widths], [.. heights]);
    }

    /// <summary>One level down: a 2×2 box when the sizes halve exactly, otherwise the bilinear sample at each texel centre.</summary>
    public static Vector4[] Reduce(Vector4[] src, int w, int h, int nw, int nh)
    {
        var dst = new Vector4[nw * nh];
        if (nw * 2 == w && nh * 2 == h)
        {
            for (int y = 0; y < nh; y++)
            {
                int r0 = 2 * y * w, r1 = r0 + w;
                for (int x = 0; x < nw; x++)
                    dst[y * nw + x] = (src[r0 + 2 * x] + src[r0 + 2 * x + 1] + src[r1 + 2 * x] + src[r1 + 2 * x + 1]) * 0.25f;
            }
        }
        else
        {
            var image = new RegionImage(w, h, src);
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    dst[y * nw + x] = image.SampleClampEdge(new Vector2((x + 0.5f) * w / nw, (y + 0.5f) * h / nh));
        }
        return dst;
    }

    /// <summary>The level of detail for uv derivatives (normalized coordinates per output pixel).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float LevelOfDetail(Vector2 dUVdx, Vector2 dUVdy)
    {
        var size = new Vector2(Width, Height);
        float rho = MathF.Max((dUVdx * size).Length(), (dUVdy * size).Length());
        return MathF.Log2(rho);
    }

    /// <summary>
    /// <c>sample(sampler, uv)</c> with the given level of detail. <paramref name="magNearest"/>: the magnification filter
    /// is nearest instead of linear; <paramref name="zeroOutside"/>: <c>clamp_to_zero</c> addressing instead of
    /// <c>clamp_to_edge</c>.
    /// </summary>
    public Vector4 Sample(Vector2 uv, float lod, bool magNearest, bool zeroOutside)
    {
        if (!(lod > 0f))
            return magNearest ? Nearest(0, uv, zeroOutside) : Bilinear(0, uv, zeroOutside);
        int last = Levels.Length - 1;
        float l = MathF.Min(lod, last);
        int l0 = (int)l;
        float f = l - l0;
        Vector4 a = Bilinear(l0, uv, zeroOutside);
        if (f == 0f || l0 >= last) return a;
        Vector4 b = Bilinear(l0 + 1, uv, zeroOutside);
        return a + (b - a) * f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector4 Texel(int level, int x, int y, bool zeroOutside)
    {
        int w = Widths[level], h = Heights[level];
        if (zeroOutside)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return Vector4.Zero;
        }
        else
        {
            x = x < 0 ? 0 : (x >= w ? w - 1 : x);
            y = y < 0 ? 0 : (y >= h ? h - 1 : y);
        }
        return Levels[level][y * w + x];
    }

    public Vector4 Nearest(int level, Vector2 uv, bool zeroOutside)
    {
        int x = (int)MathF.Floor(uv.X * Widths[level]);
        int y = (int)MathF.Floor(uv.Y * Heights[level]);
        return Texel(level, x, y, zeroOutside);
    }

    public Vector4 Bilinear(int level, Vector2 uv, bool zeroOutside)
    {
        float tx = uv.X * Widths[level] - 0.5f, ty = uv.Y * Heights[level] - 0.5f;
        float fx0 = MathF.Floor(tx), fy0 = MathF.Floor(ty);
        float fx = tx - fx0, fy = ty - fy0;
        int x0 = (int)fx0, y0 = (int)fy0;
        Vector4 a = Texel(level, x0, y0, zeroOutside), b = Texel(level, x0 + 1, y0, zeroOutside);
        Vector4 c = Texel(level, x0, y0 + 1, zeroOutside), d = Texel(level, x0 + 1, y0 + 1, zeroOutside);
        Vector4 top = a + (b - a) * fx;
        Vector4 bottom = c + (d - c) * fx;
        return top + (bottom - top) * fy;
    }
}
