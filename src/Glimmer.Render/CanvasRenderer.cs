// Ported from Packages/GlimmerKit/Sources/GlimmerRender/CanvasRenderer.swift
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Glimmer.Core;

namespace Glimmer.Render;

/// <summary>Everything needed to draw one frame of the canvas.</summary>
/// <param name="ViewSize">View size in points.</param>
/// <param name="BackingScale">Drawable pixels per view point.</param>
/// <param name="PasteboardColor">Linear, premultiplied.</param>
/// <param name="CheckerSize">Checker square size in view points.</param>
public sealed record CanvasFrame(ViewTransform Transform, Vec2 ViewSize, double BackingScale, Vec2 DocumentSize,
                                 Vector4 PasteboardColor, double CheckerSize = 8);

/// <summary>Mirrors <c>CanvasUniforms</c> in Canvas.metal.</summary>
internal struct CanvasUniforms
{
    /// <summary>Drawable pixel (y-down) → document pixel, as a 2×3 affine map (<c>Vector2.Transform</c>).</summary>
    public Matrix3x2 PixelToDocument;
    public Vector2 DocumentSize;
    /// <summary>Drawable pixels per checker square.</summary>
    public float CheckerSize;
    /// <summary>Document pixels per drawable pixel (for AA).</summary>
    public float DocumentPerPixel;
    /// <summary>Linear, premultiplied.</summary>
    public Vector4 Pasteboard;
    public Vector4 CheckerLight;
    public Vector4 CheckerDark;
}

public static class LinearColor
{
    /// <summary>sRGB-encoded component → linear light.</summary>
    public static double Linearize(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    public static Vector4 FromSRGB(double r, double g, double b, double a = 1) =>
        new((float)(Linearize(r) * a), (float)(Linearize(g) * a), (float)(Linearize(b) * a), (float)a);
}

/// <summary>
/// The drawable the canvas is drawn into: 8-bit premultiplied sRGB, BGRA byte order (WPF's <c>Pbgra32</c>), row-major.
/// Either an array or memory such as a <c>WriteableBitmap</c>'s back buffer.
/// </summary>
public sealed class CanvasTarget
{
    private readonly uint[]? array;
    private readonly nint address;
    private readonly int stride;  // pixels for an array, bytes for memory

    /// <summary>A drawable in <paramref name="pixels"/> (at least width × height values; B in the low byte).</summary>
    public CanvasTarget(uint[] pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || pixels.LongLength < (long)width * height)
            throw new ArgumentException("The pixel array doesn't match the size.", nameof(pixels));
        array = pixels;
        Width = width;
        Height = height;
        stride = width;
    }

    /// <summary>A drawable in memory the caller owns (for example a locked back buffer), rows <paramref name="strideBytes"/> apart.</summary>
    public CanvasTarget(nint address, int width, int height, int strideBytes)
    {
        if (address == 0 || width <= 0 || height <= 0 || strideBytes < width * 4)
            throw new ArgumentException("The buffer doesn't match the size.", nameof(address));
        this.address = address;
        Width = width;
        Height = height;
        stride = strideBytes;
    }

    public int Width { get; }
    public int Height { get; }

    internal unsafe Span<uint> Row(int y) =>
        array is not null ? array.AsSpan(y * stride, Width) : new Span<uint>((void*)(address + (nint)y * stride), Width);
}

/// <summary>Draws the pasteboard, the transparency checkerboard and the composited tiles.</summary>
/// <remarks>
/// The Mac draws three passes into an RGBA16Float drawable (canvas, tiles blended "over", marching ants) and the display
/// pipeline encodes it. Here one pass computes each drawable pixel the same way and encodes it to 8-bit sRGB with an
/// ordered dither. Opaque tile pixels shown at 100 % or above, or at a zoom whose detail level matches, are read
/// straight from the tile (nearest texel) and encoded through a table; everything else takes the per-pixel path.
/// </remarks>
public sealed class CanvasRenderer
{
    private readonly Vector4 checkerLight = LinearColor.FromSRGB(1, 1, 1);
    private readonly Vector4 checkerDark = LinearColor.FromSRGB(0.8, 0.8, 0.8);

    private readonly Dictionary<long, float[]> selectionAlpha = [];
    private readonly Lock gate = new();

    public CanvasRenderer() { }

    /// <summary>Threads a frame may use (-1: as many as there are cores). For measuring the per-core cost.</summary>
    internal int MaxThreads { get; init; } = -1;

    internal CanvasUniforms Uniforms(CanvasFrame frame)
    {
        double pointsPerPixel = 1 / frame.BackingScale;
        var v = frame.Transform.ViewToDocumentMatrix;
        // m = viewToDocument · pixelToPoint: the first two columns scale, the translation stays.
        double m11 = v.Column0.X * pointsPerPixel, m12 = v.Column0.Y * pointsPerPixel;
        double m21 = v.Column1.X * pointsPerPixel, m22 = v.Column1.Y * pointsPerPixel;
        double docPerPixel = Math.Sqrt(m11 * m11 + m12 * m12);
        return new CanvasUniforms
        {
            PixelToDocument = new Matrix3x2((float)m11, (float)m12, (float)m21, (float)m22, (float)v.Column2.X, (float)v.Column2.Y),
            DocumentSize = new Vector2((float)frame.DocumentSize.X, (float)frame.DocumentSize.Y),
            CheckerSize = (float)(frame.CheckerSize * frame.BackingScale),
            DocumentPerPixel = (float)docPerPixel,
            Pasteboard = frame.PasteboardColor,
            CheckerLight = checkerLight,
            CheckerDark = checkerDark,
        };
    }

    /// <summary>
    /// Draws the canvas into <paramref name="target"/>. <paramref name="tile"/> returns the composited tile for a
    /// coordinate, if any (normally <see cref="DocumentCompositor.Texture"/>); only <paramref name="coords"/> are drawn.
    /// </summary>
    /// <remarks><paramref name="target"/> comes second (it is last in Swift) because C# optional parameters come last.</remarks>
    public void Encode(CanvasFrame frame, CanvasTarget target, IReadOnlyList<TileCoord>? coords = null,
                       Func<TileCoord, CompositeTile?>? tile = null, Selection? selection = null, double time = 0,
                       int channelMask = 15)
    {
        var state = Prepare(frame, coords, tile, selection, time, channelMask);
        int height = target.Height, width = target.Width;
        const int band = 8;
        Parallel.For(0, (height + band - 1) / band, new ParallelOptions { MaxDegreeOfParallelism = MaxThreads }, b =>
        {
            for (int y = b * band; y < Math.Min(height, (b + 1) * band); y++) EncodeRow(state, y, target.Row(y), width);
        });
    }

    /// <summary>The drawable as the Mac has it before display (linear, premultiplied), for tests.</summary>
    internal Vector4[] RenderLinear(CanvasFrame frame, int width, int height, IReadOnlyList<TileCoord>? coords = null,
                                    Func<TileCoord, CompositeTile?>? tile = null, Selection? selection = null, double time = 0,
                                    int channelMask = 15)
    {
        var state = Prepare(frame, coords, tile, selection, time, channelMask);
        var output = new Vector4[width * height];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++) output[y * width + x] = PixelLinear(state, x, y);
        });
        return output;
    }

    // MARK: Frame state

    private sealed class FrameState
    {
        public CanvasUniforms U;
        public float M11, M12, M21, M22, M31, M32;
        /// <summary>The document rect tiles are clipped to (whole pixels, as the Mac's <c>Int(documentSize)</c>).</summary>
        public int DocW, DocH;
        public int MinTx, MinTy, Cols, Rows;
        public CompositeTile?[] Tiles = [];
        public float?[] TileLod = [];
        public float[]?[] Ants = [];
        public bool HasAnts;
        public float Log2Rho;
        /// <summary>Document points at least this far inside the document get the full checkerboard (no edge blend).</summary>
        public float Inset;
        public Vector2 DuvDx, DuvDy;
        public float Time;
        public int ChannelMask;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Index(int tx, int ty)
        {
            int i = tx - MinTx, j = ty - MinTy;
            return (uint)i < (uint)Cols && (uint)j < (uint)Rows ? j * Cols + i : -1;
        }
    }

    private FrameState Prepare(CanvasFrame frame, IReadOnlyList<TileCoord>? coords, Func<TileCoord, CompositeTile?>? tile,
                               Selection? selection, double time, int channelMask)
    {
        var u = Uniforms(frame);
        var m = u.PixelToDocument;
        var s = new FrameState
        {
            U = u, M11 = m.M11, M12 = m.M12, M21 = m.M21, M22 = m.M22, M31 = m.M31, M32 = m.M32,
            DocW = (int)frame.DocumentSize.X, DocH = (int)frame.DocumentSize.Y,
            Time = (float)(time % 1000), ChannelMask = channelMask,
            DuvDx = new Vector2(m.M11, m.M12) / TileGeometry.Size, DuvDy = new Vector2(m.M21, m.M22) / TileGeometry.Size,
        };
        // Texel-space footprint of a drawable pixel at the full tile size; a tile at mip level k is 2^k times smaller.
        float rho = MathF.Max(new Vector2(m.M11, m.M12).Length(), new Vector2(m.M21, m.M22).Length());
        s.Log2Rho = MathF.Log2(rho);
        s.Inset = 0.5f * u.DocumentPerPixel + 0.01f;
        if (coords is null || coords.Count == 0 || tile is null) return s;

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var c in coords)
        {
            minX = Math.Min(minX, c.X); minY = Math.Min(minY, c.Y);
            maxX = Math.Max(maxX, c.X); maxY = Math.Max(maxY, c.Y);
        }
        s.MinTx = minX; s.MinTy = minY; s.Cols = maxX - minX + 1; s.Rows = maxY - minY + 1;
        if ((long)s.Cols * s.Rows > 4_000_000) throw new ArgumentException("Too many tiles for one frame.", nameof(coords));
        s.Tiles = new CompositeTile?[s.Cols * s.Rows];
        s.TileLod = new float?[s.Cols * s.Rows];
        s.Ants = new float[]?[s.Cols * s.Rows];
        var docRect = new PixelRect(0, 0, s.DocW, s.DocH);
        foreach (var c in coords)
        {
            int i = s.Index(c.X, c.Y);
            if (c.Rect.Intersection(docRect).IsEmpty) continue;  // never draw outside the document
            if (tile(c) is { } t)
            {
                s.Tiles[i] = t;
                s.TileLod[i] = s.Log2Rho - t.Level;
            }
        }
        // Marching ants on stored selection tiles (uniform areas have no edges).
        if (selection is not null && !selection.Tiles.IsEmpty)
        {
            lock (gate)
            {
                if (selectionAlpha.Count > 256) selectionAlpha.Clear();
                foreach (var c in coords)
                {
                    if (selection.Tiles[c] is not { IsSolid: false } st) continue;
                    if (!selectionAlpha.TryGetValue(st.Id, out var alpha))
                    {
                        alpha = new float[TileGeometry.PixelCount];
                        var px = st.PixelSpan;
                        for (int k = 0; k < alpha.Length; k++) alpha[k] = (float)px[k * 4 + 3];
                        selectionAlpha[st.Id] = alpha;
                    }
                    s.Ants[s.Index(c.X, c.Y)] = alpha;
                    s.HasAnts = true;
                }
            }
        }
        return s;
    }

    // MARK: Pixels

    private static void EncodeRow(FrameState s, int y, Span<uint> output, int width)
    {
        if (s.M12 == 0f && s.M21 == 0f && s.M11 > 0f && s.M22 > 0f) EncodeRowAxisAligned(s, y, output, width);
        else EncodeRowRotated(s, y, output, width);
        if (!s.HasAnts) return;
        float py = y + 0.5f;
        float rowX = s.M21 * py + s.M31, rowY = s.M22 * py + s.M32;
        for (int x = 0; x < width; x++)
        {
            float px = x + 0.5f;
            if (Ants(s, new Vector2(px, py), new Vector2(s.M11 * px + rowX, s.M12 * px + rowY)) is { } white)
                output[x] = white ? 0xFFFFFFFFu : 0xFF000000u;
        }
    }

    /// <summary>
    /// An unrotated view: along a row the document y is fixed, so the row splits into runs that each read one row of one
    /// tile.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EncodeRowAxisAligned(FrameState s, int y, Span<uint> output, int width)
    {
        float py = y + 0.5f;
        float dy = s.M22 * py + s.M32;
        bool rowInDocument = dy >= 0f && dy < s.DocH;
        int ty = rowInDocument ? (int)(dy * (1f / 256f)) : 0;
        bool plain = s.ChannelMask == 15;
        int x = 0;
        while (x < width)
        {
            float px = x + 0.5f;
            float dx = s.M11 * px + s.M31;
            if (!rowInDocument || !(dx >= 0f && dx < s.DocW))
            {
                output[x] = DisplayEncoding.EncodeBgra(Background(s, new Vector2(px, py), dx, dy), x, y);
                x++;
                continue;
            }
            int tx = (int)(dx * (1f / 256f));
            int index = s.Index(tx, ty);
            CompositeTile? t = index >= 0 ? s.Tiles[index] : null;
            // The run: the following pixels whose document x stays in this tile column and inside the document.
            int end = x + 1;
            while (end < width)
            {
                float d = s.M11 * (end + 0.5f) + s.M31;
                if (!(d < s.DocW) || (int)(d * (1f / 256f)) != tx) break;
                end++;
            }
            if (t is not null && plain && s.TileLod[index] <= 0f)
            {
                // Magnified (or 1:1 at the tile's level): nearest texel, straight from the Float16 data.
                int w = t.Width;
                int iy = Math.Clamp((int)MathF.Floor((dy * (1f / 256f) - ty) * w), 0, w - 1);
                var bits = MemoryMarshal.Cast<Half, ushort>(t.Data.AsSpan(iy * w * 4, w * 4));
                for (int xi = x; xi < end; xi++)
                {
                    float dxi = s.M11 * (xi + 0.5f) + s.M31;
                    int o = Math.Clamp((int)MathF.Floor((dxi * (1f / 256f) - tx) * w), 0, w - 1) * 4;
                    if (bits[o + 3] == 0x3C00)
                    {
                        output[xi] = DisplayEncoding.EncodeOpaqueHalf(bits[o], bits[o + 1], bits[o + 2], xi, y);
                    }
                    else
                    {
                        var c = DisplayEncoding.LoadHalf4(bits, o);
                        Vector4 bg = Background(s, new Vector2(xi + 0.5f, py), dxi, dy);
                        output[xi] = DisplayEncoding.EncodeBgra(c + bg * (1f - c.W), xi, y);
                    }
                }
            }
            else
            {
                for (int xi = x; xi < end; xi++)
                {
                    float pxi = xi + 0.5f;
                    var doc = new Vector2(s.M11 * pxi + s.M31, dy);
                    output[xi] = DisplayEncoding.EncodeBgra(Shade(s, t, index, new Vector2(pxi, py), doc), xi, y);
                }
            }
            x = end;
        }
    }

    /// <summary>Any other view, pixel by pixel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EncodeRowRotated(FrameState s, int y, Span<uint> output, int width)
    {
        float py = y + 0.5f;
        float rowX = s.M21 * py + s.M31, rowY = s.M22 * py + s.M32;
        bool plain = s.ChannelMask == 15;
        for (int x = 0; x < width; x++)
        {
            float px = x + 0.5f;
            float dx = s.M11 * px + rowX, dy = s.M12 * px + rowY;
            int index = -1;
            if (dx >= 0f && dy >= 0f && dx < s.DocW && dy < s.DocH) index = s.Index((int)(dx * (1f / 256f)), (int)(dy * (1f / 256f)));
            CompositeTile? t = index >= 0 ? s.Tiles[index] : null;
            uint encoded;
            if (t is not null && plain && s.TileLod[index] <= 0f)
            {
                // Magnified (or 1:1 at the tile's level): nearest texel, straight from the Float16 data.
                int w = t.Width;
                int tx = (int)(dx * (1f / 256f)), ty = (int)(dy * (1f / 256f));
                int ix = Math.Clamp((int)MathF.Floor((dx * (1f / 256f) - tx) * w), 0, w - 1);
                int iy = Math.Clamp((int)MathF.Floor((dy * (1f / 256f) - ty) * w), 0, w - 1);
                var bits = MemoryMarshal.Cast<Half, ushort>(t.Data.AsSpan((iy * w + ix) * 4, 4));
                if (bits[3] == 0x3C00)
                {
                    encoded = DisplayEncoding.EncodeOpaqueHalf(bits[0], bits[1], bits[2], x, y);
                }
                else
                {
                    var c = DisplayEncoding.LoadHalf4(bits, 0);
                    Vector4 bg = Background(s, new Vector2(px, py), dx, dy);
                    encoded = DisplayEncoding.EncodeBgra(c + bg * (1f - c.W), x, y);
                }
            }
            else
            {
                encoded = DisplayEncoding.EncodeBgra(Shade(s, t, index, new Vector2(px, py), new Vector2(dx, dy)), x, y);
            }
            output[x] = encoded;
        }
    }

    /// <summary>One drawable pixel, linear and premultiplied: canvas, the tile "over" it, the ants on top.</summary>
    private static Vector4 PixelLinear(FrameState s, int x, int y)
    {
        var p = new Vector2(x + 0.5f, y + 0.5f);
        var doc = new Vector2(s.M11 * p.X + s.M21 * p.Y + s.M31, s.M12 * p.X + s.M22 * p.Y + s.M32);
        int index = -1;
        if (doc.X >= 0f && doc.Y >= 0f && doc.X < s.DocW && doc.Y < s.DocH) index = s.Index((int)(doc.X / 256f), (int)(doc.Y / 256f));
        Vector4 c = Shade(s, index >= 0 ? s.Tiles[index] : null, index, p, doc);
        if (s.HasAnts && Ants(s, p, doc) is { } white) c = white ? Vector4.One : new Vector4(0, 0, 0, 1);
        return c;
    }

    private static Vector4 Shade(FrameState s, CompositeTile? t, int index, Vector2 p, Vector2 doc)
    {
        if (t is null) return Background(s, p, doc.X, doc.Y);
        var origin = new Vector2(MathF.Floor(doc.X / 256f), MathF.Floor(doc.Y / 256f));
        Vector2 uv = doc / 256f - origin;
        Vector4 sampled = CompositeShaders.PresentChannels(Sample(t, uv, s.TileLod[index] ?? 0f), s.ChannelMask);
        if (sampled.W == 1f) return sampled;  // "over" an opaque pixel leaves nothing of the background
        Vector4 c = Background(s, p, doc.X, doc.Y);
        return sampled + c * (1f - sampled.W);
    }

    /// <summary>
    /// <c>canvas_fragment</c> at drawable pixel <paramref name="p"/> whose document point is (<paramref name="dx"/>,
    /// <paramref name="dy"/>): well inside the document only the checkerboard square is looked up, well outside it is
    /// the pasteboard; within half a pixel of the edge the full function blends the two.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector4 Background(FrameState s, Vector2 p, float dx, float dy)
    {
        float inset = s.Inset;
        Vector2 size = s.U.DocumentSize;
        if (dx >= inset && dy >= inset && dx <= size.X - inset && dy <= size.Y - inset)
        {
            int cx = (int)MathF.Floor(p.X / s.U.CheckerSize), cy = (int)MathF.Floor(p.Y / s.U.CheckerSize);
            return ((cx + cy) & 1) != 0 ? s.U.CheckerDark : s.U.CheckerLight;
        }
        if (dx < -inset || dy < -inset || dx > size.X + inset || dy > size.Y + inset) return s.U.Pasteboard;
        return CanvasShaders.CanvasFragment(p, s.U);
    }

    /// <summary>
    /// <c>present_fragment</c>'s sampler (mag nearest, min linear, mip linear, clamp to edge) on one composited tile.
    /// </summary>
    internal static Vector4 Sample(CompositeTile t, Vector2 uv, float lod)
    {
        int w = t.Width;
        if (lod <= 0f)
        {
            int ix = Math.Clamp((int)MathF.Floor(uv.X * w), 0, w - 1), iy = Math.Clamp((int)MathF.Floor(uv.Y * w), 0, w - 1);
            return t.Read(ix, iy);
        }
        int maxLevel = t.MipLevelCount - 1;
        lod = MathF.Min(lod, maxLevel);
        int l0 = (int)MathF.Floor(lod);
        float f = lod - l0;
        Vector4 a = Bilinear(t, l0, uv);
        if (f <= 0f || l0 >= maxLevel) return a;
        Vector4 b = Bilinear(t, l0 + 1, uv);
        return a + (b - a) * f;
    }

    private static Vector4 Bilinear(CompositeTile t, int level, Vector2 uv)
    {
        int w = Math.Max(1, t.Width >> level);
        float px = uv.X * w - 0.5f, py = uv.Y * w - 0.5f;
        float fx0 = MathF.Floor(px), fy0 = MathF.Floor(py);
        float fx = px - fx0, fy = py - fy0;
        int x0 = Math.Clamp((int)fx0, 0, w - 1), x1 = Math.Clamp((int)fx0 + 1, 0, w - 1);
        int y0 = Math.Clamp((int)fy0, 0, w - 1), y1 = Math.Clamp((int)fy0 + 1, 0, w - 1);
        Vector4 a, b, c, d;
        if (level == 0)
        {
            var data = MemoryMarshal.Cast<Half, ushort>(t.Data.AsSpan());
            a = DisplayEncoding.LoadHalf4(data, (y0 * w + x0) * 4); b = DisplayEncoding.LoadHalf4(data, (y0 * w + x1) * 4);
            c = DisplayEncoding.LoadHalf4(data, (y1 * w + x0) * 4); d = DisplayEncoding.LoadHalf4(data, (y1 * w + x1) * 4);
        }
        else
        {
            var mip = t.Mip(level);
            a = mip[y0 * w + x0]; b = mip[y0 * w + x1]; c = mip[y1 * w + x0]; d = mip[y1 * w + x1];
        }
        Vector4 top = a + (b - a) * fx, bottom = c + (d - c) * fx;
        return top + (bottom - top) * fy;
    }


    /// <summary><c>ants_fragment</c>: null where no ant is drawn, else whether the dash is white.</summary>
    private static bool? Ants(FrameState s, Vector2 p, Vector2 doc)
    {
        float ftx = MathF.Floor(doc.X / 256f), fty = MathF.Floor(doc.Y / 256f);
        if (ftx < int.MinValue / 2 || ftx > int.MaxValue / 2 || fty < int.MinValue / 2 || fty > int.MaxValue / 2) return null;
        int index = s.Index((int)ftx, (int)fty);
        if (index < 0 || s.Ants[index] is not { } alpha) return null;
        Vector2 uv = doc / 256f - new Vector2(ftx, fty);
        bool inside = SampleAlpha(alpha, uv) >= 0.5f;
        if (!inside) return null;
        bool edge = SampleAlpha(alpha, uv - s.DuvDx) >= 0.5f != inside || SampleAlpha(alpha, uv + s.DuvDx) >= 0.5f != inside
                 || SampleAlpha(alpha, uv - s.DuvDy) >= 0.5f != inside || SampleAlpha(alpha, uv + s.DuvDy) >= 0.5f != inside;
        if (!edge) return null;
        return CompositeShaders.AntsDashIsWhite(p, s.Time);
    }

    private static float SampleAlpha(float[] alpha, Vector2 uv)
    {
        const int w = TileGeometry.Size;
        float px = uv.X * w - 0.5f, py = uv.Y * w - 0.5f;
        float fx0 = MathF.Floor(px), fy0 = MathF.Floor(py);
        float fx = px - fx0, fy = py - fy0;
        int x0 = Math.Clamp((int)fx0, 0, w - 1), x1 = Math.Clamp((int)fx0 + 1, 0, w - 1);
        int y0 = Math.Clamp((int)fy0, 0, w - 1), y1 = Math.Clamp((int)fy0 + 1, 0, w - 1);
        float a = alpha[y0 * w + x0], b = alpha[y0 * w + x1], c = alpha[y1 * w + x0], d = alpha[y1 * w + x1];
        float top = a + (b - a) * fx, bottom = c + (d - c) * fx;
        return top + (bottom - top) * fy;
    }
}
