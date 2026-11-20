// Ported from Packages/GlimmerKit/Sources/GlimmerRender/FilterEngine.swift
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Glimmer.Core;

namespace Glimmer.Render;

/// <summary>Mirrors <c>FilterUniforms</c> in Filters.metal.</summary>
internal struct FilterUniforms
{
    public int Kind;
    /// <summary>Document position of texel (0, 0).</summary>
    public Vector2 TexOrigin;
    /// <summary>Parameters 0-3.</summary>
    public Vector4 A;
    /// <summary>Parameters 4-7.</summary>
    public Vector4 B;
    /// <summary>Document frame (vignette / chromatic aberration centre).</summary>
    public Vector2 FrameOrigin;
    public Vector2 FrameSize;
}

/// <summary>Mirrors <c>SmudgeDab</c> in Filters.metal.</summary>
public struct SmudgeDab
{
    /// <summary>In document pixels when handed to <see cref="SmudgeEngine"/>; in working-texture texels in the kernel.</summary>
    public Vector2 Centre;
    /// <summary>Movement since the previous dab.</summary>
    public Vector2 Delta;
    public float Radius;
    public float Hardness;
    public float Strength;

    public SmudgeDab(Vector2 centre, Vector2 delta, float radius, float hardness, float strength)
    {
        Centre = centre;
        Delta = delta;
        Radius = radius;
        Hardness = hardness;
        Strength = strength;
    }
}

/// <summary>
/// Destructive filters and the precomputed results retouch brushes paint toward. Everything stays in linear Float16
/// tiles; only the changed area is rewritten.
/// </summary>
/// <remarks>
/// The Mac filters the whole area in one GPU pass. Here the area is processed in bands of whole tile rows, each read
/// with the filter's reach around it, so memory stays bounded on large documents; every pixel sees exactly the
/// neighbourhood it would see in one pass (a filter never reads farther than its reach, and the edges of the read area
/// clamp where the full pass clamps).
/// </remarks>
public sealed class FilterEngine
{
    /// <summary>Rows per band (a multiple of the tile size; raised for filters that read far). For tests.</summary>
    internal int BandRows { get; init; } = 4 * TileGeometry.Size;

    public FilterEngine() { }

    internal static int KindIndex(FilterKind k) => k switch
    {
        FilterKind.GaussianBlur => 0,
        FilterKind.MotionBlur => 1,
        FilterKind.LensBlur => 2,
        FilterKind.Sharpen => 3,
        FilterKind.UnsharpMask => 4,
        FilterKind.Clarity => 5,
        FilterKind.AddNoise => 6,
        FilterKind.Pixelate => 7,
        FilterKind.FindEdges => 8,
        FilterKind.Emboss => 9,
        FilterKind.OilPaint => 10,
        FilterKind.Halftone => 11,
        FilterKind.Vignette => 12,
        _ => 13,  // chromatic aberration
    };

    internal static Vector4 Parameters(FilterSettings f)
    {
        float V(string key) => (float)f.Value(key);
        return f.Kind switch
        {
            FilterKind.GaussianBlur => new Vector4(V("radius"), 0, 0, 0),
            FilterKind.LensBlur => new Vector4(V("radius"), V("brightnessBoost"), 0, 0),
            FilterKind.MotionBlur => new Vector4(V("angle"), V("distance"), 0, 0),
            FilterKind.Sharpen => new Vector4(V("amount"), 0, 0, 0),
            FilterKind.UnsharpMask => new Vector4(V("amount"), V("radius"), V("threshold"), 0),
            FilterKind.Clarity => new Vector4(V("amount"), 0, 0, 0),
            FilterKind.AddNoise => new Vector4(V("amount"), f.Flag("monochrome") ? 1 : 0, 0, 0),
            FilterKind.Pixelate => new Vector4(V("cell"), 0, 0, 0),
            FilterKind.FindEdges => Vector4.Zero,
            FilterKind.Emboss => new Vector4(V("angle"), V("amount"), 0, 0),
            FilterKind.OilPaint => new Vector4(V("radius"), V("intensity"), 0, 0),
            FilterKind.Halftone => new Vector4(V("cellSize"), V("angle"), f.Flag("monochrome") ? 1 : 0, 0),
            FilterKind.Vignette => new Vector4(V("amount"), V("midpoint"), V("roundness"), V("feather")),
            _ => new Vector4(V("amount"), f.Flag("edgeOnly") ? 1 : 0, 0, 0),
        };
    }

    /// <summary>Clarity's base blur scales with the frame so it reads as local contrast.</summary>
    internal static float ClaritySigma(PixelRect frame) =>
        (float)Math.Min(40, Math.Max(4, Math.Round(Math.Min(frame.Width, frame.Height) * 0.02, MidpointRounding.AwayFromZero))) * 1.4f;

    private static int Reach(FilterSettings f, PixelRect frame) =>
        f.Kind == FilterKind.Clarity ? (int)(ClaritySigma(frame) * 3) + 1 : f.Reach;

    private static bool IsIdentity(FilterSettings f) => f.Kind switch
    {
        FilterKind.GaussianBlur or FilterKind.LensBlur => f.Value("radius") < 0.25,
        FilterKind.MotionBlur => f.Value("distance") < 0.5,
        FilterKind.Sharpen or FilterKind.UnsharpMask or FilterKind.Clarity or FilterKind.AddNoise or FilterKind.Vignette =>
            f.Value("amount") == 0,
        FilterKind.ChromaticAberration => f.Value("amount") < 0.05,
        _ => false,
    };

    /// <summary>Filters a raster layer's tiles inside <paramref name="frame"/> (the document), limited to <paramref name="selection"/>.</summary>
    public TileGrid Apply(FilterSettings f, TileGrid grid, PixelRect frame, Selection? selection)
    {
        if (IsIdentity(f) || grid.TileBounds is not { } content) return grid;
        var area = content.Intersection(frame);
        if (area.IsEmpty) return grid;
        var target = area;
        if (selection is not null)
        {
            if (selection.Bounds(area) is not { } b) return grid;
            target = b;
        }
        int reach = Reach(f, frame);
        var aligned = RegionOps.TileAligned(target);
        int kind = KindIndex(f.Kind);
        var parameters = Parameters(f);
        float auxSigma = f.Kind == FilterKind.Clarity ? ClaritySigma(frame) : (float)f.Value("radius");
        var frameOrigin = new Vector2(frame.X, frame.Y);
        var frameSize = new Vector2(frame.Width, frame.Height);

        const int s = TileGeometry.Size;
        int bandRows = Math.Max(BandRows, s * (int)Math.Ceiling(2.0 * reach / s));
        var result = grid.ToBuilder();
        // One set of buffers, as large as any band's read area, serves every band.
        long capacity = (long)Math.Min(area.Width, aligned.Width + 2 * reach) * Math.Min(area.Height, Math.Min(bandRows, aligned.Height) + 2 * reach);
        var sourceBuffer = new Vector4[capacity];
        var filteredBuffer = new Vector4[capacity];
        var auxBuffer = kind is 4 or 5 ? new Vector4[capacity] : null;
        for (int bandY = aligned.Y; bandY < aligned.MaxY; bandY += bandRows)
        {
            var band = new PixelRect(aligned.X, bandY, aligned.Width, Math.Min(bandRows, aligned.MaxY - bandY));
            var bandTarget = target.Intersection(band);
            if (bandTarget.IsEmpty) continue;  // these tiles keep their pixels
            var srcRect = RegionOps.Expand(bandTarget, reach).Intersection(area);
            var src = new RegionImage(srcRect.Width, srcRect.Height, sourceBuffer);
            grid.ReadRegion(srcRect, src.Floats);
            var filtered = Filter(src, kind, parameters, auxSigma, new Vector2(srcRect.X, srcRect.Y), frameOrigin, frameSize,
                                  new RegionImage(srcRect.Width, srcRect.Height, filteredBuffer),
                                  auxBuffer is null ? null : new RegionImage(srcRect.Width, srcRect.Height, auxBuffer));
            var selectionImage = selection is null ? null : FloatImage.From(selection.Tiles, band, selection.DefaultValue, channel: 3);
            var coords = band.TileCoords;
            var made = new Tile?[coords.Length];
            Parallel.For(0, coords.Length, i =>
                made[i] = Mix(grid[coords[i]], coords[i], filtered, srcRect, bandTarget, selectionImage, band));
            for (int i = 0; i < coords.Length; i++) result[coords[i]] = made[i];
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// The filter pass over an assembled region (<c>filter_apply</c>, or the Gaussian). <paramref name="texOrigin"/> is
    /// the document position of the region's top-left texel; <paramref name="auxSigma"/> the blur unsharp mask and
    /// clarity subtract.
    /// </summary>
    /// <remarks><paramref name="output"/> and <paramref name="auxOutput"/> are optional buffers to write into (else new ones).</remarks>
    internal static RegionImage Filter(RegionImage src, int kind, Vector4 parameters, float auxSigma, Vector2 texOrigin,
                                       Vector2 frameOrigin, Vector2 frameSize, RegionImage? output = null, RegionImage? auxOutput = null)
    {
        var filtered = output ?? new RegionImage(src.Width, src.Height);
        if (kind == 0)
        {
            GaussianBlur.Blur(src, filtered, parameters.X / 2f);
            return filtered;
        }
        RegionImage aux = src;
        if (kind == 4 || kind == 5)
        {
            aux = auxOutput ?? new RegionImage(src.Width, src.Height);
            GaussianBlur.Blur(src, aux, MathF.Max(auxSigma, 0.3f));
        }
        var u = new FilterUniforms
        {
            Kind = kind, TexOrigin = texOrigin, A = parameters, FrameOrigin = frameOrigin,
            FrameSize = new Vector2(MathF.Max(frameSize.X, 1f), MathF.Max(frameSize.Y, 1f)),
        };
        Parallel.For(0, src.Height, y =>
        {
            var row = filtered.Row(y);
            for (int x = 0; x < src.Width; x++) row[x] = FilterShaders.FilterApply(src, aux, x, y, u);
        });
        return filtered;
    }

    /// <summary><c>filter_mix</c> for one tile: the filtered pixels blended into the original inside the target, by the selection.</summary>
    private static Tile? Mix(Tile? original, TileCoord coord, RegionImage filtered, PixelRect filteredRect, PixelRect target,
                             FloatImage? selection, PixelRect band)
    {
        const int s = TileGeometry.Size;
        var rect = coord.Rect;
        var buffer = new Half[TileGeometry.FloatCount];
        var row = new Vector4[s];
        var floats = new float[s * 4];
        for (int y = 0; y < s; y++)
        {
            int gy = rect.Y + y;
            if (original is null) Array.Clear(row);
            else DocumentCompositor.ReadRow(original, y, 0, row, floats);
            if (gy >= target.Y && gy < target.MaxY)
            {
                int x0 = Math.Max(target.X, rect.X), x1 = Math.Min(target.MaxX, rect.MaxX);
                var f = filtered.Row(gy - filteredRect.Y);
                for (int gx = x0; gx < x1; gx++)
                {
                    float sel = selection is null ? 1f : selection.Pixels[(gy - band.Y) * selection.Width + (gx - band.X)];
                    row[gx - rect.X] = FilterShaders.FilterMix(row[gx - rect.X], f[gx - filteredRect.X], sel, selection is not null);
                }
            }
            TensorPrimitives.ConvertToHalf(MemoryMarshal.Cast<Vector4, float>(row.AsSpan()), buffer.AsSpan(y * s * 4, s * 4));
        }
        return Tile.MakeOwned(buffer);
    }

    // MARK: Retouch sources

    /// <summary>
    /// <paramref name="grid"/> moved by <paramref name="offset"/> (integer pixels), covering <paramref name="region"/>:
    /// what the clone stamp paints. Every tile of the region is stored, transparent ones too, as on the Mac (a retouch
    /// stroke falls back to the original where its source has no tile).
    /// </summary>
    public TileGrid Shifted(TileGrid grid, (int X, int Y) offset, PixelRect region)
    {
        var aligned = RegionOps.TileAligned(region);
        var coords = aligned.TileCoords;
        var made = new Tile[coords.Length];
        Parallel.For(0, coords.Length, i =>
        {
            var values = new float[TileGeometry.FloatCount];
            grid.ReadRegion(coords[i].Rect.OffsetBy(-offset.X, -offset.Y), values);
            made[i] = Tile.Make(values) ?? Tile.Solid(Vector4.Zero);
        });
        var b = new TileGrid.Builder();
        for (int i = 0; i < coords.Length; i++) b[coords[i]] = made[i];
        return b.ToImmutable();
    }

    /// <summary>Healing-brush source: the clone with the destination's low frequencies (<paramref name="sigma"/> px).</summary>
    public TileGrid Healed(TileGrid grid, (int X, int Y) offset, PixelRect region, float sigma)
    {
        var aligned = RegionOps.TileAligned(region);
        RegionOps.CheckSize(aligned);
        var destination = RegionImage.From(grid, aligned);
        var clone = RegionImage.From(grid, aligned.OffsetBy(-offset.X, -offset.Y));
        var destBlur = new RegionImage(aligned.Width, aligned.Height);
        var cloneBlur = new RegionImage(aligned.Width, aligned.Height);
        GaussianBlur.Blur(destination, destBlur, MathF.Max(sigma, 1f));
        GaussianBlur.Blur(clone, cloneBlur, MathF.Max(sigma, 1f));
        var output = destination;  // reused: the destination itself is no longer read
        Parallel.For(0, aligned.Height, y =>
        {
            int o = y * aligned.Width;
            for (int x = 0; x < aligned.Width; x++)
                output.Pixels[o + x] = FilterShaders.HealCombine(clone.Pixels[o + x], destBlur.Pixels[o + x], cloneBlur.Pixels[o + x]);
        });
        return RegionOps.Split(output, aligned, keepTransparent: true);
    }
}

/// <summary>
/// One smudge stroke. The layer's pixels in the region live in a working image while the stroke runs; each dab drags
/// colour from behind it.
/// </summary>
public sealed class SmudgeEngine
{
    public TileGrid Original { get; }
    public TileGrid Result { get; private set; }
    private readonly PixelRect region;
    private readonly RegionImage working;
    private readonly RegionImage before;
    private readonly FloatImage? selection;

    public SmudgeEngine(TileGrid tiles, PixelRect region, Selection? selection)
    {
        Original = tiles;
        Result = tiles;
        var aligned = RegionOps.TileAligned(region);
        RegionOps.CheckSize(aligned);
        this.region = aligned;
        working = RegionImage.From(tiles, aligned);
        before = new RegionImage(aligned.Width, aligned.Height);
        this.selection = selection is null ? null : FloatImage.From(selection.Tiles, aligned, selection.DefaultValue, channel: 3);
    }

    /// <summary>Applies dabs (document coordinates) and returns the tiles that changed.</summary>
    public IReadOnlySet<TileCoord> Add(IReadOnlyList<SmudgeDab> dabs)
    {
        var touched = new HashSet<TileCoord>();
        if (dabs.Count == 0) return touched;
        var origin = new Vector2(region.X, region.Y);
        foreach (var d in dabs)
        {
            var dab = d;
            dab.Centre -= origin;
            int r = (int)MathF.Ceiling(dab.Radius) + 2;
            var box = new PixelRect((int)dab.Centre.X - r, (int)dab.Centre.Y - r, 2 * r + 1, 2 * r + 1);
            // Snapshot the area the dab reads (its rect plus the drag distance).
            int reach = (int)MathF.Ceiling(dab.Delta.Length()) + 1;
            var read = RegionOps.Expand(box, reach).Intersection(new PixelRect(0, 0, region.Width, region.Height));
            if (read.IsEmpty) continue;
            for (int y = read.Y; y < read.MaxY; y++)
                working.Pixels.AsSpan(y * working.Width + read.X, read.Width).CopyTo(before.Pixels.AsSpan(y * working.Width + read.X));
            var write = box.Intersection(new PixelRect(0, 0, region.Width, region.Height));
            var dabCopy = dab;
            Parallel.For(write.Y, write.MaxY, y =>
            {
                for (int x = write.X; x < write.MaxX; x++)
                    if (FilterShaders.SmudgeDabFragment(before, selection, dabCopy, x, y, out Vector4 v))
                        working.Pixels[y * working.Width + x] = v;
            });
            foreach (var c in box.OffsetBy(region.X, region.Y).Intersection(region).TileCoords) touched.Add(c);
        }
        // Copy touched tiles out of the working image.
        var coords = touched.ToArray();
        var made = new Tile?[coords.Length];
        Parallel.For(0, coords.Length, i =>
        {
            var rect = coords[i].Rect;
            var buffer = new Half[TileGeometry.FloatCount];
            const int s = TileGeometry.Size;
            for (int y = 0; y < s; y++)
            {
                var src = working.Pixels.AsSpan((rect.Y - region.Y + y) * working.Width + (rect.X - region.X), s);
                TensorPrimitives.ConvertToHalf(MemoryMarshal.Cast<Vector4, float>(src), buffer.AsSpan(y * s * 4, s * 4));
            }
            made[i] = Tile.MakeOwned(buffer);
        });
        var b = Result.ToBuilder();
        for (int i = 0; i < coords.Length; i++) b[coords[i]] = made[i];
        Result = b.ToImmutable();
        return touched;
    }

    /// <summary>Waits for the GPU on the Mac; work here is synchronous.</summary>
    public void Finish() { }
}
