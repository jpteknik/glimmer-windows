// Ported from Packages/GlimmerKit/Sources/GlimmerRender/Shaders/Filters.metal
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Glimmer.Render;

// Destructive pixel filters (Filter menu), ported from the web app's GLSL.
// Textures hold premultiplied linear extended-sRGB. Blurs work there (physically
// correct); the "look" filters convert to display sRGB straight alpha first,
// like Photoshop and the web app.
//
// The uniform structs live beside their Swift mirrors (FilterUniforms and SmudgeDab in FilterEngine.cs,
// LiquifyBrushUniforms in Liquify.cs).

internal static class FilterShaders
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 ToDisplay(Vector4 p)
    {
        Vector3 rgb = p.W > 1e-6f ? p.Xyz() / p.W : Vector3.Zero;
        return new Vector4(SrgbTransfer.EncodeFast(rgb), p.W);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 FromDisplay(Vector4 d) => new(SrgbTransfer.DecodeFast(d.Xyz()) * d.W, d.W);

    /// <summary>Rec. 601 luma (Filters.metal's <c>lum</c>, unlike the compositor's W3C <c>lum</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lum(Vector3 c) => Vector3.Dot(c, new Vector3(0.299f, 0.587f, 0.114f));

    /// <summary>
    /// <c>fract(sin(dot(p, (127.1, 311.7)) + seed) * 43758.5453123)</c>, evaluated as the Mac's GPU does (see
    /// <see cref="ShaderMath.Dot"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Hash2(Vector2 p, float seed) =>
        ShaderMath.Fract(ShaderMath.SinF(ShaderMath.Dot(p, new Vector2(127.1f, 311.7f)) + seed) * 43758.5453123f);

    public static float HalftoneScreen(Vector2 px, float v, float ang, float cell)
    {
        float s = MathF.Sin(ang), co = MathF.Cos(ang);
        var rp = new Vector2(co * px.X + s * px.Y, -s * px.X + co * px.Y);
        Vector2 c = ShaderMath.Fmod(ShaderMath.Fmod(rp, cell) + new Vector2(cell), cell) - new Vector2(cell * 0.5f);
        float radius = (1f - ShaderMath.Saturate(v)) * cell * 0.5f * 1.42f;
        return 1f - ShaderMath.Smoothstep(radius - 1f, radius + 1f, c.Length());
    }

    /// <summary>
    /// <c>filter_apply</c> at texel (<paramref name="gx"/>, <paramref name="gy"/>) of <paramref name="src"/>.
    /// kind: 1 motion, 2 lens, 3 sharpen, 4 unsharp, 5 clarity, 6 noise, 7 pixelate,
    /// 8 find edges, 9 emboss, 10 oil paint, 11 halftone, 12 vignette, 13 chromatic aberration.
    /// </summary>
    public static Vector4 FilterApply(RegionImage src, RegionImage aux, int gx, int gy, in FilterUniforms u)
    {
        var p = new Vector2(gx + 0.5f, gy + 0.5f);
        Vector2 doc = p + u.TexOrigin;
        Vector4 c = src.Read(gx, gy);
        Vector4 result = c;

        switch (u.Kind)
        {
            case 1:
            {  // Motion blur (linear)
                float dist = MathF.Max(u.A.Y, 0f);
                float ang = u.A.X * MathF.PI / 180f;
                int n = Math.Clamp((int)MathF.Round(dist, MidpointRounding.AwayFromZero), 1, 255);
                Vector2 step = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * dist / MathF.Max(1f, n - 1);
                float mid = (n - 1) * 0.5f;
                Vector4 sum = Vector4.Zero;
                for (int i = 0; i < n; i++) sum += src.SampleClampEdge(p + step * (i - mid));
                result = sum / n;
                break;
            }
            case 2:
            {  // Lens blur: golden-angle disc, highlights weighted (linear)
                float radius = u.A.X;
                if (radius < 0.5f) break;
                int n = Math.Clamp((int)MathF.Round(radius * radius * 0.9f, MidpointRounding.AwayFromZero) + 12, 8, 256);
                Vector4 sum = Vector4.Zero;
                float wsum = 0f;
                for (int i = 0; i < n; i++)
                {
                    float t = (i + 0.5f) / n;
                    float ang = i * 2.39996323f;
                    Vector4 s = src.SampleClampEdge(p + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * MathF.Sqrt(t) * radius);
                    float l = s.W > 1e-6f ? MathF.Min(Vector3.Dot(s.Xyz() / s.W, new Vector3(0.2126f, 0.7152f, 0.0722f)), 4f) : 0f;
                    float w = 1f + u.A.Y * MathF.Pow(l, 3f) * 3f;
                    sum += s * w;
                    wsum += w;
                }
                result = sum / wsum;
                break;
            }
            case 3:
            {  // Sharpen: 4-neighbour Laplacian (display)
                Vector4 d = ToDisplay(c);
                Vector3 n = ToDisplay(src.Read(gx, gy - 1)).Xyz() + ToDisplay(src.Read(gx, gy + 1)).Xyz()
                          + ToDisplay(src.Read(gx + 1, gy)).Xyz() + ToDisplay(src.Read(gx - 1, gy)).Xyz();
                Vector3 rgb = ShaderMath.Saturate(d.Xyz() + (d.Xyz() * 4f - n) * u.A.X);
                result = FromDisplay(new Vector4(rgb, d.W));
                break;
            }
            case 4:
            {  // Unsharp mask: aux = Gaussian blur (display difference, threshold)
                Vector4 d = ToDisplay(c);
                Vector3 diff = d.Xyz() - ToDisplay(aux.Read(gx, gy)).Xyz();
                float mag = MathF.Max(MathF.Max(MathF.Abs(diff.X), MathF.Abs(diff.Y)), MathF.Abs(diff.Z));
                Vector3 rgb = d.Xyz();
                if (mag >= u.A.Z) rgb = ShaderMath.Saturate(rgb + diff * u.A.X);
                result = FromDisplay(new Vector4(rgb, d.W));
                break;
            }
            case 5:
            {  // Clarity: midtone-weighted large-radius high pass (aux = base blur)
                Vector4 d = ToDisplay(c);
                Vector3 high = d.Xyz() - ToDisplay(aux.Read(gx, gy)).Xyz();
                float l = Lum(d.Xyz());
                float midtone = 1f - MathF.Pow(ShaderMath.Saturate(MathF.Abs(l - 0.5f) * 2f), 2f);
                Vector3 rgb = ShaderMath.Saturate(d.Xyz() + high * u.A.X * 1.5f * midtone);
                result = FromDisplay(new Vector4(rgb, d.W));
                break;
            }
            case 6:
            {  // Add noise, seeded by document position (stable across tiles and previews)
                Vector4 d = ToDisplay(c);
                Vector2 q = ShaderMath.Floor(doc);
                Vector3 noise = u.A.Y > 0.5f
                    ? new Vector3(Hash2(q, 137.137f) - 0.5f)
                    : new Vector3(Hash2(q, 137.137f), Hash2(q + new Vector2(17f), 137.137f), Hash2(q + new Vector2(43f), 137.137f))
                      - new Vector3(0.5f);
                Vector3 rgb = ShaderMath.Saturate(d.Xyz() + noise * u.A.X);
                result = FromDisplay(new Vector4(rgb, d.W));
                break;
            }
            case 7:
            {  // Pixelate: cells aligned to the document origin, cell average via 4×4 samples
                float cell = MathF.Max(u.A.X, 1f);
                Vector2 cellOrigin = ShaderMath.Floor(doc / cell) * cell;
                Vector4 sum = Vector4.Zero;
                for (int j = 0; j < 4; j++)
                    for (int i = 0; i < 4; i++)
                        sum += src.SampleClampEdge(cellOrigin + (new Vector2(i, j) + new Vector2(0.5f)) * cell / 4f - u.TexOrigin);
                result = sum / 16f;
                break;
            }
            case 8:
            {  // Find edges: inverted Sobel magnitude (display)
                float tl = Lum(ToDisplay(src.Read(gx - 1, gy - 1)).Xyz());
                float tm = Lum(ToDisplay(src.Read(gx, gy - 1)).Xyz());
                float tr = Lum(ToDisplay(src.Read(gx + 1, gy - 1)).Xyz());
                float ml = Lum(ToDisplay(src.Read(gx - 1, gy)).Xyz());
                float mr = Lum(ToDisplay(src.Read(gx + 1, gy)).Xyz());
                float bl = Lum(ToDisplay(src.Read(gx - 1, gy + 1)).Xyz());
                float bm = Lum(ToDisplay(src.Read(gx, gy + 1)).Xyz());
                float br = Lum(ToDisplay(src.Read(gx + 1, gy + 1)).Xyz());
                float sx = -tl - 2f * ml - bl + tr + 2f * mr + br;
                float sy = -tl - 2f * tm - tr + bl + 2f * bm + br;
                float g = 1f - ShaderMath.Saturate(MathF.Sqrt(sx * sx + sy * sy));
                result = FromDisplay(new Vector4(g, g, g, c.W));
                break;
            }
            case 9:
            {  // Emboss (display)
                float ang = u.A.X * MathF.PI / 180f;
                var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
                float e = 0.5f + (Lum(ToDisplay(src.SampleClampEdge(p + dir)).Xyz()) - Lum(ToDisplay(src.SampleClampEdge(p - dir)).Xyz())) * u.A.Y;
                float v = ShaderMath.Saturate(e);
                result = FromDisplay(new Vector4(v, v, v, c.W));
                break;
            }
            case 10:
            {  // Oil paint: Kuwahara, lowest-variance quadrant mean (display)
                int rad = Math.Clamp((int)MathF.Round(u.A.X, MidpointRounding.AwayFromZero), 1, 10);
                Vector3 best = Vector3.Zero;
                float bestVar = 1e9f;
                for (int q = 0; q < 4; q++)
                {
                    int sx = (q & 1) != 0 ? 1 : -1, sy = q >= 2 ? 1 : -1;
                    Vector3 sumC = Vector3.Zero;
                    float sumL = 0f, sumL2 = 0f, cnt = 0f;
                    for (int dy = 0; dy <= rad; dy++)
                        for (int dx = 0; dx <= rad; dx++)
                        {
                            Vector3 s = ToDisplay(src.Read(gx + dx * sx, gy + dy * sy)).Xyz();
                            float l = Lum(s);
                            sumC += s; sumL += l; sumL2 += l * l; cnt += 1f;
                        }
                    float m = sumL / cnt;
                    float v = sumL2 / cnt - m * m;
                    if (v < bestVar) { bestVar = v; best = sumC / cnt; }
                }
                Vector4 d = ToDisplay(c);
                Vector3 rgb = ShaderMath.Mix(d.Xyz(), best, ShaderMath.Saturate(u.A.Y));
                result = FromDisplay(new Vector4(rgb, d.W));
                break;
            }
            case 11:
            {  // Halftone (display), screen anchored to the document
                Vector4 d = ToDisplay(c);
                float cell = MathF.Max(u.A.X, 2f);
                float ang = u.A.Y * MathF.PI / 180f;
                Vector3 rgb;
                if (u.A.Z > 0.5f)
                {
                    rgb = new Vector3(1f - HalftoneScreen(doc, Lum(d.Xyz()), ang, cell));
                }
                else
                {
                    float cy = HalftoneScreen(doc, d.X, ang + 0.2618f, cell);
                    float mg = HalftoneScreen(doc, d.Y, ang + 1.309f, cell);
                    float yl = HalftoneScreen(doc, d.Z, ang, cell);
                    rgb = Vector3.One - new Vector3(cy, mg, yl);
                }
                result = FromDisplay(new Vector4(rgb, d.W));
                break;
            }
            case 12:
            {  // Vignette over the document frame (display)
                Vector4 d = ToDisplay(c);
                Vector2 v = ((doc - u.FrameOrigin) / u.FrameSize - new Vector2(0.5f)) * 2f;
                v.X *= ShaderMath.Mix(u.FrameSize.X / u.FrameSize.Y, 1f, u.A.Z);
                float k = ShaderMath.Smoothstep(u.A.Y, u.A.Y + MathF.Max(u.A.W, 0.01f), v.Length());
                Vector3 rgb = ShaderMath.Saturate(d.Xyz() * (1f - k * u.A.X));
                result = FromDisplay(new Vector4(rgb, d.W));
                break;
            }
            case 13:
            {  // Chromatic aberration: R out, B in, radially from the frame centre
                Vector2 dir = (doc - u.FrameOrigin) / u.FrameSize - new Vector2(0.5f);
                float r = dir.Length() / 0.7071f;
                float falloff = u.A.Y > 0.5f ? r * r : r;
                Vector2 off = Vector2.Normalize(dir + new Vector2(1e-6f)) * u.A.X * falloff;
                Vector4 d = ToDisplay(c);
                d.X = ToDisplay(src.SampleClampEdge(p + off)).X;
                d.Z = ToDisplay(src.SampleClampEdge(p - off)).Z;
                result = FromDisplay(d);
                break;
            }
            default: break;
        }
        return result;
    }

    /// <summary><c>filter_mix</c>: the original, or the filtered pixel blended by the selection inside the target area.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 FilterMix(Vector4 original, Vector4 filtered, float selection, bool hasSelection)
    {
        float s = hasSelection ? ShaderMath.Saturate(selection) : 1f;
        return Vector4.Lerp(original, filtered, s);
    }

    /// <summary>
    /// <c>heal_combine</c>. Healing: the cloned texture with the destination's low frequencies:
    /// out = clone + blur(destination) − blur(clone).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 HealCombine(Vector4 clone, Vector4 destBlur, Vector4 cloneBlur)
    {
        Vector4 r = clone + destBlur - cloneBlur;
        return new Vector4(MathF.Max(r.X, 0f), MathF.Max(r.Y, 0f), MathF.Max(r.Z, 0f), ShaderMath.Saturate(r.W));
    }

    /// <summary>
    /// <c>smudge_dab</c>. Smudge: pulls colour along the stroke direction inside one dab. Returns false where the
    /// kernel writes nothing (outside the dab). <c>d.Centre</c> is in working-texture texels.
    /// </summary>
    public static bool SmudgeDabFragment(RegionImage before, FloatImage? selection, in SmudgeDab d, int gx, int gy,
                                         out Vector4 value)
    {
        var p = new Vector2(gx + 0.5f, gy + 0.5f);
        float dist = (p - d.Centre).Length() / MathF.Max(d.Radius, 0.5f);
        if (dist >= 1f) { value = default; return false; }
        float edge = ShaderMath.Mix(0f, 0.98f, ShaderMath.Saturate(d.Hardness));
        float k = 1f - ShaderMath.Smoothstep(edge, 1f, dist);
        if (selection is not null) k *= ShaderMath.Saturate(selection.SampleClampEdge(p));
        k *= d.Strength;
        Vector4 here = before.SampleClampEdge(p);
        Vector4 dragged = before.SampleClampEdge(p - d.Delta);
        value = Vector4.Lerp(here, dragged, k);
        return true;
    }

    // MARK: Liquify

    // The field stores, per output pixel, where to read the source from (relative
    // offset in document px): output(p) = source(p + D(p)). A brush moving content
    // by o(p) composes as D'(p) = D(p − o) − o, so strokes chain without the
    // drift you get from simply adding offsets.
    public static Vector2 LiquifyBrush(VectorField field, int gx, int gy, in LiquifyBrushUniforms b)
    {
        var t = new Vector2(gx + 0.5f, gy + 0.5f);
        Vector2 p = b.FieldOrigin + t * b.FieldScale;
        Vector2 v = p - b.Centre;
        float d = v.Length() / MathF.Max(b.Radius, 1f);
        Vector2 current = field.Read(gx, gy);
        if (d >= 1f) return current;
        float w = 1f - d * d;
        w = w * w * b.Strength;
        Vector2 o = Vector2.Zero;
        switch (b.Mode)
        {
            case 0: o = b.Delta * w; break;
            case 1: return current * (1f - w * 0.5f);
            case 2: o = -v * w * 0.08f; break;
            case 3: o = v * w * 0.08f; break;
            case 4:
            case 5:
            {
                float a = (b.Mode == 4 ? 1f : -1f) * w * 0.12f;
                var r = new Vector2(MathF.Cos(a) * v.X - MathF.Sin(a) * v.Y, MathF.Sin(a) * v.X + MathF.Cos(a) * v.Y);
                o = r - v;
                break;
            }
            default: break;
        }
        Vector2 back = field.SampleClampEdge(t - o / b.FieldScale);
        return back - o;
    }

    /// <summary><c>liquify_render</c>: out(p) = source(p + D(p)); the origins map output texels to document px.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 LiquifyRender(RegionImage source, VectorField field, int gx, int gy, Vector2 sourceOrigin,
                                        Vector2 fieldOrigin, float fieldScale)
    {
        Vector2 doc = sourceOrigin + new Vector2(gx + 0.5f, gy + 0.5f);
        Vector2 d = field.SampleClampEdge((doc - fieldOrigin) / fieldScale);
        return source.SampleClampZero(doc + d - sourceOrigin);
    }
}
