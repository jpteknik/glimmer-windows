// Ported from Packages/GlimmerKit/Sources/GlimmerRender/Shaders/Canvas.metal
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Glimmer.Render;

internal static class CanvasShaders
{
    /// <summary>
    /// <c>canvas_fragment</c> at drawable pixel centre <paramref name="pixel"/> (origin top-left): the pasteboard, and
    /// the transparency checkerboard where the document is, with an anti-aliased document edge.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 CanvasFragment(Vector2 pixel, in CanvasUniforms u)
    {
        Vector2 doc = Vector2.Transform(pixel, u.PixelToDocument);

        // Distance outside the document rect, in drawable pixels (negative inside).
        Vector2 outside = Vector2.Max(-doc, doc - u.DocumentSize);
        float distancePx = MathF.Max(outside.X, outside.Y) / u.DocumentPerPixel;
        float coverage = Math.Clamp(0.5f - distancePx, 0f, 1f);

        // Checkerboard is fixed in screen space, like every pro editor.
        int cx = (int)MathF.Floor(pixel.X / u.CheckerSize), cy = (int)MathF.Floor(pixel.Y / u.CheckerSize);
        Vector4 checker = ((cx + cy) & 1) != 0 ? u.CheckerDark : u.CheckerLight;

        return Vector4.Lerp(u.Pasteboard, checker, coverage);
    }
}
