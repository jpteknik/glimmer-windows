// Ported from Packages/GlimmerKit/Sources/GlimmerRender/TilePipelines.swift (the region textures RegionOps assembles, and
// the Metal samplers the kernels read them with)
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Glimmer.Core;

namespace Glimmer.Render;

/// <summary>
/// A region assembled from tiles: premultiplied linear RGBA as float, row-major. This is what the Mac keeps in an
/// RGBA16Float region texture; the samplers mirror the Metal samplers the kernels declare (<c>filter::linear</c>,
/// <c>coord::pixel</c>, with <c>clamp_to_edge</c> or <c>clamp_to_zero</c>).
/// </summary>
internal sealed class RegionImage
{
    public readonly int Width;
    public readonly int Height;
    public readonly Vector4[] Pixels;

    public RegionImage(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new Vector4[(long)width * height];
    }

    public RegionImage(int width, int height, Vector4[] pixels)
    {
        if (pixels.LongLength < (long)width * height) throw new ArgumentException("The pixel buffer is too small.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>The pixels of <paramref name="rect"/> in <paramref name="grid"/> (transparent where there is no tile).</summary>
    public static RegionImage From(TileGrid grid, PixelRect rect)
    {
        var image = new RegionImage(rect.Width, rect.Height);
        grid.ReadRegion(rect, image.Floats);
        return image;
    }

    public Span<float> Floats => MemoryMarshal.Cast<Vector4, float>(Pixels.AsSpan(0, Width * Height));

    public Span<Vector4> Row(int y) => Pixels.AsSpan(y * Width, Width);

    /// <summary><c>texture.read(gid)</c> with the coordinate clamped to the edge.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector4 Read(int x, int y)
    {
        x = x < 0 ? 0 : (x >= Width ? Width - 1 : x);
        y = y < 0 ? 0 : (y >= Height ? Height - 1 : y);
        return Pixels[y * Width + x];
    }

    /// <summary>Zero outside the image (<c>clamp_to_zero</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector4 ReadZero(int x, int y) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height ? Pixels[y * Width + x] : Vector4.Zero;

    /// <summary>Bilinear sample at pixel coordinate <paramref name="p"/> (texel centres at i + 0.5), edges clamped.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector4 SampleClampEdge(Vector2 p)
    {
        float tx = p.X - 0.5f, ty = p.Y - 0.5f;
        float fx0 = MathF.Floor(tx), fy0 = MathF.Floor(ty);
        float fx = tx - fx0, fy = ty - fy0;
        int x0 = (int)fx0, y0 = (int)fy0;
        if (fx == 0f && fy == 0f) return Read(x0, y0);
        Vector4 a = Read(x0, y0), b = Read(x0 + 1, y0), c = Read(x0, y0 + 1), d = Read(x0 + 1, y0 + 1);
        Vector4 top = a + (b - a) * fx;
        Vector4 bottom = c + (d - c) * fx;
        return top + (bottom - top) * fy;
    }

    /// <summary>Bilinear sample at pixel coordinate <paramref name="p"/>, transparent outside the image.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector4 SampleClampZero(Vector2 p)
    {
        float tx = p.X - 0.5f, ty = p.Y - 0.5f;
        float fx0 = MathF.Floor(tx), fy0 = MathF.Floor(ty);
        float fx = tx - fx0, fy = ty - fy0;
        int x0 = (int)fx0, y0 = (int)fy0;
        if (fx == 0f && fy == 0f) return ReadZero(x0, y0);
        Vector4 a = ReadZero(x0, y0), b = ReadZero(x0 + 1, y0), c = ReadZero(x0, y0 + 1), d = ReadZero(x0 + 1, y0 + 1);
        Vector4 top = a + (b - a) * fx;
        Vector4 bottom = c + (d - c) * fx;
        return top + (bottom - top) * fy;
    }
}

/// <summary>One float per pixel (the Mac's r32Float fields, masks and selections read into a region, depth maps).</summary>
internal sealed class FloatImage
{
    public readonly int Width;
    public readonly int Height;
    public readonly float[] Pixels;

    public FloatImage(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new float[(long)width * height];
    }

    public FloatImage(int width, int height, float[] pixels)
    {
        if (pixels.LongLength < (long)width * height) throw new ArgumentException("The pixel buffer is too small.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>One channel of <paramref name="rect"/> (masks and selections store their value in every channel).</summary>
    public static FloatImage From(TileGrid grid, PixelRect rect, float defaultValue, int channel = 3)
    {
        var image = new FloatImage(rect.Width, rect.Height);
        grid.ReadChannel(rect, image.Pixels, channel, defaultValue);
        return image;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Read(int x, int y)
    {
        x = x < 0 ? 0 : (x >= Width ? Width - 1 : x);
        y = y < 0 ? 0 : (y >= Height ? Height - 1 : y);
        return Pixels[y * Width + x];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float SampleClampEdge(Vector2 p)
    {
        float tx = p.X - 0.5f, ty = p.Y - 0.5f;
        float fx0 = MathF.Floor(tx), fy0 = MathF.Floor(ty);
        float fx = tx - fx0, fy = ty - fy0;
        int x0 = (int)fx0, y0 = (int)fy0;
        if (fx == 0f && fy == 0f) return Read(x0, y0);
        float a = Read(x0, y0), b = Read(x0 + 1, y0), c = Read(x0, y0 + 1), d = Read(x0 + 1, y0 + 1);
        float top = a + (b - a) * fx;
        float bottom = c + (d - c) * fx;
        return top + (bottom - top) * fy;
    }
}

/// <summary>Two floats per pixel (the liquify displacement field, rg32Float on the Mac).</summary>
internal sealed class VectorField
{
    public readonly int Width;
    public readonly int Height;
    public readonly Vector2[] Values;

    public VectorField(int width, int height)
    {
        Width = width;
        Height = height;
        Values = new Vector2[(long)width * height];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 Read(int x, int y)
    {
        x = x < 0 ? 0 : (x >= Width ? Width - 1 : x);
        y = y < 0 ? 0 : (y >= Height ? Height - 1 : y);
        return Values[y * Width + x];
    }

    /// <summary>Bilinear, edges clamped, pixel coordinates.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 SampleClampEdge(Vector2 p)
    {
        float tx = p.X - 0.5f, ty = p.Y - 0.5f;
        float fx0 = MathF.Floor(tx), fy0 = MathF.Floor(ty);
        float fx = tx - fx0, fy = ty - fy0;
        int x0 = (int)fx0, y0 = (int)fy0;
        if (fx == 0f && fy == 0f) return Read(x0, y0);
        Vector2 a = Read(x0, y0), b = Read(x0 + 1, y0), c = Read(x0, y0 + 1), d = Read(x0 + 1, y0 + 1);
        Vector2 top = a + (b - a) * fx;
        Vector2 bottom = c + (d - c) * fx;
        return top + (bottom - top) * fy;
    }
}
