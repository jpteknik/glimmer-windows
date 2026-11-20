// Ported from Packages/GlimmerKit/Sources/GlimmerAI/PhotoTools.swift
//
// What replaces the Mac's frameworks here:
// - Apple's aesthetics model (VNCalculateImageAestheticsScoresRequest) has no Windows counterpart, and no openly licensed
//   model was found that judges taste as it does. The rating's `Aesthetics` is therefore a technical score of this
//   port's own: exposure, tonal range, clipping and colourfulness (Hasler and Süsstrunk, "Measuring colourfulness in
//   natural images", SPIE 5007, 2003). It ranks a well exposed, colourful, full-range photo above a dull, dark or
//   blown-out one; it knows nothing about subject or composition, which Apple's model does.
// - `IsUtility` (screenshots, receipts, documents) comes from the same signals the port can measure: a flat graphic
//   (fewer than 200 colours) or a document or screen filling much of the frame.
// - The sharpness measure and the colour count are the original's.
// - Vision's document and rectangle detection and Core Image's perspective correction: ClassicVision.DocumentQuad and
//   Warp.
using Glimmer.Core;

namespace Glimmer.AI;

// MARK: Rating photos

/// <summary>
/// How good a photo is, judged on this computer: a technical quality score plus a sharpness measure, for picking the
/// keepers out of a batch.
/// </summary>
public static class PhotoQuality
{
    public sealed record Rating(double Aesthetics, bool IsUtility, double Sharpness, bool IsPhotographic = true)
    {
        // Aesthetics: −1 (poor) … 1 (great); on Windows the technical score above, not Apple's aesthetics.
        // IsUtility: screenshots, receipts, documents: pictures taken to keep information, not memories.
        // Sharpness: 0 (very soft) … 1 (crisp); see `PhotoQuality.Sharpness`.
        // IsPhotographic: a photograph rather than a flat graphic (see `ColorVariety`).

        public bool IsBlurry => IsPhotographic && Sharpness < BlurThreshold;

        /// <summary>A single number to sort by (higher is better): aesthetics, with soft pictures pushed down.</summary>
        public double Score => Aesthetics - (IsBlurry ? 0.6 : 0) + Math.Min(Sharpness, 1) * 0.2;

        /// <summary>1…5 stars.</summary>
        public int Stars => Math.Min(5, Math.Max(1, (int)Math.Ceiling((Score + 1) / 2 * 5)));
    }

    /// <summary>
    /// Distinct colours (5 bits per channel) on a small copy: photos have thousands, flat graphics and screenshots far
    /// fewer.
    /// </summary>
    public static int ColorVariety(RGBAImage image)
    {
        int w = 128, h = Math.Max(1, 128 * image.Height / Math.Max(image.Width, 1));
        var px = PixelSampling.Draw8(image, w, h, nearest: true);
        var seen = new HashSet<int>();
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] <= 128) continue;
            seen.Add((px[i] >> 3) << 10 | (px[i + 1] >> 3) << 5 | (px[i + 2] >> 3));
        }
        return seen.Count;
    }

    /// <summary>Sharpness below this reads as out of focus or shaken.</summary>
    public const double BlurThreshold = 0.56;
    /// <summary>Fewer distinct colours than this: a graphic or screenshot, never called blurry.</summary>
    public const int PhotoColorVariety = 200;

    public static Rating Rate(RGBAImage image)
    {
        var small = SmartAnalysis.Downscaled(image, 1024);
        bool photographic = ColorVariety(small) >= PhotoColorVariety;
        return new Rating(TechnicalQuality(small), IsUtility(small, photographic), Sharpness(small), photographic);
    }

    /// <summary>
    /// The stand-in for Apple's aesthetics score, −1…1: exposure (the mean light near 0.46), tonal range (2nd to 98th
    /// percentile), little clipping, and colourfulness (Hasler and Süsstrunk's M, 80 counting as fully colourful),
    /// weighted 0.35, 0.3, 0.15 and 0.2 into q (0…1). Most photographs reach q 0.7…0.97, so the score is centred there:
    /// 0 at q = 0.8, 1 at q = 1, −1 at q = 0.6 (fourteen sample pictures, from a scanned invoice to a sharp close-up,
    /// then spread from 1 to 5 stars).
    /// </summary>
    internal static double TechnicalQuality(RGBAImage image)
    {
        var tone = SmartAnalysis.ToneStatsOf(image);
        double exposure = 1 - Math.Min(1, Math.Abs(tone.Mean - 0.46) / 0.36);
        double range = Math.Min(1, tone.Range / 0.75);
        int w = Math.Min(image.Width, 256), h = Math.Max(1, (int)((double)image.Height * w / Math.Max(image.Width, 1)));
        var px = PixelSampling.Draw8(image, w, h);
        double srg = 0, syb = 0, qrg = 0, qyb = 0;
        int n = 0, clipped = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] <= 8) continue;
            double r = px[i], g = px[i + 1], b = px[i + 2];
            double rg = r - g, yb = 0.5 * (r + g) - b;
            srg += rg; syb += yb; qrg += rg * rg; qyb += yb * yb;
            double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            if (luma <= 2 || luma >= 253) clipped++;
            n++;
        }
        if (n == 0) return 0;
        double mrg = srg / n, myb = syb / n;
        double colourfulness = Math.Sqrt(Math.Max(0, qrg / n - mrg * mrg) + Math.Max(0, qyb / n - myb * myb)) + 0.3 * Math.Sqrt(mrg * mrg + myb * myb);
        double colour = Math.Min(1, colourfulness / 80);
        double clipping = Math.Min(1, (double)clipped / n / 0.25);
        double q = 0.35 * exposure + 0.3 * range + 0.15 * (1 - clipping) + 0.2 * colour;
        return Math.Clamp((q - 0.8) / 0.2, -1, 1);
    }

    /// <summary>
    /// A screenshot, graphic, document, receipt, whiteboard or screen: flat colours, a page (see
    /// <see cref="LooksLikeAPage"/>), or a document whose edges show filling 30 % of the frame.
    /// </summary>
    internal static bool IsUtility(RGBAImage image, bool photographic)
    {
        if (!photographic || LooksLikeAPage(image)) return true;
        if (DocumentDetection.QuadIn(image) is not { } quad) return false;
        var size = quad.OutputSize;
        return size.Width * size.Height >= image.Width * image.Height * 0.3;
    }

    /// <summary>
    /// Mostly light, colourless pixels (half or more) with many fine, strong edges (3 % or more): print on paper. A
    /// snowfield or a white wall is as light but smooth.
    /// </summary>
    internal static bool LooksLikeAPage(RGBAImage image)
    {
        int w = Math.Min(image.Width, 256), h = Math.Max(1, (int)((double)image.Height * w / Math.Max(image.Width, 1)));
        if (w < 3 || h < 3) return false;
        var px = PixelSampling.Draw8(image, w, h);
        var luma = new float[w * h];
        int paper = 0;
        for (int i = 0; i < luma.Length; i++)
        {
            byte r = px[i * 4], g = px[i * 4 + 1], b = px[i * 4 + 2];
            luma[i] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            int spread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            if (luma[i] > 0.75f * 255 && spread < 0.1f * 255) paper++;
        }
        int edges = 0;
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                float l = 4 * luma[i] - luma[i - 1] - luma[i + 1] - luma[i - w] - luma[i + w];
                if (Math.Abs(l) > 40) edges++;
            }
        return paper >= luma.Length * 0.5 && edges >= luma.Length * 0.03;
    }

    /// <summary>
    /// Focus of the sharpest part of the picture: in the best of 4×4 regions, the variance of the Laplacian relative to
    /// the region's own contrast, on a 768-pixel copy, mapped to 0…1.
    /// Using the best region means a sharp subject on a soft background still counts as sharp.
    /// </summary>
    public static double Sharpness(RGBAImage image)
    {
        const int side = 768;
        double k = (double)side / Math.Max(image.Width, image.Height);
        int w = Math.Max(8, (int)(image.Width * Math.Min(k, 1))), h = Math.Max(8, (int)(image.Height * Math.Min(k, 1)));
        var gray = PixelSampling.DrawGray8(image, w, h);
        const int cells = 4;
        double best = 0;
        for (int cy = 0; cy < cells; cy++)
        {
            for (int cx = 0; cx < cells; cx++)
            {
                int x0 = Math.Max(1, cx * w / cells), x1 = Math.Min(w - 1, (cx + 1) * w / cells);
                int y0 = Math.Max(1, cy * h / cells), y1 = Math.Min(h - 1, (cy + 1) * h / cells);
                double sum = 0, sum2 = 0, isum = 0, isum2 = 0, n = 0;
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        int i = y * w + x;
                        double v = gray[i];
                        double l = 4 * v - gray[i - 1] - gray[i + 1] - gray[i - w] - gray[i + w];
                        sum += l;
                        sum2 += l * l;
                        isum += v;
                        isum2 += v * v;
                        n += 1;
                    }
                }
                if (!(n > 0)) continue;
                double lapVar = sum2 / n - (sum / n) * (sum / n);
                double intensityVar = isum2 / n - (isum / n) * (isum / n);
                // Flat regions (sky, walls) say nothing about focus.
                if (!(intensityVar > 60)) continue;
                // Relative to the region's own contrast, so a hazy or dark photo isn't called soft.
                best = Math.Max(best, lapVar / intensityVar);
            }
        }
        // Ratio ~1.2 is crisp, ~0.3 slightly soft, below 0.2 shaken or out of focus.
        return Math.Min(1, Math.Max(0, (Math.Log10(Math.Max(best, 1e-4)) + 1.7) / 1.8));
    }
}

// MARK: Documents and perspective

public static class DocumentDetection
{
    /// <summary>A quadrilateral in pixel coordinates (y down), corners in order.</summary>
    public readonly record struct Quad(Vec2 TopLeft, Vec2 TopRight, Vec2 BottomRight, Vec2 BottomLeft)
    {
        public IReadOnlyList<Vec2> Corners => [TopLeft, TopRight, BottomRight, BottomLeft];

        /// <summary>Width and height of the flattened result: the longer of each pair of opposite edges.</summary>
        public CGSizeLike OutputSize
        {
            get
            {
                static double D(Vec2 a, Vec2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
                return new CGSizeLike(Math.Round(Math.Max(D(TopLeft, TopRight), D(BottomLeft, BottomRight)), MidpointRounding.AwayFromZero),
                                      Math.Round(Math.Max(D(TopLeft, BottomLeft), D(TopRight, BottomRight)), MidpointRounding.AwayFromZero));
            }
        }
    }

    /// <summary>
    /// The document, sign, whiteboard or screen in the picture, if there is a clear one covering a fair part of the
    /// frame. (Swift's <c>quad(in:)</c>.)
    /// </summary>
    public static Quad? QuadIn(RGBAImage image)
    {
        double w = image.Width, h = image.Height;
        if (ClassicVision.DocumentQuad(image) is not { } found || found.Support < 0.6) return null;
        var quad = new Quad(found.TopLeft, found.TopRight, found.BottomRight, found.BottomLeft);
        var size = quad.OutputSize;
        if (!(size.Width * size.Height > w * h * 0.08)) return null;
        return quad;
    }

    /// <summary>
    /// The quad flattened to a rectangle (Core Image's perspective correction on the Mac; a homography here, its size
    /// from <see cref="ClassicVision.RectifiedSize"/>), kept in the document's linear extended sRGB so no precision is
    /// lost. Null when the quad has no area.
    /// </summary>
    public static LinearImage? Corrected(LinearImage image, Quad quad)
    {
        var size = quad.OutputSize;
        if (size.Width < 1 || size.Height < 1) return null;
        var (ow, oh) = ClassicVision.RectifiedSize(quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft,
                                                   new Vec2(image.Width / 2.0, image.Height / 2.0));
        return ClassicVision.Warp(image.Pixels, image.Width, image.Height, quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft, ow, oh);
    }
}
