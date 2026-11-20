// Ported from Packages/GlimmerKit/Sources/GlimmerRender/Liquify.swift
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Glimmer.Core;

namespace Glimmer.Render;

/// <summary>Mirrors <c>LiquifyBrush</c> in Filters.metal.</summary>
internal struct LiquifyBrushUniforms
{
    /// <summary>Document px.</summary>
    public Vector2 Centre;
    /// <summary>Forward warp movement, document px.</summary>
    public Vector2 Delta;
    /// <summary>Document position of field texel (0, 0).</summary>
    public Vector2 FieldOrigin;
    /// <summary>Document px per field texel.</summary>
    public float FieldScale;
    /// <summary>Document px.</summary>
    public float Radius;
    /// <summary>0...1.</summary>
    public float Strength;
    /// <summary>0 forward, 1 restore, 2 pucker, 3 bloat, 4 twirl clockwise, 5 twirl counter-clockwise.</summary>
    public int Mode;
}

/// <summary>
/// Liquify on one raster layer: a displacement field edited by brushes and rendered on demand. Large layers use a coarser
/// field (bilinear) to stay light.
/// </summary>
public sealed class LiquifyEngine
{
    public enum Mode
    {
        Forward = 0,
        Restore,
        Pucker,
        Bloat,
        TwirlClockwise,
        TwirlCounterClockwise,
    }

    public TileGrid Original { get; }
    public PixelRect Region { get; }
    public bool IsIdentity { get; private set; } = true;

    private readonly RegionImage source;
    private readonly VectorField field;
    private readonly VectorField scratch;
    private readonly float fieldScale;
    /// <summary>The displacement field (for tests).</summary>
    internal VectorField Field => this.field;
    /// <summary>Document pixels per field texel (for tests).</summary>
    internal float FieldScale => fieldScale;
    /// <summary>The last <see cref="Render"/>, reused where the field hasn't changed since.</summary>
    private TileGrid? rendered;
    /// <summary>Field texels changed since the last <see cref="Render"/> (empty: none).</summary>
    private PixelRect dirty = new(0, 0, 0, 0);

    /// <param name="region">The area that can change (usually the document).</param>
    public LiquifyEngine(TileGrid tiles, PixelRect region)
    {
        Original = tiles;
        Region = RegionOps.TileAligned(region);
        RegionOps.CheckSize(Region);
        int longSide = Math.Max(Region.Width, Region.Height);
        fieldScale = longSide > 4096 ? (longSide + 4095) / 4096 : 1;
        int fw = (int)MathF.Ceiling(Region.Width / fieldScale), fh = (int)MathF.Ceiling(Region.Height / fieldScale);
        field = new VectorField(fw, fh);
        scratch = new VectorField(fw, fh);
        source = RegionImage.From(tiles, Region);
    }

    /// <summary>Throws away every change (Restore All).</summary>
    public void Reset()
    {
        Array.Clear(field.Values);
        Array.Clear(scratch.Values);
        IsIdentity = true;
        rendered = null;
        dirty = new PixelRect(0, 0, 0, 0);
    }

    /// <summary>One brush dab (document coordinates). Only the field texels around the dab are rewritten.</summary>
    public void Dab(Mode mode, Vec2 centre, Vec2 delta, double radius, double strength)
    {
        double s = fieldScale;
        int fx0 = (int)Math.Floor((centre.X - radius - Region.X) / s) - 1;
        int fy0 = (int)Math.Floor((centre.Y - radius - Region.Y) / s) - 1;
        int size = (int)Math.Ceiling(2 * radius / s) + 3;
        var box = new PixelRect(fx0, fy0, size, size).Intersection(new PixelRect(0, 0, field.Width, field.Height));
        if (box.IsEmpty) return;
        var u = new LiquifyBrushUniforms
        {
            Centre = new Vector2((float)centre.X, (float)centre.Y), Delta = new Vector2((float)delta.X, (float)delta.Y),
            FieldOrigin = new Vector2(Region.X, Region.Y), FieldScale = fieldScale,
            Radius = (float)radius, Strength = (float)strength, Mode = (int)mode,
        };
        // The kernel reads the field and writes scratch; then the box is copied back (the Mac blits scratch → field).
        Parallel.For(box.Y, box.MaxY, y =>
        {
            for (int x = box.X; x < box.MaxX; x++) scratch.Values[y * scratch.Width + x] = FilterShaders.LiquifyBrush(field, x, y, u);
        });
        for (int y = box.Y; y < box.MaxY; y++)
            scratch.Values.AsSpan(y * scratch.Width + box.X, box.Width).CopyTo(field.Values.AsSpan(y * field.Width + box.X));
        IsIdentity = false;
        dirty = dirty.IsEmpty ? box : dirty.Union(box);
    }

    /// <summary>The warped layer (tiles outside <see cref="Region"/> are untouched).</summary>
    /// <remarks>
    /// The Mac renders the whole region on the GPU each time. Here only tiles whose part of the field changed since the
    /// last render are rendered again: a pixel's output depends on the field where it is (bilinear, so one texel
    /// around) and on the untouched source, so the other tiles would come out the same.
    /// </remarks>
    public TileGrid Render()
    {
        if (IsIdentity) return Original;
        if (rendered is not null && dirty.IsEmpty) return rendered;
        var origin = new Vector2(Region.X, Region.Y);
        TileCoord[] coords;
        if (rendered is null)
        {
            coords = Region.TileCoords;
        }
        else
        {
            int s = (int)fieldScale;
            var changed = new PixelRect(Region.X + (dirty.X - 1) * s, Region.Y + (dirty.Y - 1) * s, (dirty.Width + 2) * s, (dirty.Height + 2) * s)
                .Intersection(Region);
            coords = changed.IsEmpty ? [] : changed.TileCoords;
        }
        var made = new Tile?[coords.Length];
        Parallel.For(0, coords.Length, i =>
        {
            const int s = TileGeometry.Size;
            var rect = coords[i].Rect;
            var buffer = new Half[TileGeometry.FloatCount];
            var row = new Vector4[s];
            for (int y = 0; y < s; y++)
            {
                int gy = rect.Y - Region.Y + y;
                for (int x = 0; x < s; x++)
                    row[x] = FilterShaders.LiquifyRender(source, field, rect.X - Region.X + x, gy, origin, origin, fieldScale);
                TensorPrimitives.ConvertToHalf(MemoryMarshal.Cast<Vector4, float>(row.AsSpan()), buffer.AsSpan(y * s * 4, s * 4));
            }
            made[i] = Tile.MakeOwned(buffer);
        });
        var b = (rendered ?? Original).ToBuilder();
        for (int i = 0; i < coords.Length; i++) b[coords[i]] = made[i];
        rendered = b.ToImmutable();
        dirty = new PixelRect(0, 0, 0, 0);
        return rendered;
    }
}

public static class LiquifyModeExtensions
{
    /// <summary>Swift's <c>CaseIterable.allCases</c>.</summary>
    public static IReadOnlyList<LiquifyEngine.Mode> AllCases { get; } = Enum.GetValues<LiquifyEngine.Mode>();

    public static string Title(this LiquifyEngine.Mode mode) => mode switch
    {
        LiquifyEngine.Mode.Forward => "Forward Warp",
        LiquifyEngine.Mode.Restore => "Reconstruct",
        LiquifyEngine.Mode.Pucker => "Pucker",
        LiquifyEngine.Mode.Bloat => "Bloat",
        LiquifyEngine.Mode.TwirlClockwise => "Twirl Clockwise",
        _ => "Twirl Counter-clockwise",
    };
}
