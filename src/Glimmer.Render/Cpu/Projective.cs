// Ported from Packages/GlimmerKit/Sources/GlimmerRender/LayerStyleRenderer.swift (SmartObjectRenderer: smart_vertex and
// smart_fragment in Composite.metal)
using System.Numerics;
using System.Runtime.CompilerServices;
using Glimmer.Core;

namespace Glimmer.Render;

/// <summary>
/// A 2-D projective map x' = (a·x + c·y + tx) / w, y' = (b·x + d·y + ty) / w, w = g·x + h·y + W, in double precision
/// (the Mac builds these matrices in Double and hands floats to the GPU).
/// </summary>
internal readonly record struct Projective(double A, double B, double C, double D, double Tx, double Ty, double G, double H,
                                           double W = 1)
{
    public static readonly Projective Identity = new(1, 0, 0, 1, 0, 0, 0, 0);

    public static Projective From(AffineTransform t) => new(t.A, t.B, t.C, t.D, t.Tx, t.Ty, t.G, t.H);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (double x, double y, double w) ApplyHomogeneous(double x, double y) =>
        (A * x + C * y + Tx, B * x + D * y + Ty, G * x + H * y + W);

    public Projective Inverse()
    {
        // Rows (a c tx), (b d ty), (g h W); the inverse is adj(M) / det(M).
        double m00 = A, m01 = C, m02 = Tx;
        double m10 = B, m11 = D, m12 = Ty;
        double m20 = G, m21 = H, m22 = W;
        double c00 = m11 * m22 - m12 * m21, c01 = m02 * m21 - m01 * m22, c02 = m01 * m12 - m02 * m11;
        double c10 = m12 * m20 - m10 * m22, c11 = m00 * m22 - m02 * m20, c12 = m02 * m10 - m00 * m12;
        double c20 = m10 * m21 - m11 * m20, c21 = m01 * m20 - m00 * m21, c22 = m00 * m11 - m01 * m10;
        double det = m00 * c00 + m01 * c10 + m02 * c20;
        if (Math.Abs(det) < 1e-300) return Identity;
        double k = 1 / det;
        return new Projective(c00 * k, c10 * k, c01 * k, c11 * k, c02 * k, c12 * k, c20 * k, c21 * k, c22 * k);
    }
}

/// <summary>
/// Rendering a smart object's source through its transform (<c>smart_vertex</c>/<c>smart_fragment</c>): pixels whose
/// centres fall inside the transformed source rectangle sample the source's mip chain trilinearly, transparent outside
/// it (<c>clamp_to_zero</c>), with uv interpolated perspective-correctly. On the CPU that is the inverse map of each
/// pixel centre.
/// </summary>
internal static class SmartSampling
{
    /// <summary>
    /// The source-space point for document pixel centre (px, py) and the derivatives of the source point per document
    /// pixel in x and y (what the GPU's uv derivatives measure).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector2 src, Vector2 dx, Vector2 dy, bool valid) Map(in Projective docToSource, double px, double py)
    {
        var (X, Y, W) = docToSource.ApplyHomogeneous(px, py);
        if (!(W > 1e-12)) return default;
        double iw = 1 / W;
        double sx = X * iw, sy = Y * iw;
        // d(X/W)/dx = (A·W − X·G) / W², etc.
        double dxx = (docToSource.A - sx * docToSource.G) * iw, dxy = (docToSource.B - sy * docToSource.G) * iw;
        double dyx = (docToSource.C - sx * docToSource.H) * iw, dyy = (docToSource.D - sy * docToSource.H) * iw;
        return (new Vector2((float)sx, (float)sy), new Vector2((float)dxx, (float)dxy), new Vector2((float)dyx, (float)dyy), true);
    }

    /// <summary>The smart object's colour at document pixel centre (px, py), premultiplied; zero outside.</summary>
    public static Vector4 Sample(MipChain source, in Projective docToSource, double px, double py)
    {
        var (src, dx, dy, valid) = Map(docToSource, px, py);
        if (!valid) return Vector4.Zero;
        float w = source.Width, h = source.Height;
        // Rasterization: the quad covers pixel centres inside the source rectangle.
        if (!(src.X >= 0f && src.X < w && src.Y >= 0f && src.Y < h)) return Vector4.Zero;
        var size = new Vector2(w, h);
        Vector2 uv = src / size;
        float lod = source.LevelOfDetail(dx / size, dy / size);
        return source.Sample(uv, lod, magNearest: false, zeroOutside: true);
    }
}
