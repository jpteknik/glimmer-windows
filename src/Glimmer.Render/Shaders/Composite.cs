// Ported from Packages/GlimmerKit/Sources/GlimmerRender/Shaders/Composite.metal
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Glimmer.Render;

// Compositing shaders. Everything works on premultiplied, linear, extended-sRGB
// RGBA16Float tiles. Solid tiles are bound as 1×1 textures, so every source is
// simply sampled.
//
// On the CPU each Metal function is a static method with the same name in PascalCase; a fragment shader becomes a
// per-pixel function. The uniform structs live beside their Swift mirrors (LayerUniforms and AdjustUniforms in
// TilePipelines.cs, DabInstance in StrokeEngine.cs, ApplyUniforms in PixelOps.cs, EffectColorUniforms in
// LayerStyleRenderer.cs, LensBlurUniforms in LensBlur.cs).

internal static class CompositeShaders
{
    // ── Colour helpers ──────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 EncodeSRGB(Vector3 c) => SrgbTransfer.EncodeFast(c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 DecodeSRGB(Vector3 c) => SrgbTransfer.DecodeFast(c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Luma709(Vector3 c) => Vector3.Dot(c, new Vector3(0.2126f, 0.7152f, 0.0722f));

    // ── Blend math (W3C Compositing and Blending Level 1) ──────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lum(Vector3 c) => Vector3.Dot(c, new Vector3(0.3f, 0.59f, 0.11f));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 ClipColor(Vector3 c)
    {
        float l = Lum(c);
        float n = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float x = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        if (n < 0f) c = new Vector3(l) + (c - new Vector3(l)) * l / MathF.Max(l - n, 1e-6f);
        if (x > 1f) c = new Vector3(l) + (c - new Vector3(l)) * (1f - l) / MathF.Max(x - l, 1e-6f);
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 SetLum(Vector3 c, float l) => ClipColor(c + new Vector3(l - Lum(c)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sat(Vector3 c) =>
        MathF.Max(c.X, MathF.Max(c.Y, c.Z)) - MathF.Min(c.X, MathF.Min(c.Y, c.Z));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 SetSat(Vector3 c, float s)
    {
        float mx = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        float mn = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float range = mx - mn;
        if (range <= 1e-6f) return Vector3.Zero;
        return (c - new Vector3(mn)) * s / range;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SoftLightD(float b) => b <= 0.25f ? ((16f * b - 12f) * b + 4f) * b : MathF.Sqrt(b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ColorDodge(float b, float s)
    {
        if (b <= 0f) return 0f;
        if (s >= 1f) return 1f;
        return MathF.Min(1f, b / (1f - s));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ColorBurn(float b, float s)
    {
        if (b >= 1f) return 1f;
        if (s <= 0f) return 0f;
        return 1f - MathF.Min(1f, (1f - b) / s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HardLight(float b, float s) =>
        s <= 0.5f ? b * 2f * s : b + (2f * s - 1f) - b * (2f * s - 1f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SoftLight(float b, float s) =>
        s <= 0.5f ? b - (1f - 2f * s) * b * (1f - b) : b + (2f * s - 1f) * (SoftLightD(b) - b);

    // The Photoshop extras (not in W3C compositing): vivid, linear and pin light, hard mix,
    // subtract and divide, with the source as the light.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float VividLight(float b, float s) =>
        s <= 0.5f ? ColorBurn(b, 2f * s) : ColorDodge(b, 2f * (s - 0.5f));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float PinLight(float b, float s) =>
        s <= 0.5f ? MathF.Min(b, 2f * s) : MathF.Max(b, 2f * s - 1f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DivideBlend(float b, float s)
    {
        if (s <= 0f) return b > 0f ? 1f : 0f;
        return MathF.Min(1f, b / s);
    }

    /// <summary>B(Cb, Cs) for straight colours. <paramref name="mode"/> matches BlendMode.ShaderIndex.</summary>
    public static Vector3 BlendColor(Vector3 cb, Vector3 cs, int mode)
    {
        if (mode == 0) return cs;
        cb = ShaderMath.Saturate(cb);
        cs = ShaderMath.Saturate(cs);
        switch (mode)
        {
            case 1: return cb * cs;
            case 2: return cb + cs - cb * cs;
            case 3: return new(HardLight(cs.X, cb.X), HardLight(cs.Y, cb.Y), HardLight(cs.Z, cb.Z));
            case 4: return Vector3.Min(cb, cs);
            case 5: return Vector3.Max(cb, cs);
            case 6: return new(ColorDodge(cb.X, cs.X), ColorDodge(cb.Y, cs.Y), ColorDodge(cb.Z, cs.Z));
            case 7: return new(ColorBurn(cb.X, cs.X), ColorBurn(cb.Y, cs.Y), ColorBurn(cb.Z, cs.Z));
            case 8: return new(HardLight(cb.X, cs.X), HardLight(cb.Y, cs.Y), HardLight(cb.Z, cs.Z));
            case 9: return new(SoftLight(cb.X, cs.X), SoftLight(cb.Y, cs.Y), SoftLight(cb.Z, cs.Z));
            case 10: return Vector3.Abs(cb - cs);
            case 11: return cb + cs - 2f * cb * cs;
            case 12: return Vector3.Min(Vector3.One, cb + cs);
            case 13: return Vector3.Max(Vector3.Zero, cb + cs - Vector3.One);
            case 14: return SetLum(SetSat(cs, Sat(cb)), Lum(cb));
            case 15: return SetLum(SetSat(cb, Sat(cs)), Lum(cb));
            case 16: return SetLum(cs, Lum(cb));
            case 17: return SetLum(cb, Lum(cs));
            case 18: return new(VividLight(cb.X, cs.X), VividLight(cb.Y, cs.Y), VividLight(cb.Z, cs.Z));
            case 19: return ShaderMath.Saturate(cb + 2f * cs - Vector3.One);
            case 20: return new(PinLight(cb.X, cs.X), PinLight(cb.Y, cs.Y), PinLight(cb.Z, cs.Z));
            case 21: return new(ShaderMath.Step(1f, cb.X + cs.X), ShaderMath.Step(1f, cb.Y + cs.Y), ShaderMath.Step(1f, cb.Z + cs.Z));
            case 22: return Vector3.Max(Vector3.Zero, cb - cs);
            case 23: return new(DivideBlend(cb.X, cs.X), DivideBlend(cb.Y, cs.Y), DivideBlend(cb.Z, cs.Z));
            // Darker Color and Lighter Color: the whole colour of the darker (lighter) of the two, never a
            // mix of their channels. "Darker" is by luminance: measured on files from Photoshop
            // (2026-11-16), where blue wins over red and red over green, though their channel sums are equal.
            case 24: return Lum(cs) < Lum(cb) ? cs : cb;
            case 25: return Lum(cs) > Lum(cb) ? cs : cb;
            default: return cs;
        }
    }

    /// <summary>Composites premultiplied <paramref name="src"/> (already scaled by opacity/masks) onto <paramref name="dst"/>.</summary>
    public static Vector4 BlendOver(Vector4 dst, Vector4 src, int mode, bool gamma)
    {
        float @as = src.W;
        float ab = dst.W;
        if (@as <= 0f) return dst;

        // Normal in linear light is plain premultiplied "over": the formula below reduces to it.
        if (mode == 0 && !gamma)
        {
            if (ab > 0f) return src + dst * (1f - @as);
            return new Vector4(src.Xyz() * (@as / MathF.Max(@as, 1e-8f)), @as);
        }

        Vector3 cs = src.Xyz() / MathF.Max(@as, 1e-8f);
        Vector3 cb = ab > 0f ? dst.Xyz() / ab : Vector3.Zero;
        if (gamma) { cs = EncodeSRGB(cs); cb = EncodeSRGB(cb); }

        Vector3 mixed = (1f - ab) * cs + ab * BlendColor(cb, cs, mode);
        float ao = @as + ab * (1f - @as);
        Vector3 straight = (@as * mixed + (1f - @as) * ab * cb) / MathF.Max(ao, 1e-8f);
        if (gamma) straight = DecodeSRGB(straight);
        return new Vector4(straight * ao, ao);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float MaskValue(float sampled, float density) => 1f - density * (1f - ShaderMath.Saturate(sampled));

    /// <summary>
    /// <c>lut.sample(linearSampler, float2(lutCoord(t), 0.5))</c> for a lookup table of any width (1024 on the Mac):
    /// linear between entries, clamped at both ends.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 SampleLut(ReadOnlySpan<Vector4> lut, float t)
    {
        int last = lut.Length - 1;
        float x = ShaderMath.Saturate(t) * last;
        int i = (int)x;
        if (i >= last) return lut[last];
        float f = x - i;
        Vector4 a = lut[i];
        return a + (lut[i + 1] - a) * f;
    }

    // ── Layer compositing ──────────────────────────────────────────────────────

    public static float Hash21(Vector2 p) =>
        ShaderMath.Fract(ShaderMath.SinF(ShaderMath.Dot(p, new Vector2(127.1f, 311.7f))) * 43758.5453f);

    public static Vector4 ProceduralSource(Vector2 doc, in LayerUniforms u, ReadOnlySpan<Vector4> lut)
    {
        if (u.SourceKind == 1 || u.SourceKind == 2)
        {
            Vector2 d = u.GradientEnd - u.GradientStart;
            float t;
            if (u.SourceKind == 1)
                t = Vector2.Dot(doc - u.GradientStart, d) / MathF.Max(Vector2.Dot(d, d), 1e-6f);
            else
                t = (doc - u.GradientStart).Length() / MathF.Max(d.Length(), 1e-6f);
            if (u.GradientReverse != 0) t = 1f - t;
            Vector4 c = SampleLut(lut, t);  // straight sRGB + opacity
            return new Vector4(DecodeSRGB(c.Xyz()) * c.W, c.W);
        }
        // Pattern
        float s = MathF.Max(u.PatternScale, 1f);
        Vector2 cell = ShaderMath.Floor(doc / s);
        Vector2 f = ShaderMath.Fract(doc / s);
        float k;
        switch (u.PatternKind)
        {
            case 0: k = ShaderMath.Fmod(MathF.Abs(cell.X + cell.Y), 2f); break;            // checkerboard
            case 1: k = (f - new Vector2(0.5f)).Length() < 0.3f ? 1f : 0f; break;           // dots
            case 2: k = ShaderMath.Fract((doc.X + doc.Y) / s) < 0.5f ? 1f : 0f; break;     // stripes
            case 3:
            {
                float w = MathF.Max(1f, s / 16f) / s;                                         // grid
                k = f.X < w || f.Y < w ? 1f : 0f;
                break;
            }
            default: k = Hash21(cell); break;                                                // noise
        }
        Vector3 c3 = ShaderMath.Mix(u.PatternBackground.Xyz(), u.PatternForeground.Xyz(), k);
        return new Vector4(c3, 1f);
    }

    /// <summary>The colour overlay step of <c>composite_layer_fragment</c>: replaces the layer's colour, keeping its alpha.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 ApplyOverlay(Vector4 c, Vector4 overlay, int overlayMode)
    {
        if (overlay.W > 0f && c.W > 0f)
        {
            Vector3 straight = c.Xyz() / c.W;
            Vector3 over = BlendColor(ShaderMath.Saturate(straight), overlay.Xyz(), overlayMode);
            return new Vector4(ShaderMath.Mix(straight, over, overlay.W) * c.W, c.W);
        }
        return c;
    }

    /// <summary>
    /// <c>composite_layer_fragment</c>: one layer (pixels, fill, group result or effect) onto the current tile.
    /// <paramref name="c"/> is the sampled (or procedural) source; <paramref name="srcMask"/> the layer mask sampled
    /// with the source's uv; <paramref name="clip"/>/<paramref name="clipMask"/> the clipping base's alpha and mask at
    /// the output pixel.
    /// </summary>
    public static Vector4 CompositeLayerFragment(Vector4 dst, Vector4 c, in LayerUniforms u, float srcMask, float clip,
                                                 float clipMask)
    {
        c = ApplyOverlay(c, u.Overlay, u.OverlayMode);
        float k = u.Opacity * MaskValue(srcMask, u.MaskDensity);
        k *= clip * MaskValue(clipMask, u.ClipDensity);
        return BlendOver(dst, c * k, u.Mode, u.GammaSpace != 0);
    }

    /// <summary>
    /// <c>passthrough_mix_fragment</c>: pass-through group with opacity or mask: blend the children's result back
    /// toward the backdrop saved before they were drawn.
    /// </summary>
    public static Vector4 PassthroughMixFragment(Vector4 dst, Vector4 backup, in LayerUniforms u, float mask, float clip,
                                                 float clipMask)
    {
        float k = u.Opacity * MaskValue(mask, u.MaskDensity);
        k *= clip * MaskValue(clipMask, u.ClipDensity);
        return Vector4.Lerp(backup, dst, k);
    }

    // ── Adjustment layers (formulas match Glimmer for the web) ─────────────────

    public static Vector3 Rgb2Hsl(Vector3 c)
    {
        float mx = MathF.Max(MathF.Max(c.X, c.Y), c.Z);
        float mn = MathF.Min(MathF.Min(c.X, c.Y), c.Z);
        float l = (mx + mn) * 0.5f;
        float h = 0f, s = 0f;
        float d = mx - mn;
        if (d > 1e-5f)
        {
            s = l > 0.5f ? d / (2f - mx - mn) : d / (mx + mn);
            if (mx == c.X) h = (c.Y - c.Z) / d + (c.Y < c.Z ? 6f : 0f);
            else if (mx == c.Y) h = (c.Z - c.X) / d + 2f;
            else h = (c.X - c.Y) / d + 4f;
            h /= 6f;
        }
        return new Vector3(h, s, l);
    }

    public static float Hue2Rgb(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 0.5f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }

    public static Vector3 Hsl2Rgb(Vector3 hsl)
    {
        if (hsl.Y <= 0f) return new Vector3(hsl.Z);
        float q = hsl.Z < 0.5f ? hsl.Z * (1f + hsl.Y) : hsl.Z + hsl.Y - hsl.Z * hsl.Y;
        float p = 2f * hsl.Z - q;
        return new Vector3(Hue2Rgb(p, q, hsl.X + 1f / 3f), Hue2Rgb(p, q, hsl.X), Hue2Rgb(p, q, hsl.X - 1f / 3f));
    }

    /// <summary>Straight linear colour in, adjusted straight linear colour out.</summary>
    public static Vector3 AdjustColor(Vector3 lin, in AdjustUniforms u, ReadOnlySpan<Vector4> lut)
    {
        if (u.Kind == 3)
        {  // exposure works in linear light
            Vector3 e = lin * ShaderMath.Exp2(u.P(0)) + new Vector3(u.P(1));
            e = Vector3.Max(e, Vector3.Zero);
            float p = 1f / MathF.Max(u.P(2), 0.01f);
            return new Vector3(MathF.Pow(e.X, p), MathF.Pow(e.Y, p), MathF.Pow(e.Z, p));
        }
        Vector3 c = ShaderMath.Saturate(EncodeSRGB(lin));
        switch (u.Kind)
        {
            case 0:
            {  // brightness / contrast
                c += new Vector3(u.P(0));
                float k = MathF.Tan((Math.Clamp(u.P(1), -0.999f, 0.999f) + 1f) * 0.7853981634f);
                c = (c - new Vector3(0.5f)) * k + new Vector3(0.5f);
                break;
            }
            case 1:    // levels
            case 2:    // curves
                c = new Vector3(SampleLut(lut, c.X).X, SampleLut(lut, c.Y).Y, SampleLut(lut, c.Z).Z);
                break;
            case 4:
            {  // hue / saturation
                Vector3 hsl = Rgb2Hsl(c);
                float l = u.P(2);
                hsl.X = ShaderMath.Fract(hsl.X + u.P(0) / 6.2831853f);
                hsl.Y = ShaderMath.Saturate(hsl.Y * (1f + u.P(1)));
                hsl.Z = ShaderMath.Saturate(hsl.Z + l * (l > 0f ? (1f - hsl.Z) : hsl.Z));
                c = Hsl2Rgb(hsl);
                break;
            }
            case 5:
            {  // vibrance
                float s = MathF.Max(MathF.Max(c.X, c.Y), c.Z) - MathF.Min(MathF.Min(c.X, c.Y), c.Z);
                float k = 1f + u.P(0) * (1f - s) + u.P(1);
                c = ShaderMath.Mix(new Vector3(Luma709(c)), c, k);
                break;
            }
            case 6:
            {  // colour balance
                float before = Luma709(c);
                Vector3 shadowW = Vector3.One - ShaderMath.Smoothstep(0f, 0.5f, c);
                Vector3 highW = ShaderMath.Smoothstep(0.5f, 1f, c);
                Vector3 midW = Vector3.One - shadowW - highW;
                c += new Vector3(u.P(0), u.P(1), u.P(2)) * 0.5f * shadowW;
                c += new Vector3(u.P(3), u.P(4), u.P(5)) * 0.5f * midW;
                c += new Vector3(u.P(6), u.P(7), u.P(8)) * 0.5f * highW;
                c = ShaderMath.Saturate(c);
                if (u.P(9) > 0.5f) c = ShaderMath.Saturate(c + new Vector3(before - Luma709(c)));
                break;
            }
            case 7:
            {  // black & white
                float mn = MathF.Min(MathF.Min(c.X, c.Y), c.Z);
                float red = MathF.Max(0f, MathF.Min(c.X - c.Y, c.X - c.Z));
                float green = MathF.Max(0f, MathF.Min(c.Y - c.X, c.Y - c.Z));
                float blue = MathF.Max(0f, MathF.Min(c.Z - c.X, c.Z - c.Y));
                float yellow = MathF.Max(0f, MathF.Min(c.X, c.Y) - c.Z);
                float cyan = MathF.Max(0f, MathF.Min(c.Y, c.Z) - c.X);
                float magenta = MathF.Max(0f, MathF.Min(c.X, c.Z) - c.Y);
                float gray = mn + red * u.P(0) + yellow * u.P(1) + green * u.P(2)
                           + cyan * u.P(3) + blue * u.P(4) + magenta * u.P(5);
                c = new Vector3(ShaderMath.Saturate(gray));
                break;
            }
            case 8:
            {  // photo filter
                float before = Luma709(c);
                c = ShaderMath.Mix(c, c * new Vector3(u.P(0), u.P(1), u.P(2)), u.P(3));
                if (u.P(4) > 0.5f)
                {
                    float after = Luma709(c);
                    c = ShaderMath.Saturate(c * (after > 1e-4f ? before / after : 1f));
                }
                break;
            }
            case 9:
            {  // channel mixer: rows r, g, b = (r, g, b, constant)
                var o = new Vector3(Vector3.Dot(c, new Vector3(u.P(0), u.P(1), u.P(2))) + u.P(3),
                                    Vector3.Dot(c, new Vector3(u.P(4), u.P(5), u.P(6))) + u.P(7),
                                    Vector3.Dot(c, new Vector3(u.P(8), u.P(9), u.P(10))) + u.P(11));
                if (u.P(12) > 0.5f) o = new Vector3(o.X);
                c = ShaderMath.Saturate(o);
                break;
            }
            case 10: c = Vector3.One - c; break;  // invert
            case 11:
            {  // posterize
                float n = MathF.Max(2f, u.P(0));
                c = ShaderMath.Saturate(ShaderMath.Floor(c * n) / (n - 1f));
                break;
            }
            case 12:
            {  // threshold
                float l = Vector3.Dot(c, new Vector3(0.299f, 0.587f, 0.114f));
                c = new Vector3(l >= u.P(0) ? 1f : 0f);
                break;
            }
            case 13:
                // gradient map
                c = SampleLut(lut, Luma709(c)).Xyz();
                break;
            default: break;
        }
        return DecodeSRGB(ShaderMath.Saturate(c));
    }

    /// <summary><c>adjust_fragment</c> with <paramref name="k"/> = opacity × mask × clip already multiplied.</summary>
    public static Vector4 AdjustFragment(Vector4 dst, in AdjustUniforms u, ReadOnlySpan<Vector4> lut, float k)
    {
        float a = dst.W;
        if (a <= 0f) return dst;
        Vector3 cb = dst.Xyz() / a;
        Vector3 adjusted = AdjustColor(cb, u, lut);

        // The adjustment's blend mode combines the backdrop with its adjusted self.
        bool gamma = u.GammaSpace != 0;
        Vector3 b = gamma ? EncodeSRGB(cb) : cb;
        Vector3 s = gamma ? EncodeSRGB(adjusted) : adjusted;
        Vector3 blended = BlendColor(b, s, u.Mode);

        // Opacity and masks mix in the document's blend space too: a mask at 90 % over a
        // gamma-blended document (one from Photoshop) is 90 % of the way in encoded values.
        Vector3 mixed = gamma ? DecodeSRGB(ShaderMath.Mix(b, blended, k)) : ShaderMath.Mix(cb, blended, k);
        return new Vector4(mixed * a, a);
    }

    /// <summary>The <c>k</c> of <c>adjust_fragment</c>, <c>passthrough_mix_fragment</c> and <c>composite_layer_fragment</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Coverage(float opacity, float mask, float maskDensity, float clip, float clipMask, float clipDensity) =>
        opacity * MaskValue(mask, maskDensity) * (clip * MaskValue(clipMask, clipDensity));

    // ── Layer style kernels ────────────────────────────────────────────────────

    /// <summary>
    /// <c>effect_alpha</c>: effective alpha (layer alpha × mask), shifted by the rounded offset, spread and optionally
    /// inverted. <paramref name="params"/> = offset.xy, spread, invert.
    /// </summary>
    public static void EffectAlpha(ReadOnlySpan<float> alpha, ReadOnlySpan<float> mask, int width, int height, Vector4 @params,
                                   Span<float> output)
    {
        int ox = (int)MathF.Round(@params.X, MidpointRounding.AwayFromZero);
        int oy = (int)MathF.Round(@params.Y, MidpointRounding.AwayFromZero);
        bool invert = @params.W > 0.5f;
        float spread = Math.Clamp(@params.Z, 0f, 0.99f);
        float scale = 1f / (1f - spread);
        for (int y = 0; y < height; y++)
        {
            int py = y - oy;
            bool rowInside = py >= 0 && py < height;
            for (int x = 0; x < width; x++)
            {
                int px = x - ox;
                float a = 0f;
                if (rowInside && px >= 0 && px < width)
                {
                    int i = py * width + px;
                    a = alpha[i] * ShaderMath.Saturate(mask[i]);
                }
                if (invert) a = 1f - a;
                output[y * width + x] = ShaderMath.Saturate(a * scale);
            }
        }
    }

    /// <summary><c>effect_binary</c>: binary shape (alpha ≥ 0.5) or its complement, for the distance transform.</summary>
    public static void EffectBinary(ReadOnlySpan<float> alpha, ReadOnlySpan<float> mask, bool invert, Span<float> output)
    {
        for (int i = 0; i < output.Length; i++)
        {
            float a = alpha[i] * ShaderMath.Saturate(mask[i]);
            bool on = a >= 0.5f;
            if (invert) on = !on;
            output[i] = on ? 1f : 0f;
        }
    }

    /// <summary><c>effect_colorize</c>: turns a coverage (or distance) field into a premultiplied colour layer.</summary>
    public static Vector4 EffectColorize(float field, float field2, float pixelAlpha, float mask, in EffectColorUniforms u)
    {
        float cov;
        int mode = (int)u.Stroke.Z;
        if (mode == 1)
        {
            // field = distance outside the shape, field2 = distance inside.
            float w = u.Stroke.X;
            float dOut = field, dIn = field2;
            int pos = (int)u.Stroke.Y;
            float half = pos == 2 ? w * 0.5f : w;
            float outside = dOut > 0f ? ShaderMath.Saturate(half - dOut + 0.5f) : 0f;
            float inside = dIn > 0f ? ShaderMath.Saturate(half - dIn + 0.5f) : 0f;
            cov = pos == 0 ? outside : (pos == 1 ? inside : MathF.Max(outside, inside));
        }
        else if (mode == 2)
        {
            // Inner shadow: blurred inverse shape, kept inside the layer.
            float a = pixelAlpha * ShaderMath.Saturate(mask);
            cov = field * a;
        }
        else
        {
            cov = field;
        }
        float k = ShaderMath.Saturate(cov) * u.Color.W;
        return new Vector4(u.Color.Xyz() * k, k);
    }

    // ── Presentation ────────────────────────────────────────────────────────────

    /// <summary>
    /// The channel step of <c>present_fragment</c> (Channels panel): <paramref name="channel"/> is a bit mask of what to
    /// show (1 R, 2 G, 4 B, 8 alpha); 15 shows the tile as it is.
    /// </summary>
    public static Vector4 PresentChannels(Vector4 c, int channel)
    {
        if (channel == 15) return c;
        var m = new Vector3(channel & 1, (channel >> 1) & 1, (channel >> 2) & 1);
        bool alpha = (channel & 8) != 0;
        Vector3 rgb = c.W > 0.0001f ? c.Xyz() / c.W : Vector3.Zero;
        float count = m.X + m.Y + m.Z;
        // No colour channel: alpha as grey (or nothing). One: that channel as grey.
        if (count < 0.5f) return new Vector4(new Vector3(alpha ? c.W : 0f), 1f);
        if (count < 1.5f) return new Vector4(new Vector3(Vector3.Dot(rgb, m)), 1f);
        // Two or three: only those colours; without alpha the image is opaque.
        float a = alpha ? c.W : 1f;
        return new Vector4(rgb * m * a, a);
    }

    /// <summary>The dash of <c>ants_fragment</c> at drawable pixel centre <paramref name="p"/> (white when true, black otherwise).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AntsDashIsWhite(Vector2 p, float time)
    {
        float dash = ShaderMath.Fmod(MathF.Floor((p.X + p.Y) / 5f + time * 6f), 2f);
        return dash > 0.5f;
    }

    // ── Depth lens blur ─────────────────────────────────────────────────────────

    public static float CocAt(float d, in LensBlurUniforms u)
    {
        float distance = MathF.Max(MathF.Abs(d - u.Focus) - u.Size * 0.5f, 0f);
        return ShaderMath.Saturate(distance * 2.5f) * u.MaxRadius;
    }

    /// <summary>
    /// <c>lens_blur</c>: gather blur along a golden-angle spiral. Samples that are sharper than the sampling distance
    /// are down-weighted so in-focus edges don't bleed.
    /// </summary>
    public static Vector4 LensBlur(RegionImage color, FloatImage depth, int gx, int gy, in LensBlurUniforms u)
    {
        var p = new Vector2(gx + 0.5f, gy + 0.5f);
        float coc = CocAt(depth.SampleClampEdge(p), u);
        Vector4 centre = color.Read(gx, gy);
        if (coc < 0.5f) return centre;

        int n = Math.Clamp((int)(coc * coc * 0.8f), 24, 160);
        Vector4 sum = Vector4.Zero;
        float wsum = 0f;
        for (int i = 0; i < n; i++)
        {
            float r = MathF.Sqrt((i + 0.5f) / n) * coc;
            float a = i * 2.39996323f;
            Vector2 q = p + r * new Vector2(MathF.Cos(a), MathF.Sin(a));
            Vector4 c = color.SampleClampEdge(q);
            float sc = CocAt(depth.SampleClampEdge(q), u);
            float w = sc >= r * 0.8f ? 1f : 0.15f;
            float luma = Vector3.Dot(c.Xyz(), new Vector3(0.2126f, 0.7152f, 0.0722f));
            w *= 1f + u.Bokeh * 6f * MathF.Pow(ShaderMath.Saturate(luma), 4f);
            sum += c * w;
            wsum += w;
        }
        return sum / MathF.Max(wsum, 1e-5f);
    }

    // ── Brush dabs and pixel operations ─────────────────────────────────────────

    /// <summary>The quad half-size <c>dab_vertex</c> draws: the radius plus 1.5 px for the anti-aliased edge.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DabExtent(in DabInstance d) => d.Radius + 1.5f;

    /// <summary>
    /// <c>dab_fragment</c>: dab coverage at document pixel centre <paramref name="doc"/>; zero outside the dab's quad.
    /// The result is accumulated "over" the stroke's wet buffer (flow builds up).
    /// </summary>
    public static float DabFragment(in DabInstance d, Vector2 doc)
    {
        float r = DabExtent(d);
        Vector2 local = doc - d.Center;
        if (MathF.Abs(local.X) >= r || MathF.Abs(local.Y) >= r) return 0f;
        float c = MathF.Cos(-d.Angle), s = MathF.Sin(-d.Angle);
        var p = new Vector2(c * local.X - s * local.Y, s * local.X + c * local.Y);
        p.Y /= MathF.Max(d.Roundness, 0.05f);
        float dist = p.Length();
        float shape;
        if (d.Hardness >= 0.99f)
        {
            shape = ShaderMath.Saturate(d.Radius - dist + 0.5f);  // 1 px anti-aliased edge
        }
        else
        {
            float t = dist / MathF.Max(d.Radius, 0.5f);
            shape = 1f - ShaderMath.Smoothstep(d.Hardness, 1f, t);
        }
        if (d.Textured > 0.5f)
        {
            Vector2 cell = ShaderMath.Floor(doc / 2f);
            float n = ShaderMath.Fract(ShaderMath.SinF(ShaderMath.Dot(cell, new Vector2(12.9898f, 78.233f))) * 43758.5453f);
            shape *= ShaderMath.Smoothstep(0.25f, 0.75f, n);
        }
        return ShaderMath.Saturate(shape * d.Flow);
    }

    /// <summary><c>pixel_apply_fragment</c>: combines a tile with a coverage ("wet") tile, limited by the selection.</summary>
    public static Vector4 PixelApplyFragment(Vector4 o, float wet, float s, Vector4 effect, in ApplyUniforms u)
    {
        if (u.Mode == 3) return o * s;
        float k = ShaderMath.Saturate(wet) * u.Opacity * s;
        if (u.Mode == 1) return o * (1f - k);
        if (u.Mode == 2) { float v = ShaderMath.Mix(o.W, u.MaskTarget, k); return new Vector4(v); }
        if (u.Mode == 4) return Vector4.Lerp(o, effect, k);  // retouch: blend toward a precomputed result
        if (u.Mode == 5 || u.Mode == 6)
        {
            // Dodge / burn in display space. color.x = exposure, color.y = range (0 shadows, 1 midtones, 2 highlights).
            if (o.W <= 1e-6f) return o;
            Vector3 d = ShaderMath.Saturate(EncodeSRGB(o.Xyz() / o.W));
            float l = Vector3.Dot(d, new Vector3(0.299f, 0.587f, 0.114f));
            float w = u.Color.Y < 0.5f ? (1f - l) * (1f - l) : (u.Color.Y < 1.5f ? 1f - MathF.Abs(2f * l - 1f) : l * l);
            float e = u.Color.X * w;
            Vector3 r = u.Mode == 5 ? d + (Vector3.One - d) * e * 0.6f : d * (1f - e * 0.6f);
            return Vector4.Lerp(o, new Vector4(DecodeSRGB(r) * o.W, o.W), k);
        }
        return u.Color * k + o * (1f - k);
    }

    /// <summary>
    /// <c>selection_distance_remap</c>. Grow: pixels within <paramref name="radius"/> of the selection. Shrink: pixels
    /// farther than <paramref name="radius"/> from the unselected area. (<paramref name="d"/> comes from the distance
    /// transform.)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SelectionDistanceRemap(float d, float radius, bool grow) =>
        grow ? ShaderMath.Saturate(radius - d + 0.5f) : ShaderMath.Saturate(d - radius + 0.5f);
}
