// Ported from Packages/GlimmerKit/Sources/GlimmerRender/Shaders/Composite.metal (blendOver and blendColor, the blending
// step of composite_layer_fragment, applied to a row)
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Glimmer.Render;

/// <summary>
/// <see cref="CompositeShaders.BlendOver"/> over a row of pixels, with one loop per blend mode and blend space so the
/// mode's formula is compiled into the loop instead of being chosen per pixel. The results equal
/// <see cref="CompositeShaders.BlendOver"/>'s to float rounding (the divisions become reciprocals).
/// </summary>
internal static class BlendRows
{
    /// <summary>dst = blendOver(dst, src × k, mode) for each pixel.</summary>
    public static void Blend(Span<Vector4> dst, ReadOnlySpan<Vector4> src, ReadOnlySpan<float> k, int mode, bool gamma)
    {
        if (gamma) Dispatch<Gamma>(dst, src, k, mode);
        else Dispatch<Linear>(dst, src, k, mode);
    }

    private static void Dispatch<TSpace>(Span<Vector4> dst, ReadOnlySpan<Vector4> src, ReadOnlySpan<float> k, int mode)
        where TSpace : struct, ISpace
    {
        switch (mode)
        {
            case 0:
                if (typeof(TSpace) == typeof(Linear)) Normal(dst, src, k);
                else Run<Replace, TSpace>(dst, src, k);
                return;
            case 1: Run<Multiply, TSpace>(dst, src, k); return;
            case 2: Run<Screen, TSpace>(dst, src, k); return;
            case 3: Run<Overlay, TSpace>(dst, src, k); return;
            case 4: Run<Darken, TSpace>(dst, src, k); return;
            case 5: Run<Lighten, TSpace>(dst, src, k); return;
            case 6: Run<ColorDodge, TSpace>(dst, src, k); return;
            case 7: Run<ColorBurn, TSpace>(dst, src, k); return;
            case 8: Run<HardLight, TSpace>(dst, src, k); return;
            case 9: Run<SoftLight, TSpace>(dst, src, k); return;
            case 10: Run<Difference, TSpace>(dst, src, k); return;
            case 11: Run<Exclusion, TSpace>(dst, src, k); return;
            case 12: Run<LinearDodge, TSpace>(dst, src, k); return;
            case 13: Run<LinearBurn, TSpace>(dst, src, k); return;
            case 14: Run<Hue, TSpace>(dst, src, k); return;
            case 15: Run<Saturation, TSpace>(dst, src, k); return;
            case 16: Run<Color, TSpace>(dst, src, k); return;
            case 17: Run<Luminosity, TSpace>(dst, src, k); return;
            case 18: Run<VividLight, TSpace>(dst, src, k); return;
            case 19: Run<LinearLight, TSpace>(dst, src, k); return;
            case 20: Run<PinLight, TSpace>(dst, src, k); return;
            case 21: Run<HardMix, TSpace>(dst, src, k); return;
            case 22: Run<Subtract, TSpace>(dst, src, k); return;
            case 23: Run<Divide, TSpace>(dst, src, k); return;
            case 24: Run<DarkerColor, TSpace>(dst, src, k); return;
            case 25: Run<LighterColor, TSpace>(dst, src, k); return;
            default: Run<Replace, TSpace>(dst, src, k); return;
        }
    }

    /// <summary>Normal in linear light is plain premultiplied "over".</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Normal(Span<Vector4> dst, ReadOnlySpan<Vector4> src, ReadOnlySpan<float> k)
    {
        for (int x = 0; x < dst.Length; x++)
        {
            Vector4 sc = src[x] * k[x];
            float a = sc.W;
            if (!(a > 0f)) continue;
            Vector4 d = dst[x];
            dst[x] = d.W > 0f ? sc + d * (1f - a) : new Vector4(new Vector3(sc.X, sc.Y, sc.Z) * (a / MathF.Max(a, 1e-8f)), a);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Run<TMode, TSpace>(Span<Vector4> dst, ReadOnlySpan<Vector4> src, ReadOnlySpan<float> k)
        where TMode : struct, IMode where TSpace : struct, ISpace
    {
        for (int x = 0; x < dst.Length; x++)
        {
            Vector4 sc = src[x] * k[x];
            float @as = sc.W;
            if (@as <= 0f) continue;
            Vector4 d = dst[x];
            float ab = d.W;
            Vector3 cs = new Vector3(sc.X, sc.Y, sc.Z) * (1f / MathF.Max(@as, 1e-8f));
            Vector3 cb = ab > 0f ? new Vector3(d.X, d.Y, d.Z) * (1f / ab) : Vector3.Zero;
            cs = TSpace.Encode(cs);
            cb = TSpace.Encode(cb);
            Vector3 blended = TMode.Saturates ? TMode.Blend(ShaderMath.Saturate(cb), ShaderMath.Saturate(cs)) : TMode.Blend(cb, cs);
            Vector3 mixed = (1f - ab) * cs + ab * blended;
            float ao = @as + ab * (1f - @as);
            Vector3 premultiplied = @as * mixed + (1f - @as) * ab * cb;
            if (typeof(TSpace) == typeof(Linear) && ao >= 1e-8f)
            {
                // straight × ao is the premultiplied value itself.
                dst[x] = new Vector4(premultiplied, ao);
                continue;
            }
            Vector3 straight = TSpace.Decode(premultiplied * (1f / MathF.Max(ao, 1e-8f)));
            dst[x] = new Vector4(straight * ao, ao);
        }
    }

    // MARK: Blend spaces

    internal interface ISpace
    {
        static abstract Vector3 Encode(Vector3 c);
        static abstract Vector3 Decode(Vector3 c);
    }

    private struct Linear : ISpace
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Encode(Vector3 c) => c;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Decode(Vector3 c) => c;
    }

    private struct Gamma : ISpace
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Encode(Vector3 c) => CompositeShaders.EncodeSRGB(c);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Decode(Vector3 c) => CompositeShaders.DecodeSRGB(c);
    }

    // MARK: Modes (blendColor's cases; every mode but normal works on saturated colours)

    internal interface IMode
    {
        static abstract bool Saturates { get; }
        static abstract Vector3 Blend(Vector3 cb, Vector3 cs);
    }

    private struct Replace : IMode
    {
        public static bool Saturates => false;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Blend(Vector3 cb, Vector3 cs) => cs;
    }

    private struct Multiply : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Blend(Vector3 cb, Vector3 cs) => cb * cs;
    }

    private struct Screen : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Blend(Vector3 cb, Vector3 cs) => cb + cs - cb * cs;
    }

    private struct Overlay : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) =>
            new(CompositeShaders.HardLight(cs.X, cb.X), CompositeShaders.HardLight(cs.Y, cb.Y), CompositeShaders.HardLight(cs.Z, cb.Z));
    }

    private struct Darken : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Blend(Vector3 cb, Vector3 cs) => Vector3.Min(cb, cs);
    }

    private struct Lighten : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Blend(Vector3 cb, Vector3 cs) => Vector3.Max(cb, cs);
    }

    private struct ColorDodge : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) =>
            new(CompositeShaders.ColorDodge(cb.X, cs.X), CompositeShaders.ColorDodge(cb.Y, cs.Y), CompositeShaders.ColorDodge(cb.Z, cs.Z));
    }

    private struct ColorBurn : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) =>
            new(CompositeShaders.ColorBurn(cb.X, cs.X), CompositeShaders.ColorBurn(cb.Y, cs.Y), CompositeShaders.ColorBurn(cb.Z, cs.Z));
    }

    private struct HardLight : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) =>
            new(CompositeShaders.HardLight(cb.X, cs.X), CompositeShaders.HardLight(cb.Y, cs.Y), CompositeShaders.HardLight(cb.Z, cs.Z));
    }

    private struct SoftLight : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) =>
            new(CompositeShaders.SoftLight(cb.X, cs.X), CompositeShaders.SoftLight(cb.Y, cs.Y), CompositeShaders.SoftLight(cb.Z, cs.Z));
    }

    private struct Difference : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Blend(Vector3 cb, Vector3 cs) => Vector3.Abs(cb - cs);
    }

    private struct Exclusion : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Blend(Vector3 cb, Vector3 cs) => cb + cs - 2f * cb * cs;
    }

    private struct LinearDodge : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Blend(Vector3 cb, Vector3 cs) => Vector3.Min(Vector3.One, cb + cs);
    }

    private struct LinearBurn : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) => Vector3.Max(Vector3.Zero, cb + cs - Vector3.One);
    }

    private struct Hue : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) =>
            CompositeShaders.SetLum(CompositeShaders.SetSat(cs, CompositeShaders.Sat(cb)), CompositeShaders.Lum(cb));
    }

    private struct Saturation : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) =>
            CompositeShaders.SetLum(CompositeShaders.SetSat(cb, CompositeShaders.Sat(cs)), CompositeShaders.Lum(cb));
    }

    private struct Color : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) => CompositeShaders.SetLum(cs, CompositeShaders.Lum(cb));
    }

    private struct Luminosity : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) => CompositeShaders.SetLum(cb, CompositeShaders.Lum(cs));
    }

    private struct VividLight : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) =>
            new(CompositeShaders.VividLight(cb.X, cs.X), CompositeShaders.VividLight(cb.Y, cs.Y), CompositeShaders.VividLight(cb.Z, cs.Z));
    }

    private struct LinearLight : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) => ShaderMath.Saturate(cb + 2f * cs - Vector3.One);
    }

    private struct PinLight : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) =>
            new(CompositeShaders.PinLight(cb.X, cs.X), CompositeShaders.PinLight(cb.Y, cs.Y), CompositeShaders.PinLight(cb.Z, cs.Z));
    }

    private struct HardMix : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) =>
            new(ShaderMath.Step(1f, cb.X + cs.X), ShaderMath.Step(1f, cb.Y + cs.Y), ShaderMath.Step(1f, cb.Z + cs.Z));
    }

    private struct Subtract : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Blend(Vector3 cb, Vector3 cs) => Vector3.Max(Vector3.Zero, cb - cs);
    }

    private struct Divide : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) =>
            new(CompositeShaders.DivideBlend(cb.X, cs.X), CompositeShaders.DivideBlend(cb.Y, cs.Y), CompositeShaders.DivideBlend(cb.Z, cs.Z));
    }

    // Darker Color and Lighter Color: the whole colour of the darker (lighter) of the two, never a mix of their channels.
    private struct DarkerColor : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) => CompositeShaders.Lum(cs) < CompositeShaders.Lum(cb) ? cs : cb;
    }

    private struct LighterColor : IMode
    {
        public static bool Saturates => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Blend(Vector3 cb, Vector3 cs) => CompositeShaders.Lum(cs) > CompositeShaders.Lum(cb) ? cs : cb;
    }
}
