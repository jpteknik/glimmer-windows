// New file, no Swift original: on the Mac, CIRAWFilter develops white balance, exposure, shadows and contrast inside
// Apple's decoder. On Windows the system decoder gives the picture as shot, and Glimmer develops these settings on it,
// in linear light, with published colour science:
//   - the Planckian locus in CIE 1960 uv by M. Krystek's rational approximation ("An algorithm to calculate correlated
//     colour temperature", Color Research & Application 10 (1), 1985);
//   - tint on Adobe's scale (its DNG SDK, dng_temperature.cpp): 3000 × the uv distance of the light from the locus,
//     positive for a light greener than the locus, which a development turns magenta (as a camera's fluorescent preset
//     has a positive tint);
//   - white balance as a von Kries adaptation in the Bradford cone space (K. M. Lam, 1985; the matrix as in ICC.1:2010
//     Annex E);
//   - sRGB's primaries and white (IEC 61966-2-1).
// The tone curves for shadows and contrast are Glimmer's own and are described where they are made.
#if GLIMMER_RAW   // Directory.Build.props switches this (Config/Features.xcconfig on the Mac)
using System.Numerics;
using Glimmer.Render;

namespace Glimmer.Formats;

internal static class RawColor
{
    private static readonly double[] XyzFromRgb = [0.4124564, 0.3575761, 0.1804375, 0.2126729, 0.7151522, 0.0721750, 0.0193339, 0.1191920, 0.9503041];
    private static readonly double[] RgbFromXyz = SourceColor.Invert(XyzFromRgb);
    private static readonly double[] Bradford = [0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296];
    private static readonly double[] BradfordInverse = SourceColor.Invert(Bradford);
    private static readonly double[] D65 = [0.95047, 1.0, 1.08883];

    /// <summary>Luminance of linear sRGB.</summary>
    public static readonly Vector3 Luminance = new(0.2126729f, 0.7151522f, 0.0721750f);

    /// <summary>The temperature and tint a picture is taken to be as shot when the decoder doesn't say: daylight.</summary>
    public const double ReferenceTemperature = 6500, ReferenceTint = 0;

    /// <summary>The Planckian locus at <paramref name="t"/> kelvin, CIE 1960 uv (Krystek's approximation).</summary>
    public static (double U, double V) Locus(double t)
    {
        double u = (0.860117757 + 1.54118254e-4 * t + 1.28641212e-7 * t * t) / (1 + 8.42420235e-4 * t + 7.08145163e-7 * t * t);
        double v = (0.317398726 + 4.22806245e-5 * t + 4.20481691e-8 * t * t) / (1 - 2.89741816e-5 * t + 1.61456053e-7 * t * t);
        return (u, v);
    }

    /// <summary>The unit normal of the locus at <paramref name="t"/>, pointing away from it towards green (higher v).</summary>
    private static (double U, double V) GreenNormal(double t)
    {
        double step = Math.Max(1, t * 1e-4);
        var (u1, v1) = Locus(t - step);
        var (u2, v2) = Locus(t + step);
        double du = u2 - u1, dv = v2 - v1, length = Math.Sqrt(du * du + dv * dv);
        return (dv / length, -du / length);
    }

    /// <summary>The white of a temperature and tint, uv: on the locus, moved off it by tint / 3000 (towards green for a
    /// positive tint).</summary>
    public static (double U, double V) White(double temperature, double tint)
    {
        var (u, v) = Locus(temperature);
        var (nu, nv) = GreenNormal(temperature);
        double duv = tint / 3000;
        return (u + duv * nu, v + duv * nv);
    }

    /// <summary>XYZ with Y = 1 of a uv chromaticity.</summary>
    private static double[] Xyz((double U, double V) uv)
    {
        double d = 2 * uv.U - 8 * uv.V + 4;
        double x = 3 * uv.U / d, y = 2 * uv.V / d;
        return [x / y, 1, (1 - x - y) / y];
    }

    private static (double U, double V) Uv(double[] xyz)
    {
        double sum = xyz[0] + xyz[1] + xyz[2];
        return UvOfXy(xyz[0] / sum, xyz[1] / sum);
    }

    /// <summary>CIE 1960 uv of a CIE 1931 xy chromaticity.</summary>
    public static (double U, double V) UvOfXy(double x, double y)
    {
        double d = -2 * x + 12 * y + 3;
        return (4 * x / d, 6 * y / d);
    }

    /// <summary>The temperature and tint whose white is <paramref name="uv"/>: the nearest point of the locus (searched in
    /// mireds), and the distance from it.</summary>
    public static (double Temperature, double Tint) TemperatureTint((double U, double V) uv)
    {
        double Distance(double mired)
        {
            var (u, v) = Locus(1e6 / mired);
            return (u - uv.U) * (u - uv.U) + (v - uv.V) * (v - uv.V);
        }
        // Golden-section search over 10…1000 mireds (100 000…1000 K).
        double lo = 10, hi = 1000, g = (Math.Sqrt(5) - 1) / 2;
        double a = hi - g * (hi - lo), b = lo + g * (hi - lo), fa = Distance(a), fb = Distance(b);
        for (int i = 0; i < 80; i++)
        {
            if (fa < fb) { hi = b; b = a; fb = fa; a = hi - g * (hi - lo); fa = Distance(a); }
            else { lo = a; a = b; fa = fb; b = lo + g * (hi - lo); fb = Distance(b); }
        }
        double t = 1e6 / ((lo + hi) / 2);
        var (lu, lv) = Locus(t);
        var (nu, nv) = GreenNormal(t);
        double duv = (uv.U - lu) * nu + (uv.V - lv) * nv;
        return (t, duv * 3000);
    }

    private static double[] Multiply(double[] m, double[] v) =>
        [m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[3] * v[0] + m[4] * v[1] + m[5] * v[2], m[6] * v[0] + m[7] * v[1] + m[8] * v[2]];

    /// <summary>
    /// The linear sRGB matrix that turns a picture rendered with white balance (<paramref name="t0"/>,
    /// <paramref name="tint0"/>) into the same picture rendered with (<paramref name="t1"/>, <paramref name="tint1"/>):
    /// the cone responses scaled by the old white over the new one. A lower temperature than the picture was rendered
    /// with cools it, a higher one warms it. Scaled so that white keeps its luminance.
    /// </summary>
    public static float[] Balance(double t0, double tint0, double t1, double tint1)
    {
        var l0 = Multiply(Bradford, Xyz(White(t0, tint0)));
        var l1 = Multiply(Bradford, Xyz(White(t1, tint1)));
        var gain = new double[9];
        for (int i = 0; i < 3; i++) gain[i * 4] = l0[i] / l1[i];
        var m = SourceColor.Multiply(RgbFromXyz, SourceColor.Multiply(BradfordInverse, SourceColor.Multiply(gain, SourceColor.Multiply(Bradford, XyzFromRgb))));
        double white = Luminance.X * (m[0] + m[1] + m[2]) + Luminance.Y * (m[3] + m[4] + m[5]) + Luminance.Z * (m[6] + m[7] + m[8]);
        return [.. m.Select(v => (float)(v / white))];
    }

    /// <summary>The white balance that makes <paramref name="colour"/> (linear sRGB, from a picture rendered with
    /// (<paramref name="t0"/>, <paramref name="tint0"/>)) grey.</summary>
    public static (double Temperature, double Tint) Neutral(Vector3 colour, double t0, double tint0)
    {
        var lp = Multiply(Bradford, Multiply(XyzFromRgb, [colour.X, colour.Y, colour.Z]));
        var l0 = Multiply(Bradford, Xyz(White(t0, tint0)));
        var ld = Multiply(Bradford, D65);
        double[] l1 = [l0[0] * lp[0] / ld[0], l0[1] * lp[1] / ld[1], l0[2] * lp[2] / ld[2]];
        if (l1.Any(v => !(v > 0) || !double.IsFinite(v))) return (t0, tint0);
        return TemperatureTint(Uv(Multiply(BradfordInverse, l1)));
    }

    /// <summary>
    /// The tone change for shadows and contrast, on luminance <paramref name="y"/> (0…1, linear), in sRGB-encoded values
    /// L: shadows lift L by <paramref name="shadows"/> × L (1 − L)² (−1…1: 0 leaves it, positive lifts, negative deepens;
    /// steepest near black, nothing at black and white), then contrast moves L away from the middle by
    /// <paramref name="contrast"/> × (L − ½) × 4 L (1 − L) (−½…½). Both keep black and white and never fold back.
    /// </summary>
    public static float Tone(float y, float shadows, float contrast)
    {
        float l = SrgbTransfer.EncodeFast(Math.Clamp(y, 0f, 1f));
        l += shadows * l * (1 - l) * (1 - l);
        l += contrast * (l - 0.5f) * 4 * l * (1 - l);
        return SrgbTransfer.DecodeFast(Math.Clamp(l, 0f, 1f));
    }
}
#endif
