// Ported from Packages/GlimmerKit/Sources/GlimmerRender/CanvasRenderer.swift (the presentation pass; on the Mac the
// drawable is RGBA16Float and the display pipeline encodes it)
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Glimmer.Render;

/// <summary>
/// The last step of presentation on Windows: premultiplied linear colour to 8-bit premultiplied sRGB in BGRA order
/// (WPF's Pbgra32), with an 8×8 ordered dither so smooth gradients don't band (docs/PLAN.md §2.4: "checkerboard and
/// dither in the presentation pass"). The dither is fixed to drawable pixels, like the checkerboard, and adds at most
/// one quantization step.
/// </summary>
internal static class DisplayEncoding
{
    // Bayer 8×8 thresholds, (v + 0.5) / 64, and the same in 1/256 steps for the table path.
    private static readonly float[] Bayer = BuildBayer();
    private static readonly int[] Bayer256 = [.. Bayer.Select(t => (int)(t * 256))];

    private static float[] BuildBayer()
    {
        int[] b2 = [0, 2, 3, 1];
        var t = new float[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int v = 0;
                for (int bit = 0; bit < 3; bit++)
                {
                    int xx = (x >> bit) & 1, yy = (y >> bit) & 1;
                    v |= b2[yy * 2 + xx] << (2 * (2 - bit));
                }
                t[y * 8 + x] = (v + 0.5f) / 64f;
            }
        return t;
    }

    /// <summary>The dither threshold (0…1) at drawable pixel (x, y).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Threshold(int x, int y) => Bayer[((y & 7) << 3) | (x & 7)];

    /// <summary>Premultiplied linear → BGRA8 premultiplied sRGB, as a little-endian uint (B in the low byte).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint EncodeBgra(Vector4 c, int x, int y, bool dither = true)
    {
        float t = dither ? Threshold(x, y) : 0.5f;
        float a = c.W;
        if (a >= 0.99999f)
        {
            uint r = Quantize(SrgbTransfer.EncodeFast(c.X) * 255f + t);
            uint g = Quantize(SrgbTransfer.EncodeFast(c.Y) * 255f + t);
            uint b = Quantize(SrgbTransfer.EncodeFast(c.Z) * 255f + t);
            return b | (g << 8) | (r << 16) | 0xFF000000u;
        }
        if (!(a > 0f)) return 0;
        uint a8 = Quantize(a * 255f + 0.5f);
        float scale = a8;  // premultiplied in encoded space, as WPF composites
        float inv = 1f / a;
        uint r2 = Math.Min(a8, Quantize(SrgbTransfer.EncodeFast(ShaderMath.Saturate(c.X * inv)) * scale + t));
        uint g2 = Math.Min(a8, Quantize(SrgbTransfer.EncodeFast(ShaderMath.Saturate(c.Y * inv)) * scale + t));
        uint b2 = Math.Min(a8, Quantize(SrgbTransfer.EncodeFast(ShaderMath.Saturate(c.Z * inv)) * scale + t));
        return b2 | (g2 << 8) | (r2 << 16) | (a8 << 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Quantize(float v) => v <= 0f ? 0u : (v >= 255f ? 255u : (uint)v);

    // The encoded value of every Float16 (by its bits) in 1/256 of an 8-bit step: Half keeps 11 significant bits,
    // more than an 8-bit sRGB step needs anywhere on the curve.
    private static readonly ushort[] Encode16 = BuildEncode16();

    private static ushort[] BuildEncode16()
    {
        var t = new ushort[65536];
        for (int bits = 0; bits < 65536; bits++)
        {
            var h = BitConverter.UInt16BitsToHalf((ushort)bits);
            float v = (float)h;
            double e = float.IsNaN(v) || v <= 0 ? 0 : (v >= 1 ? 1 : (v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055));
            t[bits] = (ushort)Math.Min(65280, Math.Round(e * 255 * 256));
        }
        return t;
    }

    /// <summary>Every Float16 (by its bits) as a float: a table read is faster than a software conversion per value.</summary>
    internal static readonly float[] HalfToSingle = BuildHalfToSingle();

    private static float[] BuildHalfToSingle()
    {
        var t = new float[65536];
        for (int bits = 0; bits < t.Length; bits++) t[bits] = (float)BitConverter.UInt16BitsToHalf((ushort)bits);
        return t;
    }

    /// <summary>The RGBA pixel at value index <paramref name="i"/> of Float16 data (as bits), through <see cref="HalfToSingle"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 LoadHalf4(ReadOnlySpan<ushort> bits, int i)
    {
        var t = HalfToSingle;
        return new Vector4(t[bits[i]], t[bits[i + 1]], t[bits[i + 2]], t[bits[i + 3]]);
    }

    /// <summary>An opaque pixel given as Float16 bits (alpha exactly 1) → BGRA8, through the table.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint EncodeOpaqueHalf(ushort r, ushort g, ushort b, int x, int y)
    {
        var table = Encode16;
        int t = Bayer256[((y & 7) << 3) | (x & 7)];
        uint r8 = (uint)Math.Min(255, (table[r] + t) >> 8);
        uint g8 = (uint)Math.Min(255, (table[g] + t) >> 8);
        uint b8 = (uint)Math.Min(255, (table[b] + t) >> 8);
        return b8 | (g8 << 8) | (r8 << 16) | 0xFF000000u;
    }

    /// <summary>
    /// Encodes a row of premultiplied linear pixels into BGRA8. <paramref name="halfScratch"/> holds at least
    /// <c>4 × row.Length</c> values. Opaque pixels go through the Float16 table; the rest through
    /// <see cref="EncodeBgra(Vector4, int, int, bool)"/>.
    /// </summary>
    public static void EncodeRow(ReadOnlySpan<Vector4> row, Span<uint> output, int x0, int y, Span<Half> halfScratch)
    {
        var floats = MemoryMarshal.Cast<Vector4, float>(row);
        var halves = halfScratch[..floats.Length];
        TensorPrimitives.ConvertToHalf(floats, halves);
        var bits = MemoryMarshal.Cast<Half, ushort>(halves);
        var table = Encode16;
        int rowBase = (y & 7) << 3;
        for (int i = 0; i < row.Length; i++)
        {
            int o = i * 4;
            if (bits[o + 3] != 0x3C00)  // not exactly 1.0
            {
                output[i] = EncodeBgra(row[i], x0 + i, y);
                continue;
            }
            int t = Bayer256[rowBase | ((x0 + i) & 7)];
            uint r = (uint)Math.Min(255, (table[bits[o]] + t) >> 8);
            uint g = (uint)Math.Min(255, (table[bits[o + 1]] + t) >> 8);
            uint b = (uint)Math.Min(255, (table[bits[o + 2]] + t) >> 8);
            output[i] = b | (g << 8) | (r << 16) | 0xFF000000u;
        }
    }
}
