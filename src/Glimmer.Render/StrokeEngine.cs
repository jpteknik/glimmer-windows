// Ported from Packages/GlimmerKit/Sources/GlimmerRender/StrokeEngine.swift
using System.Numerics;
using Glimmer.Core;

namespace Glimmer.Render;

/// <summary>Mirrors <c>DabInstance</c> in Composite.metal.</summary>
public struct DabInstance : IEquatable<DabInstance>
{
    /// <summary>Document pixels.</summary>
    public Vector2 Center;
    public float Radius;
    public float Hardness;
    public float Roundness;
    /// <summary>Radians.</summary>
    public float Angle;
    /// <summary>0...1 (already pressure-scaled).</summary>
    public float Flow;
    /// <summary>0 or 1.</summary>
    public float Textured;

    /// <remarks>
    /// <paramref name="flow"/> has no default in Swift; here it defaults to 1 because a C# parameter without a default
    /// can't follow ones with defaults.
    /// </remarks>
    public DabInstance(Vector2 center, float radius, float hardness, float roundness = 1, float angle = 0, float flow = 1,
                       bool textured = false)
    {
        Center = center;
        Radius = radius;
        Hardness = hardness;
        Roundness = roundness;
        Angle = angle;
        Flow = flow;
        Textured = textured ? 1 : 0;
    }

    /// <summary>The pixels the dab can touch.</summary>
    internal readonly PixelRect Bounds
    {
        get
        {
            int r = (int)MathF.Ceiling(Radius + 2);
            return new PixelRect((int)Center.X - r, (int)Center.Y - r, 2 * r + 1, 2 * r + 1);
        }
    }

    public readonly bool Equals(DabInstance other) =>
        Center == other.Center && Radius == other.Radius && Hardness == other.Hardness && Roundness == other.Roundness
        && Angle == other.Angle && Flow == other.Flow && Textured == other.Textured;

    public override readonly bool Equals(object? obj) => obj is DabInstance d && Equals(d);
    public override readonly int GetHashCode() => HashCode.Combine(Center, Radius, Hardness, Roundness, Angle, Flow, Textured);
    public static bool operator ==(DabInstance a, DabInstance b) => a.Equals(b);
    public static bool operator !=(DabInstance a, DabInstance b) => !a.Equals(b);
}

/// <summary>
/// One brush stroke on a layer's pixels or mask. Dabs accumulate in per-tile coverage ("wet") textures; after each batch
/// the touched tiles are recomputed from the untouched originals, so opacity caps the stroke like Photoshop.
/// </summary>
public sealed class StrokeEngine
{
    /// <summary>What the stroke paints (Swift's <c>enum Target</c>).</summary>
    public abstract record Target
    {
        private Target() { }

        public sealed record Pixels : Target;

        /// <summary>Painting a mask: missing tiles use <see cref="DefaultValue"/>.</summary>
        public sealed record Mask(float DefaultValue) : Target;
    }

    public TileGrid Original { get; }
    public TileGrid Result { get; private set; }

    private readonly PixelOps ops = new();
    private readonly Target target;
    private readonly PixelOps.Mode mode;
    private readonly Vector4 color;
    private readonly float opacity;
    private readonly float maskTarget;
    private readonly Selection? selection;
    private readonly TileGrid? effect;
    private readonly Dictionary<TileCoord, float[]> wet = [];

    public StrokeEngine(TileGrid tiles, Target target, bool erase, Vector4 color, float opacity, float maskTarget = 0,
                        Selection? selection = null)
    {
        Original = tiles;
        Result = tiles;
        this.target = target;
        mode = target is Target.Mask ? PixelOps.Mode.Mask : erase ? PixelOps.Mode.Erase : PixelOps.Mode.Paint;
        this.color = color;
        this.opacity = opacity;
        this.maskTarget = maskTarget;
        this.selection = selection;
        effect = null;
    }

    /// <summary>
    /// A retouch stroke on pixels: <see cref="PixelOps.Mode.TowardEffect"/> blends toward <paramref name="effect"/>
    /// (clone, heal, blur, sharpen); <see cref="PixelOps.Mode.Dodge"/>/<see cref="PixelOps.Mode.Burn"/> take exposure
    /// and range in <paramref name="parameters"/>.xy.
    /// </summary>
    public StrokeEngine(TileGrid tiles, PixelOps.Mode retouch, TileGrid? effect, Vector4 parameters, float opacity,
                        Selection? selection)
    {
        Original = tiles;
        Result = tiles;
        target = new Target.Pixels();
        mode = retouch;
        color = parameters;
        this.opacity = opacity;
        maskTarget = 0;
        this.selection = selection;
        this.effect = effect;
    }

    /// <summary>Stamps <paramref name="dabs"/> and updates <see cref="Result"/>. Returns the coordinates that changed.</summary>
    public IReadOnlySet<TileCoord> Add(IReadOnlyList<DabInstance> dabs)
    {
        if (dabs.Count == 0) return new HashSet<TileCoord>();
        var byTile = new Dictionary<TileCoord, List<DabInstance>>();
        foreach (var dab in dabs)
        {
            foreach (var coord in dab.Bounds.TileCoords)
            {
                if (!byTile.TryGetValue(coord, out var list)) byTile[coord] = list = [];
                list.Add(dab);
            }
        }
        var coords = byTile.Keys.ToArray();
        var buffers = new float[coords.Length][];
        for (int i = 0; i < coords.Length; i++)
        {
            if (!wet.TryGetValue(coords[i], out var buffer)) wet[coords[i]] = buffer = new float[TileGeometry.PixelCount];
            buffers[i] = buffer;
        }
        Parallel.For(0, coords.Length, i =>
        {
            var origin = coords[i].Origin;
            Stamp(buffers[i], TileGeometry.Size, new Vector2(origin.X, origin.Y), CollectionsMarshalSpan(byTile[coords[i]]));
        });
        float? sourceDefault = target is Target.Mask m ? m.DefaultValue : null;
        var updated = ops.Encode(coords, Original, sourceDefault, c => wet.GetValueOrDefault(c), fullCoverage: false, selection,
                                 mode, color, opacity, maskTarget, effect);
        var b = Result.ToBuilder();
        foreach (var (c, t) in updated) b[c] = t;
        Result = b.ToImmutable();
        return new HashSet<TileCoord>(coords);
    }

    /// <summary>Waits for the GPU on the Mac (call before handing <see cref="Result"/> to the document); work here is synchronous.</summary>
    public void Finish() { }

    private static ReadOnlySpan<DabInstance> CollectionsMarshalSpan(List<DabInstance> list) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);

    /// <summary>
    /// <c>dab_vertex</c>/<c>dab_fragment</c> instanced into one tile's wet buffer (size × size, row-major, top-left at
    /// document pixel <paramref name="origin"/>): each dab's quad covers the pixels whose centres lie within its radius
    /// + 1.5 px, and its coverage is accumulated "over" what the stroke already laid down (one − (1 − a)(1 − wet)), so
    /// flow builds up within a stroke.
    /// </summary>
    internal static void Stamp(Span<float> wet, int size, Vector2 origin, ReadOnlySpan<DabInstance> dabs)
    {
        foreach (ref readonly DabInstance d in dabs)
        {
            float r = CompositeShaders.DabExtent(d);
            // Pixel centres x + 0.5 inside (cx − r, cx + r): the rows and columns the quad covers.
            int x0 = Math.Max(0, (int)MathF.Ceiling(d.Center.X - r - origin.X - 0.5f));
            int x1 = Math.Min(size - 1, (int)MathF.Floor(d.Center.X + r - origin.X - 0.5f));
            int y0 = Math.Max(0, (int)MathF.Ceiling(d.Center.Y - r - origin.Y - 0.5f));
            int y1 = Math.Min(size - 1, (int)MathF.Floor(d.Center.Y + r - origin.Y - 0.5f));
            for (int y = y0; y <= y1; y++)
            {
                var row = wet.Slice(y * size, size);
                float py = origin.Y + y + 0.5f;
                for (int x = x0; x <= x1; x++)
                {
                    float a = CompositeShaders.DabFragment(d, new Vector2(origin.X + x + 0.5f, py));
                    if (a > 0f) row[x] = a + row[x] * (1f - a);
                }
            }
        }
    }
}
