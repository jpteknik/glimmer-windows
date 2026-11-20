// Ported from Packages/GlimmerKit/Sources/GlimmerTools/FloodFill.swift
namespace Glimmer.Tools;

/// <summary>Magic-wand / paint-bucket region growing on straight 8-bit-range RGBA pixels.</summary>
public static class FloodFill
{
    /// <summary>
    /// A 0/1 mask of pixels within <paramref name="tolerance"/> (0...255, max channel difference) of the colour at
    /// <paramref name="seed"/>. <paramref name="contiguous"/> grows from the seed (4-connected); otherwise every matching
    /// pixel is included.
    /// </summary>
    public static float[] Mask(ReadOnlySpan<float> pixels, int width, int height, (int X, int Y) seed, float tolerance,
                               bool contiguous)
    {
        var mask = new float[width * height];
        if (seed.X < 0 || seed.Y < 0 || seed.X >= width || seed.Y >= height) return mask;
        int s = (seed.Y * width + seed.X) * 4;
        float t0 = pixels[s], t1 = pixels[s + 1], t2 = pixels[s + 2], t3 = pixels[s + 3];

        if (!contiguous)
        {
            for (int i = 0; i < width * height; i++)
                if (Matches(pixels, i, t0, t1, t2, t3, tolerance)) mask[i] = 1;
            return mask;
        }
        // Scanline fill.
        var stack = new Stack<(int X, int Y)>();
        stack.Push(seed);
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            int row = y * width;
            if (mask[row + x] != 0 || !Matches(pixels, row + x, t0, t1, t2, t3, tolerance)) continue;
            while (x > 0 && mask[row + x - 1] == 0 && Matches(pixels, row + x - 1, t0, t1, t2, t3, tolerance)) x -= 1;
            bool spanAbove = false, spanBelow = false;
            while (x < width && mask[row + x] == 0 && Matches(pixels, row + x, t0, t1, t2, t3, tolerance))
            {
                mask[row + x] = 1;
                if (y > 0)
                {
                    int a = row - width + x;
                    bool ok = mask[a] == 0 && Matches(pixels, a, t0, t1, t2, t3, tolerance);
                    if (ok && !spanAbove) stack.Push((x, y - 1));
                    spanAbove = ok;
                }
                if (y < height - 1)
                {
                    int b = row + width + x;
                    bool ok = mask[b] == 0 && Matches(pixels, b, t0, t1, t2, t3, tolerance);
                    if (ok && !spanBelow) stack.Push((x, y + 1));
                    spanBelow = ok;
                }
                x += 1;
            }
        }
        return mask;
    }

    private static bool Matches(ReadOnlySpan<float> pixels, int i, float t0, float t1, float t2, float t3, float tolerance)
    {
        int o = i * 4;
        float d0 = MathF.Abs(pixels[o] - t0), d1 = MathF.Abs(pixels[o + 1] - t1);
        float d2 = MathF.Abs(pixels[o + 2] - t2), d3 = MathF.Abs(pixels[o + 3] - t3);
        return MathF.Max(MathF.Max(d0, d1), MathF.Max(d2, d3)) <= tolerance;
    }
}
