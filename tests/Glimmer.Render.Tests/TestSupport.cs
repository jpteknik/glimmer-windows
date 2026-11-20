// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/CanvasRendererTests.swift (the helpers the render tests share:
// solidLayer, textureTile, renderPixel and the W3C reference)
using System.Numerics;
using Glimmer.Core;

namespace Glimmer.Render.Tests;

internal static class TestSupport
{
    public static Layer SolidLayer(Vector4 color, int size = 256, double opacity = 1, BlendMode mode = BlendMode.Normal) =>
        new("L", new RasterContent(TileGrid.Solid(color, new PixelRect(0, 0, size, size))), opacity: opacity, blendMode: mode);

    /// <summary>A pixel tile filled with one colour (forces the pixel path, not a solid tile).</summary>
    public static Tile TextureTile(Vector4 color)
    {
        var px = new Half[TileGeometry.FloatCount];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = (Half)color.X; px[i + 1] = (Half)color.Y; px[i + 2] = (Half)color.Z; px[i + 3] = (Half)color.W;
        }
        px[0] = (Half)((float)(Half)color.X + 0.001f);  // break uniformity at one corner pixel
        return Tile.FromHalf(px);
    }

    public static TileGrid Grid(params (TileCoord Coord, Tile Tile)[] tiles)
    {
        var b = new TileGrid.Builder();
        foreach (var (c, t) in tiles) b[c] = t;
        return b.ToImmutable();
    }

    public static Vector4 RenderPixel(DocumentState state, int x, int y, LayerOverrides? overrides = null)
    {
        var (buffer, bytesPerRow, rect) = new DocumentCompositor().RenderPixels(state, overrides, new PixelRect(x, y, 1, 1));
        Assert.True(rect.Width == 1 && bytesPerRow == 8);
        return new Vector4((float)buffer[0], (float)buffer[1], (float)buffer[2], (float)buffer[3]);
    }

    public static float SrgbEncode(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;

    public static float SrgbDecode(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static bool Close(Vector4 a, Vector4 b, float tolerance = 0.004f) => (a - b).Length() < tolerance;

    public static Vector3 Xyz(this Vector4 v) => new(v.X, v.Y, v.Z);
}

/// <summary>CPU reference (W3C Compositing and Blending Level 1), independent of the shader.</summary>
internal static class Reference
{
    public static float Lum(Vector3 c) => 0.3f * c.X + 0.59f * c.Y + 0.11f * c.Z;

    private static float Min(Vector3 c) => MathF.Min(c.X, MathF.Min(c.Y, c.Z));
    private static float Max(Vector3 c) => MathF.Max(c.X, MathF.Max(c.Y, c.Z));

    public static Vector3 Clip(Vector3 c)
    {
        float l = Lum(c), n = Min(c), x = Max(c);
        if (n < 0) c = new Vector3(l) + (c - new Vector3(l)) * l / (l - n);
        if (x > 1) c = new Vector3(l) + (c - new Vector3(l)) * (1 - l) / (x - l);
        return c;
    }

    public static Vector3 SetLum(Vector3 c, float l) => Clip(c + new Vector3(l - Lum(c)));
    public static float Sat(Vector3 c) => Max(c) - Min(c);

    public static Vector3 SetSat(Vector3 c, float s)
    {
        float r = Max(c) - Min(c);
        return r <= 1e-6f ? Vector3.Zero : (c - new Vector3(Min(c))) * s / r;
    }

    private static Vector3 Each(Vector3 b, Vector3 s, Func<float, float, float> f) => new(f(b.X, s.X), f(b.Y, s.Y), f(b.Z, s.Z));

    public static float HardLight(float b, float s) => s <= 0.5f ? b * 2 * s : b + (2 * s - 1) - b * (2 * s - 1);

    public static Vector3 Blend(Vector3 b, Vector3 s, BlendMode mode)
    {
        static float Burn(float b, float s) => b >= 1 ? 1 : (s <= 0 ? 0 : 1 - MathF.Min(1, (1 - b) / s));
        static float Dodge(float b, float s) => b == 0 ? 0 : (s >= 1 ? 1 : MathF.Min(1, b / (1 - s)));
        return mode switch
        {
            BlendMode.Normal or BlendMode.PassThrough => s,
            BlendMode.Multiply => b * s,
            BlendMode.Screen => b + s - b * s,
            BlendMode.Overlay => Each(b, s, (b, s) => HardLight(s, b)),
            BlendMode.Darken => Vector3.Min(b, s),
            BlendMode.Lighten => Vector3.Max(b, s),
            BlendMode.ColorDodge => Each(b, s, Dodge),
            BlendMode.ColorBurn => Each(b, s, Burn),
            BlendMode.HardLight => Each(b, s, HardLight),
            BlendMode.SoftLight => Each(b, s, (b, s) =>
            {
                float d = b <= 0.25f ? ((16 * b - 12) * b + 4) * b : MathF.Sqrt(b);
                return s <= 0.5f ? b - (1 - 2 * s) * b * (1 - b) : b + (2 * s - 1) * (d - b);
            }),
            BlendMode.Difference => Vector3.Abs(b - s),
            BlendMode.Exclusion => b + s - 2 * b * s,
            BlendMode.LinearDodge => Vector3.Min(b + s, Vector3.One),
            BlendMode.LinearBurn => Vector3.Max(b + s - Vector3.One, Vector3.Zero),
            BlendMode.Hue => SetLum(SetSat(s, Sat(b)), Lum(b)),
            BlendMode.Saturation => SetLum(SetSat(b, Sat(s)), Lum(b)),
            BlendMode.Color => SetLum(s, Lum(b)),
            BlendMode.Luminosity => SetLum(b, Lum(s)),
            BlendMode.VividLight => Each(b, s, (b, s) => s <= 0.5f ? Burn(b, 2 * s) : Dodge(b, 2 * (s - 0.5f))),
            BlendMode.LinearLight => Vector3.Clamp(b + 2 * s - Vector3.One, Vector3.Zero, Vector3.One),
            BlendMode.PinLight => Each(b, s, (b, s) => s <= 0.5f ? MathF.Min(b, 2 * s) : MathF.Max(b, 2 * s - 1)),
            BlendMode.HardMix => Each(b, s, (b, s) => b + s >= 1 ? 1 : 0),
            BlendMode.Subtract => Vector3.Max(b - s, Vector3.Zero),
            BlendMode.Divide => Each(b, s, (b, s) => s <= 0 ? (b > 0 ? 1 : 0) : MathF.Min(1, b / s)),
            BlendMode.DarkerColor => Lum(s) < Lum(b) ? s : b,
            BlendMode.LighterColor => Lum(s) > Lum(b) ? s : b,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    /// <summary>Straight-colour backdrop/source in, premultiplied result out (linear blend space).</summary>
    public static Vector4 Composite(Vector3 cb, float ab, Vector3 cs, float a, float opacity, BlendMode mode)
    {
        float asrc = a * opacity;
        Vector3 mixed = (1 - ab) * cs + ab * Blend(cb, cs, mode);
        float ao = asrc + ab * (1 - asrc);
        Vector3 premul = asrc * mixed + (1 - asrc) * ab * cb;
        return new Vector4(premul, ao);
    }
}
