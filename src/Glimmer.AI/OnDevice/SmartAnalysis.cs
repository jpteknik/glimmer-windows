// Ported from Packages/GlimmerKit/Sources/GlimmerAI/SmartAnalysis.swift
//
// What replaces the Mac's frameworks here:
// - Vision's horizon, saliency and face requests: ClassicVision.HorizonDegrees and SalientRect (own code), and the
//   app's IFaceDetector (Windows.Media.FaceAnalysis); without one, no faces are reported.
// - Vision's text request: the app's IImageTextRecognizer (Windows.Media.Ocr), with the Mac's filters applied here.
// - Core Image's autoAdjustmentFilters: AutoAdjustments below, a histogram method of this port's own; see there for
//   how it differs.
using System.Collections.Immutable;
using Glimmer.Core;

namespace Glimmer.AI;

/// <summary>
/// On-device image analysis: what the photo needs, found without a network call. Feeds Auto Enhance, Auto Straighten,
/// crop suggestions and the omnibar's suggestions for the open image.
/// </summary>
public static partial class SmartAnalysis
{
    // MARK: Insights

    public sealed record Insights
    {
        /// <summary>Tilt of the horizon in degrees (positive = rising to the right). null when there is no clear horizon or it is already level.</summary>
        public double? HorizonDegrees { get; init; }
        public int FaceCount { get; init; }
        /// <summary>Faces, normalised (0…1, y down).</summary>
        public ImmutableArray<CGRectLike> FaceRects { get; init; } = ImmutableArray<CGRectLike>.Empty;
        /// <summary>Whether the image shows readable text.</summary>
        public bool HasText { get; init; }
        /// <summary>Union of the salient objects, normalised (0…1, y down).</summary>
        public CGRectLike? SalientRect { get; init; }
        public ToneStats Tone { get; init; } = new();
        public EnhanceRecipe Enhance { get; init; } = new();

        public bool Equals(Insights? other) =>
            other is not null && HorizonDegrees == other.HorizonDegrees && FaceCount == other.FaceCount
            && FaceRects.SequenceEqual(other.FaceRects) && HasText == other.HasText && SalientRect == other.SalientRect
            && Tone == other.Tone && Enhance.Equals(other.Enhance);

        public override int GetHashCode() => HashCode.Combine(HorizonDegrees, FaceCount, HasText, SalientRect, Tone);
    }

    /// <summary>Luminance statistics in perceptual (sRGB) units, 0…1.</summary>
    public sealed record ToneStats
    {
        public double Mean { get; init; } = 0.5;
        /// <summary>2nd and 98th percentiles.</summary>
        public double Low { get; init; }
        public double High { get; init; } = 1.0;
        /// <summary>Mean HSV saturation.</summary>
        public double Saturation { get; init; } = 0.3;

        public double Range => High - Low;
        public bool IsFlat => Range < 0.6;
        public bool IsDark => Mean < 0.3;
        public bool IsBright => Mean > 0.75;
        public bool IsMuted => Saturation < 0.12;
    }

    /// <summary>
    /// Horizon tilts below this are left alone; above <see cref="MaxTilt"/> the detection is more likely a diagonal in
    /// the scene than a crooked camera.
    /// </summary>
    public const double MinTilt = 0.4, MaxTilt = 20.0;

    /// <summary>
    /// Everything at once, on a downscaled copy (the analysis needs ~1 MP at most). Faces and text come from the app's
    /// detector and recognizer (<see cref="OnDeviceServices"/> unless given); without them there are none.
    /// </summary>
    public static async Task<Insights> InsightsAsync(RGBAImage image, IFaceDetector? faceDetector = null, IImageTextRecognizer? textRecognizer = null,
                                                     CancellationToken ct = default)
    {
        faceDetector ??= OnDeviceServices.FaceDetector;
        textRecognizer ??= OnDeviceServices.TextRecognizer;
        var small = Downscaled(image, 1024);
        var result = await Task.Run(() => new Insights
        {
            Tone = ToneStatsOf(small),
            Enhance = EnhanceRecipeFor(small),
            // The detector reports the angle with Glimmer's sign already (Vision's is the other way round: a horizon
            // rising to the right is negative there, so the Mac negates it).
            HorizonDegrees = ClassicVision.HorizonDegrees(small) is { } angle ? UsableTilt(angle) : null,
            SalientRect = ClassicVision.SalientRect(small),
        }, ct).ConfigureAwait(false);
        if (faceDetector is not null)
        {
            var faces = await faceDetector.DetectAsync(small, ct).ConfigureAwait(false);
            double w = small.Width, h = small.Height;
            var rects = faces.Select(f => new CGRectLike(f.MinX / w, f.MinY / h, f.MaxX / w, f.MaxY / h)).ToImmutableArray();
            result = result with { FaceRects = rects, FaceCount = rects.Length };
        }
        if (textRecognizer is not null)
        {
            // The Mac reads with the accurate level and a minimum text height of 2 % of the picture, and counts lines
            // with a confidence above one half.
            var lines = await textRecognizer.RecognizeAsync(small, ct).ConfigureAwait(false);
            var words = lines.Where(l => l.Confidence > 0.5 && l.Box.Height >= small.Height * 0.02);
            result = result with { HasText = words.Sum(l => l.String.Length) >= 6 };
        }
        return result;
    }

    /// <summary>A detected tilt worth correcting, or null.</summary>
    public static double? UsableTilt(double degrees) =>
        Math.Abs(degrees) >= MinTilt && Math.Abs(degrees) <= MaxTilt ? degrees : null;

    // MARK: Auto Enhance

    /// <summary>
    /// Automatic corrections, expressed as Glimmer adjustments so they land as editable layers instead of baked pixels.
    /// (Core Image's on the Mac; <see cref="AutoAdjustments"/> here.)
    /// </summary>
    public sealed record EnhanceRecipe
    {
        /// <summary>RGB curve (sRGB values, like the Curves adjustment).</summary>
        public ImmutableArray<CurvePoint> Curve { get; init; } = CurveMath.Identity;
        public double Vibrance { get; init; }
        /// <summary>Colour cast of the neutrals (see <see cref="ColorCast"/>).</summary>
        public Vec3? ColorCast { get; init; }

        /// <summary>Auto Enhance only corrects a clear cast; Auto Color handles subtle ones.</summary>
        public const double CastThreshold = 0.03;

        public bool CorrectsCast => IsNoticeableCast(ColorCast, CastThreshold);

        /// <summary>Whether applying it would visibly change the image.</summary>
        public bool IsNoticeable => Curve.Any(p => Math.Abs(p.Y - p.X) > 0.025) || Math.Abs(Vibrance) > 0.08 || CorrectsCast;

        public IReadOnlyList<Adjustment> Adjustments
        {
            get
            {
                var list = new List<Adjustment>();
                if (Curve.Any(p => Math.Abs(p.Y - p.X) > 0.005))
                {
                    list.Add(new Adjustment(AdjustmentKind.Curves)
                    {
                        Curves = ImmutableDictionary<CurveChannel, ImmutableArray<CurvePoint>>.Empty.Add(CurveChannel.Rgb, Curve),
                    });
                }
                if (CorrectsCast && ColorCast is { } cast) list.Add(CastCorrection(cast));
                if (Math.Abs(Vibrance) > 0.01)
                    list.Add(new Adjustment(AdjustmentKind.Vibrance) { Numbers = ImmutableDictionary<string, double>.Empty.Add("vibrance", Vibrance) });
                return list;
            }
        }

        public bool Equals(EnhanceRecipe? other) =>
            other is not null && Curve.SequenceEqual(other.Curve) && Vibrance == other.Vibrance && ColorCast == other.ColorCast;

        public override int GetHashCode() => HashCode.Combine(Curve.Length, Vibrance, ColorCast);
    }

    public static EnhanceRecipe EnhanceRecipeFor(RGBAImage image)
    {
        var auto = AutoAdjustments(image);
        var curve = auto.Curve.Count < 2 ? CurveMath.Identity : auto.Curve.ToImmutableArray();
        return new EnhanceRecipe
        {
            Curve = Folded(curve, auto.Shadows, auto.Highlights).ToImmutableArray(),
            Vibrance = Math.Min(Math.Max(auto.Vibrance, -1), 1),
            ColorCast = ColorCast(image),
        };
    }

    /// <summary>
    /// What the Mac gets from <c>CIImage.autoAdjustmentFilters</c> (CIToneCurve's five points, CIVibrance's amount,
    /// CIHighlightShadowAdjust's shadow and highlight amounts), worked out from the luminance histogram instead. Core
    /// Image's own method isn't published; this one, on a 256-px copy:
    /// - black and white points from the 0.5th and 99.5th percentiles, moved half of the way to 0 and 1 (not at all
    ///   when they are within 0.03 of it);
    /// - a gamma that takes the mean 70 % of the way (in log terms) into 0.40…0.55, limited to 0.55…1.25: dark
    ///   pictures are lifted more readily than bright ones are darkened (a page or a snowfield is meant to be light);
    /// - vibrance when the mean saturation is below 0.25 but there is colour to bring out (up to +0.4; none for a
    ///   picture that is practically grey);
    /// - a shadow amount from the share of pixels darker than 0.15, a highlight amount below 1 from the share brighter
    ///   than 0.95 (both then folded into the curve exactly as the Mac folds Core Image's).
    /// It never balances skin tones on faces (CIFaceBalance) and has no red-eye step; Glimmer turns red-eye off on the
    /// Mac too.
    /// </summary>
    internal static (IReadOnlyList<CurvePoint> Curve, double Vibrance, double Shadows, double Highlights) AutoAdjustments(RGBAImage image)
    {
        int w = Math.Min(image.Width, 256), h = Math.Max(1, (int)((double)image.Height * w / Math.Max(image.Width, 1)));
        var px = PixelSampling.Draw8(image, w, h);
        var histogram = new int[256];
        double sum = 0, sat = 0;
        int n = 0, dark = 0, bright = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] <= 8) continue;
            double r = px[i], g = px[i + 1], b = px[i + 2];
            double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            histogram[Math.Min(255, (int)y)]++;
            sum += y;
            double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            sat += mx > 0 ? (mx - mn) / mx : 0;
            if (y < 0.15 * 255) dark++;
            if (y > 0.95 * 255) bright++;
            n++;
        }
        if (n == 0) return (CurveMath.Identity, 0, 0, 1);
        double Percentile(double p)
        {
            int target = (int)(n * p), acc = 0;
            for (int v = 0; v < 256; v++)
            {
                acc += histogram[v];
                if (acc > target) return v / 255.0;
            }
            return 1;
        }
        double black = Percentile(0.005), white = Percentile(0.995);
        double low = black <= 0.03 ? 0 : Math.Min(black, 0.25) * 0.5;
        double high = white >= 0.97 ? 1 : 1 - (1 - Math.Max(white, 0.75)) * 0.5;
        double mean = Math.Clamp((sum / n / 255 - low) / Math.Max(high - low, 1e-3), 1e-3, 0.999);
        double target = Math.Clamp(mean, 0.40, 0.55);
        double gamma = Math.Clamp(Math.Log(target) / Math.Log(mean), 0.55, 1.25);
        gamma = Math.Pow(gamma, 0.7);
        var curve = new List<CurvePoint>();
        foreach (var x in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            double t = Math.Clamp((x - low) / (high - low), 0, 1);
            curve.Add(new CurvePoint(x, Math.Pow(t, gamma)));
        }
        double saturation = sat / n;
        double vibrance = saturation < 0.25 ? Math.Min(0.4, (0.25 - saturation) * 1.6) * Math.Clamp((saturation - 0.03) / 0.07, 0, 1) : 0;
        double shadows = Math.Clamp(((double)dark / n - 0.1) * 2, 0, 0.6);
        double highlights = 1 - Math.Clamp(((double)bright / n - 0.05) * 2, 0, 0.5);
        return (curve, vibrance, shadows, highlights);
    }

    /// <summary>
    /// Core Image's shadow/highlight step is local; Glimmer approximates it globally by lifting the dark end and easing
    /// the bright end of the curve.
    /// </summary>
    internal static IReadOnlyList<CurvePoint> Folded(IReadOnlyList<CurvePoint> curve, double shadows, double highlights)
    {
        double lift = Math.Min(Math.Max(shadows, 0), 1) * 0.1, ease = Math.Min(Math.Max(1 - highlights, 0), 1) * 0.08;
        if (!(lift > 0.001 || ease > 0.001)) return curve;
        var points = curve.OrderBy(p => p.X).ToArray();
        for (int i = 0; i < points.Length; i++)
        {
            if (!(points[i].X > 0 && points[i].X < 1)) continue;
            double x = points[i].X, y = points[i].Y;
            y += lift * Math.Pow(1 - x, 2) * 4 * x;  // peaks in the shadows, zero at both ends
            y -= ease * Math.Pow(x, 2) * 4 * (1 - x);
            points[i] = points[i] with { Y = Math.Min(Math.Max(y, 0), 1) };
        }
        // Keep it monotone.
        for (int i = 1; i < points.Length; i++) points[i] = points[i] with { Y = Math.Max(points[i].Y, points[i - 1].Y) };
        return points;
    }

    // MARK: Crop suggestion

    private static double Round(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>
    /// A crop that frames the salient part of the image, in pixels (y down). With an aspect that differs from the
    /// image's, the largest box of that aspect slides over the subject; without one, the box tightens around it at the
    /// image's own proportions. null when the whole image is the best crop. With <paramref name="faces"/>, the crop
    /// frames the people instead: heads and shoulders, with the faces near the upper third.
    /// </summary>
    public static CGRectLike? SuggestedCrop(CGRectLike salient, int imageWidth, int imageHeight, double? aspect,
                                            IReadOnlyList<CGRectLike>? faces = null)
    {
        double w = imageWidth, h = imageHeight;
        if (!(w > 0 && h > 0)) return null;
        if (faces is { Count: > 0 }) return PortraitCrop(faces, w, h, aspect);
        if (!(salient.Width > 0 && salient.Height > 0)) return null;
        var s = Rects.Make(salient.MinX * w, salient.MinY * h, salient.Width * w, salient.Height * h);
        double imageAspect = w / h;
        double a = aspect ?? imageAspect;
        double bw, bh;
        if (a > imageAspect) { bw = w; bh = w / a; } else { bh = h; bw = h * a; }
        if (aspect is null || Math.Abs(a - imageAspect) < 0.01)
        {
            // Tighten: the subject plus breathing room, never below 55 % of the frame.
            double k = Math.Min(1, Math.Max(0.55, Math.Max(s.Width * 1.5 / bw, s.Height * 1.5 / bh)));
            if (!(k < 0.95)) return null;
            bw *= k;
            bh *= k;
        }
        // Centre on the subject, nudged so it sits on the nearer third line.
        double cx = s.MidX(), cy = s.MidY();
        if (s.Width < bw * 0.5) cx += (s.MidX() < w / 2 ? 1 : -1) * bw / 6 * 0.5;
        if (s.Height < bh * 0.5) cy += bh / 6 * 0.5;  // a little headroom
        double x = Math.Min(Math.Max(cx - bw / 2, 0), w - bw), y = Math.Min(Math.Max(cy - bh / 2, 0), h - bh);
        return Rects.Make(Round(x), Round(y), Round(bw), Round(bh));
    }

    internal static CGRectLike? PortraitCrop(IReadOnlyList<CGRectLike> faces, double w, double h, double? aspect)
    {
        CGRectLike? union = null;
        foreach (var face in faces) union = Rects.Union(union, Rects.Make(face.MinX * w, face.MinY * h, face.Width * w, face.Height * h));
        var f = union!.Value;
        double fh = faces.Max(face => face.Height * h), fw = faces.Max(face => face.Width * w);
        // Head and shoulders around the faces.
        var want = Rects.Make(f.MinX - fw * 0.9, f.MinY - fh * 0.6, f.Width + fw * 1.8, f.Height + fh * 2.4);
        double a = aspect ?? (w / h);
        double bw = Math.Max(want.Width, want.Height * a), bh = bw / a;
        // Never larger than the image.
        double k = Math.Min(1, Math.Min(w / bw, h / bh));
        bw *= k;
        bh *= k;
        if (!(bw * bh < w * h * 0.92 || aspect is not null)) return null;
        // Faces' centre at 38 % of the box height, horizontally centred.
        double cx = f.MidX(), top = f.MidY() - bh * 0.38;
        double x = Math.Min(Math.Max(cx - bw / 2, 0), w - bw), y = Math.Min(Math.Max(top, 0), h - bh);
        return Rects.Make(Round(x), Round(y), Round(bw), Round(bh));
    }

    // MARK: Helpers

    internal static ToneStats ToneStatsOf(RGBAImage image)
    {
        int w = Math.Min(image.Width, 256), h = Math.Max(1, (int)((double)image.Height * w / Math.Max(image.Width, 1)));
        var px = PixelSampling.Draw8(image, w, h);
        var histogram = new int[256];
        double sum = 0, sat = 0;
        int n = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] <= 8) continue;
            double r = px[i], g = px[i + 1], b = px[i + 2];
            double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            histogram[Math.Min(255, (int)y)]++;
            sum += y;
            double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            sat += mx > 0 ? (mx - mn) / mx : 0;
            n++;
        }
        if (n == 0) return new ToneStats();
        double Percentile(double p)
        {
            int target = (int)(n * p), acc = 0;
            for (int v = 0; v < 256; v++)
            {
                acc += histogram[v];
                if (acc > target) return v / 255.0;
            }
            return 1;
        }
        return new ToneStats { Mean = sum / n / 255, Low = Percentile(0.02), High = Percentile(0.98), Saturation = sat / n };
    }

    /// <summary>A copy whose longer side is at most <paramref name="maxSide"/> (the image itself when it already is).</summary>
    internal static RGBAImage Downscaled(RGBAImage image, int maxSide)
    {
        var (w, h) = PixelSampling.Fit(image.Width, image.Height, maxSide);
        return PixelSampling.Resized(image, w, h);
    }
}
