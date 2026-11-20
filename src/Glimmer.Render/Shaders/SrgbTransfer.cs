// Ported from Packages/GlimmerKit/Sources/GlimmerRender/Shaders/Composite.metal (encodeSRGB / decodeSRGB)
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Glimmer.Render;

/// <summary>
/// The sRGB transfer function as the shaders use it: mirrored for negative (extended-range) values, so
/// encode(−x) = −encode(x).
/// </summary>
/// <remarks>
/// <see cref="Encode(float)"/>/<see cref="Decode(float)"/> are the exact formulas. <see cref="EncodeFast(float)"/>
/// and <see cref="DecodeFast(float)"/> give the same values for inputs in [0, 1] from a small table indexed by the
/// float's top bits (8 mantissa bits, linear interpolation): worst error 1.5e-6 (encode) and 1e-5 (decode) over
/// [0, 1], measured in SrgbTransferTests. Outside [0, 1] they fall back to the exact formula. Hot loops use the fast
/// pair; a GPU <c>pow</c> is itself an approximation of the same order.
/// </remarks>
public static class SrgbTransfer
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Encode(float c)
    {
        float a = MathF.Abs(c);
        float e = a <= 0.0031308f ? a * 12.92f : 1.055f * MathF.Pow(a, 1f / 2.4f) - 0.055f;
        return c < 0 ? -e : (c > 0 ? e : 0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Decode(float c)
    {
        float a = MathF.Abs(c);
        float d = a <= 0.04045f ? a / 12.92f : MathF.Pow((a + 0.055f) / 1.055f, 2.4f);
        return c < 0 ? -d : (c > 0 ? d : 0f);
    }

    public static Vector3 Encode(Vector3 c) => new(Encode(c.X), Encode(c.Y), Encode(c.Z));
    public static Vector3 Decode(Vector3 c) => new(Decode(c.X), Decode(c.Y), Decode(c.Z));

    // Index = float bits >> 15 (sign, exponent, 8 mantissa bits). The tables start at the first index above the
    // linear segment of each curve and end one past 1.0.
    private const int Shift = 15;
    private const float FractionScale = 1f / (1 << Shift);

    private static readonly int EncodeStart = (int)(BitConverter.SingleToUInt32Bits(0.0031308f) >> Shift);
    private static readonly int DecodeStart = (int)(BitConverter.SingleToUInt32Bits(0.04045f) >> Shift);
    private static readonly int OneIndex = (int)(BitConverter.SingleToUInt32Bits(1f) >> Shift);
    private static readonly float[] EncodeTable = BuildTable(EncodeStart, x => 1.055 * Math.Pow(x, 1 / 2.4) - 0.055);
    private static readonly float[] DecodeTable = BuildTable(DecodeStart, x => Math.Pow((x + 0.055) / 1.055, 2.4));

    private static float[] BuildTable(int start, Func<double, double> f)
    {
        int count = OneIndex - start + 2;
        var table = new float[count];
        for (int i = 0; i < count; i++)
        {
            double x = BitConverter.UInt32BitsToSingle((uint)(start + i) << Shift);
            table[i] = (float)f(x);
        }
        return table;
    }

    /// <summary>Encode for linear values; the exact formula outside (0.0031308, 1].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EncodeFast(float c)
    {
        uint bits = BitConverter.SingleToUInt32Bits(c);
        int index = (int)(bits >> Shift) - EncodeStart;
        // The unsigned compare also rejects negatives (sign bit set) and values above 1.
        if ((uint)index < (uint)(OneIndex - EncodeStart) && c > 0.0031308f)
        {
            float t = (bits & ((1u << Shift) - 1)) * FractionScale;
            float a = EncodeTable[index], b = EncodeTable[index + 1];
            return a + (b - a) * t;
        }
        if (c == 1f) return 1f;
        return Encode(c);
    }

    /// <summary>Decode for sRGB values; the exact formula outside (0.04045, 1].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DecodeFast(float c)
    {
        uint bits = BitConverter.SingleToUInt32Bits(c);
        int index = (int)(bits >> Shift) - DecodeStart;
        if ((uint)index < (uint)(OneIndex - DecodeStart) && c > 0.04045f)
        {
            float t = (bits & ((1u << Shift) - 1)) * FractionScale;
            float a = DecodeTable[index], b = DecodeTable[index + 1];
            return a + (b - a) * t;
        }
        if (c == 1f) return 1f;
        return Decode(c);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 EncodeFast(Vector3 c) => new(EncodeFast(c.X), EncodeFast(c.Y), EncodeFast(c.Z));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 DecodeFast(Vector3 c) => new(DecodeFast(c.X), DecodeFast(c.Y), DecodeFast(c.Z));
}
