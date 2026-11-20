// Ported from Packages/GlimmerKit/Sources/GlimmerHeal/Heal.swift
//
// Heal Selection: fills a selected area from what surrounds it. No model, no network.
//
// HARD RULES for everything in this project (docs/PATENTS.md in the original has the patent numbers and the
// reasons; HealSourceRulesTests checks what can be checked in source). Do not relax one
// without a decision recorded there.
//
//  1. Every patch is searched from scratch over the whole window. No match is ever derived
//     from a neighbour's match, and nothing is randomised.
//  2. Everything runs at full resolution. No reduced-size copy of the image, no pyramid.
//  3. Patches are axis-aligned, 1:1, copied as they are: never rotated, scaled, mirrored or
//     otherwise transformed; overlapping patches are never voted or blended.
//  3b. No gradient-domain (Poisson) seam.
//  4. The fill order is the priority of Criminisi, Pérez and Toyama (2004), unchanged:
//     confidence × data term at the front pixel. No map of structure strength.
//  5. The smooth fill is fast marching (Telea 2004). No differential-equation solve.
//  6. Where to sample is fixed by the hole. There is no "sample from here" input.
//  7. Written from the papers. No code copied or ported from anywhere.
#if GLIMMER_HEAL
using System.Numerics;

namespace Glimmer.Heal;

public static class Heal
{
    // Swift raw values: "auto", "smooth", "texture".
    public enum Method { Auto, Smooth, Texture }

    /// <summary>Why a fill was not done. <see cref="Reason"/> is a short code, <see cref="Exception.Message"/> the text a person sees.</summary>
    public sealed class Refusal : Exception
    {
        public string Reason { get; }
        /// <summary>True when <c>allowLarge</c> doesn't help.</summary>
        public bool Hard { get; set; }

        public Refusal(string reason, string message, bool hard = false) : base(message)
        {
            Reason = reason;
            Hard = hard;
        }
    }

    /// <summary>Side of the square patches of the texture fill.</summary>
    public const int PatchSide = 9;
    /// <summary>Up to this width (the area at its thickest, in pixels) the result is usually good.</summary>
    public const double ComfortableWidth = 120.0;
    /// <summary>Beyond this width, or this many pixels, the fill is refused whatever the caller says.</summary>
    public const double LargestWidth = 250.0;
    public const int LargestPixels = 4_000_000;
    /// <summary>A hole at most this wide is filled smoothly when the method is <c>Auto</c>.</summary>
    public const double ThinWidth = 6.0;

    // MARK: What the selection means

    /// <summary>The area to fill, worked out from the selection's coverage over a rectangle.</summary>
    /// <param name="Width">Width of the rectangle.</param>
    /// <param name="Height">Height of the rectangle.</param>
    /// <param name="Hole">True where pixels are replaced (any coverage after expanding).</param>
    /// <param name="Weight">How much of the fill shows: 1 in the solid part, falling off over a soft selection edge.</param>
    /// <param name="Pixels">Pixels in the solid part.</param>
    /// <param name="Thickness">How wide the solid part is at its thickest.</param>
    /// <param name="Bounds">Bounds of <c>Hole</c> in the rectangle: x, y, width, height.</param>
    public sealed record Area(int Width, int Height, bool[] Hole, float[] Weight, int Pixels, double Thickness,
                              (int X, int Y, int Width, int Height) Bounds);

    /// <summary>
    /// <c>coverage</c> is the selection (0…1) over a <c>width</c> × <c>height</c> rectangle. The part covered
    /// at least half is grown by <c>expand</c> pixels and replaced fully; a soft edge outside that
    /// is blended by its coverage. Null when nothing is covered at least half.
    /// </summary>
    public static Area? MakeArea(float[] coverage, int width, int height, int expand)
    {
        if (coverage.Length != width * height) throw new ArgumentException("coverage.Length must be width * height");
        var core = new bool[coverage.Length];
        bool any = false;
        for (int i = 0; i < core.Length; i++)
        {
            core[i] = coverage[i] >= 0.5f;
            any |= core[i];
        }
        if (!any) return null;
        float[] toCore = Distance(core, width, height);
        float reach = (float)expand + 0.001f;
        var solid = new bool[core.Length];
        var weight = new float[core.Length];
        var hole = new bool[core.Length];
        int count = 0;
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int i = 0; i < core.Length; i++)
        {
            if (toCore[i] <= reach)
            {
                solid[i] = true;
                weight[i] = 1;
                count++;
            }
            else if (coverage[i] > 0.02f)
            {
                weight[i] = Math.Min(1f, 2 * coverage[i]);
            }
            if (weight[i] > 0)
            {
                hole[i] = true;
                int x = i % width, y = i / width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }
        var notSolid = new bool[solid.Length];
        for (int i = 0; i < solid.Length; i++) notSolid[i] = !solid[i];
        float[] inside = Distance(notSolid, width, height);
        float deepest = 0;
        for (int i = 0; i < solid.Length; i++) if (solid[i]) deepest = Math.Max(deepest, inside[i]);
        // A row of n pixels has its middle pixel (n + 1) / 2 from the outside.
        double thickness = float.IsFinite(deepest) ? (double)(2 * deepest - 1) : (double)Math.Max(width, height);
        return new Area(width, height, hole, weight, count, Math.Max(1, thickness),
                        (minX, minY, maxX - minX + 1, maxY - minY + 1));
    }

    /// <summary>How far the window that is sampled reaches beyond the hole's bounds.</summary>
    public static int Padding(double thickness) => Math.Min(256, Math.Max(64, (int)Math.Ceiling(2 * thickness)));

    /// <summary>
    /// The texture fill compares every candidate patch for every patch it fills, so its time
    /// goes with hole pixels × window pixels. Above this product it is refused (about 10 s
    /// on the Mac it was measured on, an M-series with 8 fast cores; docs/TESTING-NOTES.md).
    /// </summary>
    public const double LargestTextureWork = 4.0e10;

    /// <summary>Whether an area of this size is filled: null, or why not. <c>allowLarge</c> lifts the first limit only.</summary>
    public static Refusal? RefusalFor(int pixels, double thickness, bool allowLarge)
    {
        int across = (int)Math.Round(thickness, MidpointRounding.AwayFromZero);
        if (thickness > LargestWidth || pixels > LargestPixels)
        {
            return new Refusal("area_too_large",
                               $"The area is {across} px across at its thickest ({pixels} pixels). Heal Selection fills areas up to {(int)LargestWidth} px across and {LargestPixels / 1_000_000} megapixels.",
                               hard: true);
        }
        if (thickness > ComfortableWidth && !allowLarge)
        {
            return new Refusal("area_too_large",
                               $"The area is {across} px across at its thickest. Heal Selection gives good results up to about {(int)ComfortableWidth} px; beyond that the fill may smear or repeat.");
        }
        return null;
    }

    // MARK: Filling

    public sealed class Result
    {
        /// <summary>The window's pixels with every hole pixel replaced.</summary>
        public float[] Pixels { get; set; }
        public Method Method { get; set; }

        public Result(float[] pixels, Method method)
        {
            Pixels = pixels;
            Method = method;
        }
    }

    /// <summary>
    /// Fills <c>hole</c> in a <c>width</c> × <c>height</c> window of premultiplied RGBA <c>pixels</c> (4 floats a
    /// pixel, row-major). Pixels outside the hole are never changed. Same input, same output.
    /// Throws <see cref="Refusal"/>, or <see cref="OperationCanceledException"/> when <c>isCancelled</c> says so.
    /// </summary>
    public static Result Fill(float[] pixels, int width, int height, bool[] hole, double thickness,
                              Method method = Method.Auto, Func<bool>? isCancelled = null)
    {
        if (pixels.Length != width * height * 4 || hole.Length != width * height)
            throw new ArgumentException("pixels must be width * height * 4 floats and hole width * height flags");
        isCancelled ??= static () => false;
        int usable = 0, missing = 0;
        for (int i = 0; i < hole.Length; i++)
        {
            if (hole[i]) missing++;
            else if (pixels[i * 4 + 3] > 0) usable++;
        }
        if (missing == 0) return new Result((float[])pixels.Clone(), method == Method.Auto ? Method.Smooth : method);
        if (usable < missing)
        {
            throw new Refusal("nothing_to_sample",
                              $"There isn't enough image around the selection to fill it from ({usable} usable pixels for {missing}).");
        }
        Method chosen = method;
        if (method == Method.Auto)
        {
            chosen = thickness <= ThinWidth || !HasTexture(pixels, width, height, hole) ? Method.Smooth : Method.Texture;
        }
        if (chosen == Method.Texture)
        {
            if ((double)missing * (double)(width * height) > LargestTextureWork)
            {
                throw new Refusal("area_too_large",
                                  $"The area ({missing} pixels) is too large to fill from its surroundings in reasonable time.",
                                  hard: true);
            }
            try
            {
                return new Result(ExemplarFill.Run(pixels, width, height, hole, isCancelled), Method.Texture);
            }
            catch (Refusal refusal) when (method == Method.Auto && refusal.Reason == "nothing_to_sample")
            {
                // No whole patch to copy from (a thin strip of image, say): the smooth fill still works.
            }
        }
        return new Result(FastMarchingFill.Run(pixels, width, height, hole, isCancelled), Method.Smooth);
    }

    /// <summary>
    /// Whether the image just around the hole has texture a smooth fill would wipe out:
    /// the mean difference between neighbouring pixels there, in 8-bit levels.
    /// </summary>
    internal static bool HasTexture(float[] pixels, int width, int height, bool[] hole)
    {
        float[] near = Distance(hole, width, height);
        var tone = new MatchImage(pixels, width, height);
        double sum = 0;
        int n = 0;
        for (int y = 0; y < height - 1; y++)
        {
            for (int x = 0; x < width - 1; x++)
            {
                int i = y * width + x;
                if (!(near[i] > 0 && near[i] <= 12 && !hole[i + 1] && !hole[i + width]
                      && pixels[i * 4 + 3] > 0 && pixels[(i + 1) * 4 + 3] > 0 && pixels[(i + width) * 4 + 3] > 0)) continue;
                sum += Math.Abs(tone.Luma(i) - tone.Luma(i + 1)) + Math.Abs(tone.Luma(i) - tone.Luma(i + width));
                n += 2;
            }
        }
        return n > 0 && sum / n > TextureLevel;
    }

    /// <summary>Photo noise on an even surface is 1–2 levels between neighbours; grass, gravel, fabric are well above.</summary>
    internal const double TextureLevel = 2.5;

    // MARK: Distance

    /// <summary>
    /// The distance from every pixel to the nearest <c>true</c> pixel (0 on them; infinity if there is none).
    /// Exact Euclidean, by two passes of the one-dimensional lower-envelope transform.
    /// </summary>
    public static float[] Distance(bool[] mask, int width, int height)
    {
        const float far = 1e20f;
        var d = new float[mask.Length];
        for (int i = 0; i < d.Length; i++) d[i] = mask[i] ? 0f : far;
        int n = Math.Max(width, height);
        var f = new float[n];
        var output = new float[n];
        var v = new int[n];
        var z = new float[n + 1];

        // Lower envelope of the parabolas rooted at the points that have a distance at all.
        void Pass(int count)
        {
            int k = -1;
            for (int q = 0; q < count; q++)
            {
                if (!(f[q] < far / 2)) continue;
                float s = -far;
                while (k >= 0)
                {
                    int p = v[k];
                    s = ((f[q] + (float)(q * q)) - (f[p] + (float)(p * p))) / (float)(2 * q - 2 * p);
                    if (s <= z[k]) k--; else break;
                }
                if (k < 0) s = -far;
                k++;
                v[k] = q; z[k] = s; z[k + 1] = far;
            }
            if (k < 0)
            {
                for (int q = 0; q < count; q++) output[q] = far;
                return;
            }
            k = 0;
            for (int q = 0; q < count; q++)
            {
                while (z[k + 1] < (float)q) k++;
                int p = v[k];
                output[q] = (float)((q - p) * (q - p)) + f[p];
            }
        }

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++) f[y] = d[y * width + x];
            Pass(height);
            for (int y = 0; y < height; y++) d[y * width + x] = output[y];
        }
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++) f[x] = d[y * width + x];
            Pass(width);
            for (int x = 0; x < width; x++) d[y * width + x] = output[x];
        }
        var result = new float[d.Length];
        for (int i = 0; i < d.Length; i++) result[i] = d[i] >= far / 2 ? float.PositiveInfinity : MathF.Sqrt(d[i]);
        return result;
    }
}

/// <summary>
/// The image as it is compared: straight (not premultiplied) colour, roughly as the eye
/// weighs it (square root of linear light), in 8 bits, at the image's own size.
/// </summary>
internal readonly struct MatchImage
{
    /// <summary>Four bytes a pixel: red, green, blue, unused.</summary>
    public readonly byte[] Bytes;

    public MatchImage(float[] pixels, int width, int height)
    {
        Bytes = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            float a = pixels[i * 4 + 3];
            if (!(a > 0)) continue;
            for (int c = 0; c < 3; c++)
            {
                float v = Math.Min(1f, Math.Max(0f, pixels[i * 4 + c] / a));
                Bytes[i * 4 + c] = (byte)MathF.Round(MathF.Sqrt(v) * 255f, MidpointRounding.AwayFromZero);
            }
        }
    }

    public double Luma(int i) =>
        0.2126 * (double)Bytes[i * 4] + 0.7152 * (double)Bytes[i * 4 + 1] + 0.0722 * (double)Bytes[i * 4 + 2];
}
#endif
