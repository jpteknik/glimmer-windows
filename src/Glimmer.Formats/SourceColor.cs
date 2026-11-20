// New file, no Swift original. On the Mac, CoreGraphics converts a picture's colours as it draws the picture into
// Glimmer's working space (extended linear sRGB, ImageImporter.workingColorSpace). Here a decoded picture keeps its
// samples as the file encodes them, and this says how they become linear sRGB: a transfer curve per sample, then,
// for other primaries, a 3 × 3 matrix. sRGB itself is converted exactly (the curve in double precision, once per
// possible sample value); the Mac's CoreGraphics rounds 8-bit sRGB to steps of 1/4096 on the way, which is not copied.
using System.Text;
using Glimmer.Core;
using SkiaSharp;

namespace Glimmer.Formats;

internal sealed class SourceColor
{
    private readonly Func<double, double> transfer;
    private readonly float[]? toSrgb;   // row-major 3 × 3: linear source RGB → linear sRGB; null when the primaries are sRGB's
    private float[]? lut8, lut16, lutHalf;

    private SourceColor(Func<double, double> transfer, float[]? toSrgb, bool isLinear)
    {
        this.transfer = transfer;
        this.toSrgb = toSrgb;
        IsLinear = isLinear;
    }

    /// <summary>The samples are linear already: the curve is the identity.</summary>
    public bool IsLinear { get; }

    public bool HasMatrix => toSrgb is not null;

    /// <summary>sRGB, exactly (<see cref="ColorMath.SrgbToLinear(double)"/>).</summary>
    public static SourceColor Srgb { get; } = new(ColorMath.SrgbToLinear, null, isLinear: false);

    /// <summary>Linear sRGB: the samples are what Glimmer keeps.</summary>
    public static SourceColor LinearSrgb { get; } = new(x => x, null, isLinear: true);

    /// <summary>Grey with a gamma of 2.2, what the Mac assumes for a grey picture with no profile of its own
    /// (<c>CGColorSpace.genericGrayGamma2_2</c>).</summary>
    public static SourceColor GrayGamma22 { get; } = new(x => x < 0 ? -Math.Pow(-x, 2.2) : Math.Pow(x, 2.2), null, isLinear: false);

    /// <summary>Linear light with other primaries (a 32-bit Photoshop file in its profile's primaries).</summary>
    public SourceColor AsLinear() => new(x => x, toSrgb, isLinear: true);

    public double Linear(double encoded) => transfer(encoded);

    /// <summary>The linear value of every 8-bit sample.</summary>
    public float[] Lut8 => lut8 ??= Table(256, i => i / 255.0);

    /// <summary>The linear value of every 16-bit sample.</summary>
    public float[] Lut16 => lut16 ??= Table(65536, i => i / 65535.0);

    /// <summary>The linear value of every half-precision sample, by its bits.</summary>
    public float[] LutHalf => lutHalf ??= Table(65536, i => (double)BitConverter.UInt16BitsToHalf((ushort)i));

    private float[] Table(int count, Func<int, double> value)
    {
        var table = new float[count];
        for (int i = 0; i < count; i++)
        {
            double v = value(i);
            table[i] = double.IsFinite(v) ? Sanitize((float)transfer(v)) : 0;
        }
        return table;
    }

    /// <summary>No NaN, and nothing a half-precision tile can't hold.</summary>
    internal static float Sanitize(float v) => float.IsNaN(v) ? 0 : Math.Clamp(v, -65504f, 65504f);

    /// <summary>Linear source primaries to linear sRGB (nothing to do when they are sRGB's).</summary>
    public void ToSrgb(ref float r, ref float g, ref float b)
    {
        if (toSrgb is not { } m) return;
        float x = m[0] * r + m[1] * g + m[2] * b;
        float y = m[3] * r + m[4] * g + m[5] * b;
        float z = m[6] * r + m[7] * g + m[8] * b;
        r = Sanitize(x); g = Sanitize(y); b = Sanitize(z);
    }

    // MARK: From Skia's colour spaces and from ICC profiles

    /// <summary>
    /// The conversion for a colour space Skia decoded or parsed: exact for sRGB, a numerical curve and matrix for other
    /// matrix/curve spaces. Null when Skia can't describe the space with numbers (PQ, HLG, a table profile it couldn't
    /// approximate): such a picture is converted by Skia itself.
    /// </summary>
    public static SourceColor? FromSkia(SKColorSpace? space)
    {
        if (space is null) return null;
        if (space.IsSrgb) return Srgb;
        if (!space.GetNumericalTransferFunction(out var fn) || !space.ToColorSpaceXyz(out var xyz)) return null;
        if (!(fn.G > 0)) return null;   // PQ and HLG are written as curves with a negative exponent
        var m = Multiply(SrgbFromXyz.Value, ToArray(xyz));
        bool sameMatrix = IsIdentity(m);
        bool linear = Math.Abs(fn.G - 1) < 1e-6 && Math.Abs(fn.A - 1) < 1e-6 && Math.Abs(fn.B) < 1e-6 && Math.Abs(fn.E) < 1e-6
                      && Math.Abs(fn.D) < 1e-6;
        if (sameMatrix && IsSrgbCurve(fn)) return Srgb;
        double g = fn.G, a = fn.A, b = fn.B, c = fn.C, d = fn.D, e = fn.E, f = fn.F;
        Func<double, double> curve = x =>
        {
            // Skia's and ICC's parametric curve, mirrored for negative values as Skia evaluates it.
            double sign = x < 0 ? -1 : 1, ax = Math.Abs(x);
            return sign * (ax < d ? c * ax + f : Math.Pow(Math.Max(a * ax + b, 0), g) + e);
        };
        return new SourceColor(linear ? x => x : curve, sameMatrix ? null : ToFloats(m), linear);
    }

    /// <summary>
    /// The conversion for an embedded ICC profile of the expected kind (an RGB profile for colour samples, a grey one
    /// for grey), or null when the profile is of another kind or can't be read. Grey profiles are read here (Skia's
    /// colour spaces are RGB only): their one curve, which ICC's profile connection space applies to the grey axis.
    /// </summary>
    public static SourceColor? FromIcc(ReadOnlySpan<byte> icc, bool gray)
    {
        if (icc.Length < 132) return null;
        string kind = Encoding.ASCII.GetString(icc.Slice(16, 4));
        if (gray) return kind == "GRAY" ? GrayFromIcc(icc) : null;
        if (kind != "RGB ") return null;
        // A profile Skia can't parse gives no profile object, and SKColorSpace.CreateIcc throws on that: asked in two
        // steps, so that a damaged profile is "can't be read" (the Mac's CGColorSpace(iccData:) gives nil).
        using var profile = SKColorSpaceIccProfile.Create(icc.ToArray());
        if (profile is null) return null;
        using var space = SKColorSpace.CreateIcc(profile);
        return FromSkia(space);
    }

    private static SourceColor? GrayFromIcc(ReadOnlySpan<byte> icc)
    {
        static uint ReadU32(ReadOnlySpan<byte> bytes, int at) => (uint)(bytes[at] << 24 | bytes[at + 1] << 16 | bytes[at + 2] << 8 | bytes[at + 3]);
        long tags = ReadU32(icc, 128);
        if (tags > (icc.Length - 132) / 12) return null;
        for (int i = 0; i < tags; i++)
        {
            int entry = 132 + i * 12;
            if (Encoding.ASCII.GetString(icc.Slice(entry, 4)) != "kTRC") continue;
            long offset = ReadU32(icc, entry + 4), size = ReadU32(icc, entry + 8);
            if (offset < 128 || size < 12 || offset + size > icc.Length) return null;
            var curve = Curve(icc.Slice((int)offset, (int)size));
            return curve is null ? null : new SourceColor(curve, null, isLinear: false);
        }
        return null;
    }

    /// <summary>An ICC curve ('curv': identity, a gamma or a table; 'para': the five function types), or null.</summary>
    private static Func<double, double>? Curve(ReadOnlySpan<byte> tag)
    {
        string type = Encoding.ASCII.GetString(tag[..4]);
        if (type == "curv")
        {
            long count = (uint)(tag[8] << 24 | tag[9] << 16 | tag[10] << 8 | tag[11]);
            if (count == 0) return x => x;
            if (12 + count * 2 > tag.Length || count > 65536) return null;
            if (count == 1)
            {
                double gamma = (tag[12] << 8 | tag[13]) / 256.0;
                return gamma > 0 ? x => x < 0 ? -Math.Pow(-x, gamma) : Math.Pow(x, gamma) : null;
            }
            var table = new double[count];
            for (int i = 0; i < count; i++) table[i] = (tag[12 + i * 2] << 8 | tag[13 + i * 2]) / 65535.0;
            return x =>
            {
                double position = Math.Clamp(x, 0, 1) * (table.Length - 1);
                int at = Math.Min((int)position, table.Length - 2);
                return table[at] + (table[at + 1] - table[at]) * (position - at);
            };
        }
        if (type == "para")
        {
            if (tag.Length < 12) return null;
            int function = tag[8] << 8 | tag[9];
            int needed = function switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => -1 };
            if (needed < 0 || tag.Length < 12 + needed * 4) return null;
            var p = new double[7];
            for (int i = 0; i < needed; i++) p[i] = (tag[12 + i * 4] << 24 | tag[13 + i * 4] << 16 | tag[14 + i * 4] << 8 | tag[15 + i * 4]) / 65536.0;
            double g = p[0], a = p[1], b = p[2], c = p[3], d = p[4], e = p[5], f = p[6];
            if (!(g > 0)) return null;
            double Pow(double v) => Math.Pow(Math.Max(v, 0), g);
            return function switch
            {
                0 => x => Pow(x),
                1 => x => a != 0 && x >= -b / a ? Pow(a * x + b) : 0,
                2 => x => a != 0 && x >= -b / a ? Pow(a * x + b) + c : c,
                3 => x => x >= d ? Pow(a * x + b) : c * x,
                _ => x => x >= d ? Pow(a * x + b) + e : c * x + f,
            };
        }
        return null;
    }

    // MARK: Matrices

    /// <summary>XYZ (D50, the ICC connection space) to linear sRGB: the inverse of Skia's sRGB matrix, so every
    /// conversion here meets Skia's adaptation to D50.</summary>
    internal static readonly Lazy<double[]> SrgbFromXyz = new(() => Invert(ToArray(SKColorSpaceXyz.Srgb)));

    /// <summary>Row-major. (SkiaSharp's indexer is <c>[column, row]</c>; <c>Values</c> is row after row.)</summary>
    private static double[] ToArray(SKColorSpaceXyz m) => [.. m.Values.Select(v => (double)v)];

    private static float[] ToFloats(double[] m) => [.. m.Select(v => (float)v)];

    internal static double[] Multiply(double[] a, double[] b)
    {
        var m = new double[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        return m;
    }

    internal static double[] Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return
        [
            (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
            (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
            (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det,
        ];
    }

    private static bool IsIdentity(double[] m)
    {
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                if (Math.Abs(m[r * 3 + c] - (r == c ? 1 : 0)) > 1e-4) return false;
        return true;
    }

    private static bool IsSrgbCurve(SKColorSpaceTransferFn fn)
    {
        var s = SKColorSpaceTransferFn.Srgb;
        return Math.Abs(fn.G - s.G) < 1e-4 && Math.Abs(fn.A - s.A) < 1e-4 && Math.Abs(fn.B - s.B) < 1e-4 && Math.Abs(fn.C - s.C) < 1e-4
               && Math.Abs(fn.D - s.D) < 1e-4 && Math.Abs(fn.E - s.E) < 1e-4 && Math.Abs(fn.F - s.F) < 1e-4;
    }
}
