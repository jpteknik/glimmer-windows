// Ported from Packages/GlimmerKit/Sources/GlimmerRender/Shaders/Composite.metal (Metal Shading Language built-ins)
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Glimmer.Render;

/// <summary>
/// The Metal Shading Language built-ins the shaders use, with Metal's definitions: <c>saturate</c> maps NaN to 0,
/// <c>fract(x) = x − floor(x)</c>, <c>fmod</c> truncates like C, <c>step(edge, x) = x &lt; edge ? 0 : 1</c>,
/// <c>smoothstep</c> is the Hermite curve on the clamped ratio.
/// </summary>
internal static class ShaderMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Saturate(float x) => x > 0f ? (x < 1f ? x : 1f) : 0f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Saturate(Vector3 v) => new(Saturate(v.X), Saturate(v.Y), Saturate(v.Z));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Saturate(Vector4 v) => new(Saturate(v.X), Saturate(v.Y), Saturate(v.Z), Saturate(v.W));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Fract(float x) => x - MathF.Floor(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Fract(Vector2 v) => new(Fract(v.X), Fract(v.Y));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Floor(Vector2 v) => new(MathF.Floor(v.X), MathF.Floor(v.Y));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Floor(Vector3 v) => new(MathF.Floor(v.X), MathF.Floor(v.Y), MathF.Floor(v.Z));

    /// <summary>C <c>fmod</c>: x − y·trunc(x / y).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Fmod(float x, float y) => x % y;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Fmod(Vector2 v, float y) => new(v.X % y, v.Y % y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Step(float edge, float x) => x < edge ? 0f : 1f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Saturate((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Smoothstep(float edge0, float edge1, Vector3 x) =>
        new(Smoothstep(edge0, edge1, x.X), Smoothstep(edge0, edge1, x.Y), Smoothstep(edge0, edge1, x.Z));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Mix(float a, float b, float t) => a + (b - a) * t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Mix(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Exp2(float x) => MathF.Pow(2f, x);

    /// <summary>
    /// <c>dot(a, b)</c> for two-component vectors as Apple's shader compiler emits it: a fused multiply-add,
    /// <c>fma(a.y, b.y, a.x * b.x)</c>. It matters where the result feeds a hash: measured on filter-add_noise.png
    /// (2026-11-19), this form reproduces 99.97 % of the Mac's noise channels; a plain float dot reproduces 82 %, a
    /// double-precision one 8 % (chance).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(Vector2 a, Vector2 b) => MathF.FusedMultiplyAdd(a.Y, b.Y, a.X * b.X);

    /// <summary>
    /// <c>sin</c> of a float argument, correctly rounded to float. The Mac's GPU computes the hash sines accurately
    /// even for arguments in the tens of thousands (same measurement as <see cref="Dot"/>); the double-precision sine
    /// makes the result the same on every platform's C runtime.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SinF(float x) => (float)Math.Sin(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Xyz(this Vector4 v) => new(v.X, v.Y, v.Z);
}
