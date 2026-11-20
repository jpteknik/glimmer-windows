// Ported from the colour conversions in Packages/GlimmerKit/Sources/GlimmerCore/Adjustments.swift (SRGBColor.linear)
// and StatusKind.swift (luminance), gathered in one place so every package converts the same way.

using System.Numerics;

namespace Glimmer.Core;

/// <summary>
/// sRGB transfer functions. Glimmer keeps pixels as premultiplied, linear, extended sRGB; colours a person picks are
/// straight sRGB (<see cref="SRGBColor"/>).
/// </summary>
public static class ColorMath
{
    /// <summary>
    /// sRGB-encoded → linear, exactly as <c>SRGBColor.linear</c> on the Mac: values at or below 0.04045 (negative ones
    /// included) take the linear segment.
    /// </summary>
    public static double SrgbToLinear(double c) =>
        c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    public static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    /// <summary>Linear → sRGB-encoded (the inverse of <see cref="SrgbToLinear(double)"/>).</summary>
    public static double LinearToSrgb(double c) =>
        c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;

    public static float LinearToSrgb(float c) =>
        c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;

    /// <summary>
    /// Extended sRGB → linear: the curve mirrored for negative values (Apple's <c>extendedSRGB</c> colour space, in
    /// which Glimmer's pixels live).
    /// </summary>
    public static float ExtendedSrgbToLinear(float c) => c < 0 ? -SrgbToLinear(-c) : SrgbToLinear(c);

    /// <summary>Linear → extended sRGB, mirrored for negative values.</summary>
    public static float LinearToExtendedSrgb(float c) => c < 0 ? -LinearToSrgb(-c) : LinearToSrgb(c);

    public static double ExtendedSrgbToLinear(double c) => c < 0 ? -SrgbToLinear(-c) : SrgbToLinear(c);
    public static double LinearToExtendedSrgb(double c) => c < 0 ? -LinearToSrgb(-c) : LinearToSrgb(c);

    /// <summary>A straight sRGB colour with an opacity as Glimmer's pixel format: premultiplied linear RGBA.</summary>
    public static Vector4 PremultipliedLinear(SRGBColor color, double alpha = 1)
    {
        var lin = color.Linear;
        float a = (float)alpha;
        return new Vector4(lin.X * a, lin.Y * a, lin.Z * a, a);
    }

    /// <summary>A premultiplied linear pixel back to straight sRGB and its alpha (0 alpha gives black).</summary>
    public static (SRGBColor Color, double Alpha) StraightSrgb(Vector4 premultipliedLinear)
    {
        float a = premultipliedLinear.W;
        if (a <= 0) return (SRGBColor.Black, 0);
        return (new SRGBColor(LinearToExtendedSrgb(premultipliedLinear.X / a),
                              LinearToExtendedSrgb(premultipliedLinear.Y / a),
                              LinearToExtendedSrgb(premultipliedLinear.Z / a)), a);
    }

    /// <summary>WCAG relative luminance of an sRGB colour (components 0…1).</summary>
    public static double RelativeLuminance(double red, double green, double blue) =>
        0.2126 * SrgbToLinear(red) + 0.7152 * SrgbToLinear(green) + 0.0722 * SrgbToLinear(blue);

    /// <summary>
    /// 256 entries: the linear value of each 8-bit sRGB code. Used when 8-bit pictures (rendered text, imported
    /// images) become tiles.
    /// </summary>
    public static ReadOnlySpan<float> SrgbByteToLinear => SrgbTable.Value;

    private static readonly Lazy<float[]> SrgbTable = new(() =>
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = (float)SrgbToLinear(i / 255.0);
        return t;
    });
}
