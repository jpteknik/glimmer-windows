// Ported from Packages/GlimmerKit/Sources/GlimmerAI/SkySelection.swift
// The working copy is drawn as the Mac's 8-bit context draws it (PixelSampling.Draw8); the rest is the original's logic.
using Glimmer.Core;

namespace Glimmer.AI;

/// <summary>
/// Select Sky: the far, smooth region that reaches the top edge, grown from the top with the depth map and colour
/// continuity (no sky model needed).
/// </summary>
public static class SkySelection
{
    /// <summary>Working size of the region growing; the mask is scaled up afterwards.</summary>
    internal const int Side = 512;

    /// <summary>
    /// A soft mask (0…255) of the sky at the image's size, or null when the top of the image isn't sky.
    /// <paramref name="depth"/>: relative depth at the image's size, near = 255 (see <see cref="DepthEstimator"/>).
    /// </summary>
    public static GrayImage? Mask(RGBAImage image, GrayImage depth)
    {
        if (Region(image, depth) is not { } r) return null;
        return Upsampled(r.Sky, r.Width, r.Height, image.Width, image.Height);
    }

    internal sealed record SkyRegion(int Width, int Height, bool[] Sky, bool[] Unlike)
    {
        // Sky: the grown sky. Unlike: pixels that don't look like sky (darker, rougher or a different colour), whatever
        // the depth says.
    }

    private static float Length(float r, float g, float b) => MathF.Sqrt(r * r + g * g + b * b);

    /// <summary>The grown sky region on the working grid.</summary>
    internal static SkyRegion? Region(RGBAImage image, GrayImage depth)
    {
        double k = (double)Side / Math.Max(image.Width, image.Height);
        int w = Math.Max(8, (int)(image.Width * k)), h = Math.Max(8, (int)(image.Height * k));
        var px = PixelSampling.Draw8(image, w, h);
        float R(int i) => px[i * 4] / 255f;
        float G(int i) => px[i * 4 + 1] / 255f;
        float B(int i) => px[i * 4 + 2] / 255f;
        float Diff(int i, int j) => Length(R(i) - R(j), G(i) - G(j), B(i) - B(j));
        // Depth sampled down to the working size, 0 (far) … 1 (near).
        var d = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min(depth.Width - 1, x * depth.Width / w), sy = Math.Min(depth.Height - 1, y * depth.Height / h);
                d[y * w + x] = depth.Values[sy * depth.Width + sx] / 255;
            }
        }
        // Local roughness: mean colour difference to the 4 neighbours.
        var rough = new float[w * h];
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                rough[i] = (Diff(i - 1, i) + Diff(i + 1, i) + Diff(i - w, i) + Diff(i + w, i)) / 4;
            }
        }
        float Lum(int i) => 0.2126f * R(i) + 0.7152f * G(i) + 0.0722f * B(i);
        // Sky is bright next to what stands against it (hills, buildings, trees are silhouettes).
        var topRow = Enumerable.Range(0, w).Select(Lum).OrderBy(v => v).ToArray();
        float minLum = topRow[w / 2] * 0.6f;
        // Seeds: far, smooth pixels along the top edge.
        // Sky is always far: a near top edge (a wall, a ceiling, foliage) is never seeded.
        var topDepth = d.Take(w).OrderBy(v => v).ToArray();
        float farLimit = Math.Min(0.4f, Math.Max(0.12f, topDepth[w / 2] + 0.15f));
        var inSky = new bool[w * h];
        var queue = new List<int>();
        for (int x = 0; x < w; x++)
        {
            if (d[x] < farLimit && rough[Math.Min(w + x, w * h - 1)] < 0.06f)
            {
                inSky[x] = true;
                queue.Add(x);
            }
        }
        if (queue.Count <= w / 4) return null;
        int head = 0;
        Span<(int X, int Y)> neighbours = stackalloc (int, int)[4];
        while (head < queue.Count)
        {
            int i = queue[head];
            head++;
            int x = i % w, y = i / w;
            neighbours[0] = (x - 1, y);
            neighbours[1] = (x + 1, y);
            neighbours[2] = (x, y - 1);
            neighbours[3] = (x, y + 1);
            foreach (var (nx, ny) in neighbours)
            {
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int j = ny * w + nx;
                if (inSky[j] || !(d[j] < farLimit + 0.05f) || !(rough[j] < 0.08f) || !(Lum(j) > minLum) || !(Diff(j, i) < 0.045f)) continue;
                // A soft ridge still shows as a clear change over a few pixels: stop there.
                int bx = x - 3 * (nx - x), by = y - 3 * (ny - y);
                if (bx >= 0 && by >= 0 && bx < w && by < h && Diff(j, by * w + bx) > 0.1f) continue;
                inSky[j] = true;
                queue.Add(j);
            }
        }
        // Fill small holes (birds, specks): a pixel surrounded by sky is sky.
        var filled = (bool[])inSky.Clone();
        for (int y = 2; y < h - 2; y++)
        {
            for (int x = 2; x < w - 2; x++)
            {
                if (inSky[y * w + x]) continue;
                int n = 0;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        if (inSky[(y + dy) * w + x + dx]) n++;
                if (n >= 18) filled[y * w + x] = true;
            }
        }
        int count = filled.Count(v => v);
        double coverage = (double)count / (w * h);
        if (!(coverage > 0.03)) return null;
        var unlike = new bool[w * h];
        float sr = 0, sg = 0, sb = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (!filled[i]) continue;
            sr += R(i); sg += G(i); sb += B(i);
        }
        float denominator = Math.Max(1, count);
        sr /= denominator; sg /= denominator; sb /= denominator;
        for (int i = 0; i < w * h; i++)
        {
            if (filled[i]) continue;
            unlike[i] = Lum(i) < minLum || rough[i] > 0.08f || Length(R(i) - sr, G(i) - sg, B(i) - sb) > 0.35f;
        }
        return new SkyRegion(w, h, filled, unlike);
    }

    /// <summary>
    /// Prompts for Segment Anything: points deep inside the grown sky (positive) and in the foreground (negative), in
    /// image pixels. SAM then draws the boundary much more precisely than region growing can (a hazy ridge, cables,
    /// leaves).
    /// </summary>
    public static IReadOnlyList<MagicSelector.Point>? Prompts(RGBAImage image, GrayImage depth)
    {
        if (Region(image, depth) is not { } r) return null;
        int w = r.Width, h = r.Height;
        var sky = r.Sky;
        var unlike = r.Unlike;
        double sx = (double)image.Width / w, sy = (double)image.Height / h;
        bool DeepInside(int x, int y)
        {
            for (int dy = -6; dy <= 6; dy++)
            {
                for (int dx = -6; dx <= 6; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h || !sky[yy * w + xx]) return false;
                }
            }
            return true;
        }
        var points = new List<MagicSelector.Point>();
        // Positives: across the width, in the upper part of the sky.
        for (int col = 0; col < 5; col++)
        {
            int x = (col * 2 + 1) * w / 10;
            for (int y = 6; y < h / 2; y += 4)
            {
                if (!DeepInside(x, y)) continue;
                points.Add(new MagicSelector.Point(new Vec2((x + 0.5) * sx, (y + 0.5) * sy), positive: true));
                break;
            }
        }
        if (points.Count < 2) return null;
        // Negatives: just below the sky in each column (the skyline), and the bottom row.
        for (int col = 0; col < 5; col++)
        {
            int x = (col * 2 + 1) * w / 10;
            int top = -1;
            for (int y = 0; y < h; y++) if (sky[y * w + x]) { top = y; break; }
            if (top >= 0)
            {
                int edge = -1;
                for (int y = top; y < h; y++) if (unlike[y * w + x]) { edge = y; break; }
                if (edge >= 0)
                {
                    int y = Math.Min(h - 1, edge + Math.Max(6, h / 25));
                    if (!sky[y * w + x] && unlike[y * w + x])
                        points.Add(new MagicSelector.Point(new Vec2((x + 0.5) * sx, (y + 0.5) * sy), positive: false));
                }
            }
            if (col % 2 == 0)
                points.Add(new MagicSelector.Point(new Vec2((x + 0.5) * sx, image.Height * 0.95), positive: false));
        }
        return points.Take(MagicSelector.MaxPoints).ToList();
    }

    internal static GrayImage Upsampled(bool[] filled, int w, int h, int W, int H)
    {
        // Scale up with a soft edge.
        var o = new float[W * H];
        for (int y = 0; y < H; y++)
        {
            double fy = (y + 0.5) * h / H - 0.5;
            int y0 = Math.Max(0, Math.Min(h - 1, (int)Math.Floor(fy))), y1 = Math.Min(h - 1, y0 + 1);
            float ty = (float)Math.Min(Math.Max(fy - y0, 0), 1);
            for (int x = 0; x < W; x++)
            {
                double fx = (x + 0.5) * w / W - 0.5;
                int x0 = Math.Max(0, Math.Min(w - 1, (int)Math.Floor(fx))), x1 = Math.Min(w - 1, x0 + 1);
                float tx = (float)Math.Min(Math.Max(fx - x0, 0), 1);
                float V(int xx, int yy) => filled[yy * w + xx] ? 1 : 0;
                float top = V(x0, y0) * (1 - tx) + V(x1, y0) * tx, bottom = V(x0, y1) * (1 - tx) + V(x1, y1) * tx;
                o[y * W + x] = (top * (1 - ty) + bottom * ty) * 255;
            }
        }
        return new GrayImage(W, H, o);
    }
}
