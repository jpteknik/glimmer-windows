// Ported from Packages/GlimmerKit/Sources/GlimmerRender/DocumentCompositor.swift
using System.Collections.Immutable;
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Glimmer.Core;

namespace Glimmer.Render;

/// <summary>
/// Temporary, non-undoable changes shown while a tool is active (e.g. a layer being dragged). Committed edits go into
/// <see cref="DocumentState"/>.
/// </summary>
public sealed record LayerOverrides
{
    public LayerOverrides(ImmutableDictionary<Guid, (int X, int Y)>? offsets = null, ImmutableDictionary<Guid, TileGrid>? pixels = null,
                          ImmutableDictionary<Guid, TileGrid>? masks = null)
    {
        Offsets = offsets ?? ImmutableDictionary<Guid, (int X, int Y)>.Empty;
        Pixels = pixels ?? ImmutableDictionary<Guid, TileGrid>.Empty;
        Masks = masks ?? ImmutableDictionary<Guid, TileGrid>.Empty;
    }

    /// <summary>Integer pixel offsets per layer id (groups move their descendants).</summary>
    public ImmutableDictionary<Guid, (int X, int Y)> Offsets { get; init; }
    /// <summary>Replacement pixels for raster layers (e.g. a brush stroke in progress).</summary>
    public ImmutableDictionary<Guid, TileGrid> Pixels { get; init; }
    /// <summary>Replacement mask tiles (painting a mask).</summary>
    public ImmutableDictionary<Guid, TileGrid> Masks { get; init; }

    public static LayerOverrides None { get; } = new();

    public bool Equals(LayerOverrides? other) =>
        other is not null && Same(Offsets, other.Offsets, (a, b) => a == b)
        && Same(Pixels, other.Pixels, (a, b) => a.ContentSignature == b.ContentSignature)
        && Same(Masks, other.Masks, (a, b) => a.ContentSignature == b.ContentSignature);

    public override int GetHashCode() => HashCode.Combine(Offsets.Count, Pixels.Count, Masks.Count);

    private static bool Same<T>(ImmutableDictionary<Guid, T> a, ImmutableDictionary<Guid, T> b, Func<T, T, bool> equal)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
            if (!b.TryGetValue(key, out var other) || !equal(value, other)) return false;
        return true;
    }
}

/// <summary>A mask ready for compositing (enabled, feather already applied).</summary>
internal sealed class ResolvedMask(TileGrid tiles, float defaultValue, float density)
{
    public readonly TileGrid Tiles = tiles;
    public readonly float DefaultValue = defaultValue;
    public readonly float Density = density;

    /// <summary>The stored tile at <paramref name="coord"/>; null where the default value applies.</summary>
    public Tile? StoredTile(TileCoord coord) => Tiles[coord];
}

internal readonly record struct NodeCommon(BlendMode Mode, float Opacity, bool IsClipped, ResolvedMask? Mask);

/// <summary>The layer tree flattened into what the per-tile renderer needs (Swift's <c>indirect enum RenderNode</c>).</summary>
internal abstract class RenderNode(NodeCommon common)
{
    public readonly NodeCommon Common = common;
}

internal sealed class PixelsNode(NodeCommon common, TileGrid grid, (int X, int Y) offset, EffectGrid[] below, EffectGrid[] above,
                                 ColorOverlayEffect? overlay, bool effectsOnly = false) : RenderNode(common)
{
    public readonly TileGrid Grid = grid;
    public readonly (int X, int Y) Offset = offset;
    public readonly EffectGrid[] Below = below;
    public readonly EffectGrid[] Above = above;
    public readonly ColorOverlayEffect? Overlay = overlay;
    /// <summary>Only the effects of another node (a fill layer's styles): never the base of a clipping group.</summary>
    public readonly bool EffectsOnly = effectsOnly;

    /// <summary>The style grids of a pixels node.</summary>
    public LayerStyleRenderer.Rendered Effects => new(Below, Above);
}

internal sealed class FillNode(NodeCommon common, Fill fill, ColorOverlayEffect? overlay) : RenderNode(common)
{
    public readonly Fill Fill = fill;
    public readonly ColorOverlayEffect? Overlay = overlay;
    public readonly ulong Hash = ContentHash.Of(fill);
}

internal sealed class AdjustmentNode(NodeCommon common, Adjustment adjustment) : RenderNode(common)
{
    public readonly Adjustment Adjustment = adjustment;
    public readonly ulong Hash = ContentHash.Of(adjustment);
}

internal sealed class GroupNode(NodeCommon common, RenderNode[] children) : RenderNode(common)
{
    public readonly RenderNode[] Children = children;
}

/// <summary>
/// One composited tile of the cache: premultiplied linear Float16 RGBA, starting at mip <see cref="Level"/>
/// (0 = the full 256 px; k = 256 &gt;&gt; k px), with the smaller levels made the first time a zoomed-out view needs
/// them. Immutable: a tile that changes is a new <see cref="CompositeTile"/>.
/// </summary>
public sealed class CompositeTile
{
    private readonly Half[] data;
    private Vector4[][]? mips;
    private readonly Lock gate = new();

    internal CompositeTile(int level, Half[] pixels)
    {
        Level = level;
        Width = TileGeometry.Size >> level;
        data = pixels;
    }

    /// <summary>The mip level this tile starts at: 0 = full 256 px; k = 256 &gt;&gt; k px.</summary>
    public int Level { get; }

    /// <summary>Pixels on a side (the tile is square).</summary>
    public int Width { get; }

    /// <summary>Levels this tile holds, its own included (down to 1×1).</summary>
    public int MipLevelCount => DocumentCompositor.MipLevels - Level;

    /// <summary>The <see cref="Width"/> × <see cref="Width"/> RGBA values of the tile's own level.</summary>
    public ReadOnlyMemory<Half> Pixels => data;

    internal Half[] Data => data;

    /// <summary>A pixel of mip <paramref name="mip"/> (0 = the tile's own level), as floats.</summary>
    public Vector4 Read(int x, int y, int mip = 0)
    {
        int w = Math.Max(1, Width >> mip);
        if ((uint)x >= (uint)w || (uint)y >= (uint)w) throw new ArgumentOutOfRangeException(nameof(x));
        if (mip == 0)
        {
            int i = (y * Width + x) * 4;
            return new Vector4((float)data[i], (float)data[i + 1], (float)data[i + 2], (float)data[i + 3]);
        }
        return Mip(mip)[y * w + x];
    }

    /// <summary>Mip <paramref name="mip"/> ≥ 1 (each a 2×2 box of the one above), as floats; made on first use.</summary>
    internal Vector4[] Mip(int mip)
    {
        var levels = Volatile.Read(ref mips);
        if (levels is null)
        {
            lock (gate)
            {
                levels = mips ??= BuildMips();
            }
        }
        return levels[mip];
    }

    private Vector4[][] BuildMips()
    {
        int count = MipLevelCount;
        var levels = new Vector4[count][];
        levels[0] = [];
        if (count < 2) return levels;
        // Level 1 straight from the Float16 rows.
        int w = Width, half = Math.Max(1, w >> 1);
        var first = new Vector4[half * half];
        var row0 = new float[w * 4];
        var row1 = new float[w * 4];
        for (int y = 0; y < half; y++)
        {
            TensorPrimitives.ConvertToSingle(data.AsSpan(2 * y * w * 4, w * 4), row0);
            TensorPrimitives.ConvertToSingle(data.AsSpan(Math.Min(2 * y + 1, w - 1) * w * 4, w * 4), row1);
            var a = MemoryMarshal.Cast<float, Vector4>(row0.AsSpan());
            var b = MemoryMarshal.Cast<float, Vector4>(row1.AsSpan());
            for (int x = 0; x < half; x++)
            {
                int x1 = Math.Min(2 * x + 1, w - 1);
                first[y * half + x] = (a[2 * x] + a[x1] + b[2 * x] + b[x1]) * 0.25f;
            }
        }
        levels[1] = first;
        for (int i = 2; i < count; i++)
        {
            int pw = Math.Max(1, w >> (i - 1)), nw = Math.Max(1, w >> i);
            levels[i] = MipChain.Reduce(levels[i - 1], pw, pw, nw, nw);
        }
        return levels;
    }

    /// <summary>The same tile starting <paramref name="levels"/> levels further down.</summary>
    internal CompositeTile Reduced(int levels)
    {
        if (levels <= 0) return this;
        var source = Mip(levels);
        var half = new Half[source.Length * 4];
        TensorPrimitives.ConvertToHalf(MemoryMarshal.Cast<Vector4, float>(source.AsSpan()), half);
        return new CompositeTile(Level + levels, half);
    }
}

/// <summary>
/// Composites a document into a cache of 256×256 tiles in document space, re-rendering only tiles whose inputs
/// changed.
/// </summary>
/// <remarks>
/// Each cached tile is Float16, premultiplied linear, with mip levels so the canvas can minify without aliasing. Not
/// thread-safe: one caller at a time (the Mac uses it from its render actor); the work inside each call runs on all
/// cores.
/// </remarks>
public sealed class DocumentCompositor
{
    public const int MipLevels = 9;  // 256 → 1

    private struct Entry
    {
        public ulong Signature;
        /// <summary>Null when nothing visible contributes (fully transparent).</summary>
        public CompositeTile? Texture;
        /// <summary>
        /// Mip level the texture starts at: 0 = full 256 px; k = 256 &gt;&gt; k px. Zoomed-out views keep only the level
        /// they show, which saves memory.
        /// </summary>
        public int Level;
    }

    private readonly Dictionary<TileCoord, Entry> cache = [];
    private readonly LayerStyleRenderer styles = new();
    private readonly SmartObjectRenderer smartRenderer = new();
    private readonly MaskFeatherRenderer featherRenderer = new();
    private readonly Lazy<FilterEngine> filterEngine = new(() => new FilterEngine());

    private readonly Dictionary<Guid, (EffectsKey Key, LayerStyleRenderer.Rendered Value)> effectsCache = [];
    private readonly Dictionary<Guid, (SmartKey Key, TileGrid Value)> smartCache = [];
    private readonly Dictionary<Guid, (FeatherKey Key, TileGrid Value)> featherCache = [];
    private readonly Dictionary<Guid, (TextLayer Key, TileGrid Value)> textCache = [];

    private readonly record struct EffectsKey(long Grid, LayerEffects Effects, PixelRect Limit, long Mask, float MaskDefault, bool HasMask);

    private readonly record struct FeatherKey(long Tiles, double Feather, float DefaultValue);

    private sealed record SmartKey(long Source, AffineTransform Transform, PixelRect Limit, ImmutableArray<SmartFilter> Filters, PixelRect Bounds)
    {
        public bool Equals(SmartKey? other) =>
            other is not null && Source == other.Source && Transform.Equals(other.Transform) && Limit == other.Limit
            && Bounds == other.Bounds && Filters.AsSpan().SequenceEqual(other.Filters.AsSpan());

        public override int GetHashCode() => HashCode.Combine(Source, Transform, Limit, Bounds, Filters.Length);
    }

    public DocumentCompositor() { }

    // MARK: Resolving the tree

    /// <summary>
    /// Renders anything that needs neighbouring pixels (smart objects, styles, feathered masks), using caches, and
    /// returns the render tree.
    /// </summary>
    internal RenderNode[] Resolve(DocumentState state, LayerOverrides overrides)
    {
        var limit = RegionOps.Expand(state.Bounds, 512);
        var seen = new HashSet<Guid>();

        ResolvedMask? MaskFor(Layer layer)
        {
            if (layer.Mask is not { IsEnabled: true } m || !(m.Density > 0)) return null;
            var tiles = overrides.Masks.TryGetValue(layer.Id, out var painted) ? painted : m.Tiles;
            if (m.Feather > 0.25)
            {
                var key = new FeatherKey(tiles.ContentSignature, m.Feather, m.DefaultValue);
                if (featherCache.TryGetValue(layer.Id, out var hit) && hit.Key == key)
                {
                    tiles = hit.Value;
                }
                else
                {
                    try
                    {
                        var feathered = featherRenderer.Feather(m with { Tiles = tiles }, limit);
                        featherCache[layer.Id] = (key, feathered);
                        tiles = feathered;
                    }
                    catch (RenderError) { }
                }
            }
            return new ResolvedMask(tiles, m.DefaultValue, (float)m.Density);
        }

        RenderNode[] ResolveList(ImmutableArray<Layer> layers, (int X, int Y) offset)
        {
            var nodes = new List<RenderNode>();
            bool baseHidden = false;
            foreach (var layer in layers)
            {
                if (!layer.IsClipped) baseHidden = !layer.IsVisible;
                if (!layer.IsVisible || (layer.IsClipped && baseHidden)) continue;
                seen.Add(layer.Id);
                (int X, int Y) own = overrides.Offsets.TryGetValue(layer.Id, out var d) ? d : (0, 0);
                (int X, int Y) o = (offset.X + own.X, offset.Y + own.Y);
                var common = new NodeCommon(layer.BlendMode, (float)layer.Opacity, layer.IsClipped, MaskFor(layer));
                var overlay = layer.Effects.ColorOverlay is { IsEnabled: true } co ? co : null;
                switch (layer.Content)
                {
                    case RasterContent raster:
                    {
                        var grid = overrides.Pixels.TryGetValue(layer.Id, out var p) ? p : raster.Tiles;
                        nodes.Add(PixelsNodeFor(layer, grid, common, o, overlay, limit));
                        break;
                    }
                    case SmartContent sc:
                    {
                        var smart = sc.Smart;
                        var key = new SmartKey(smart.Source.ContentSignature, smart.Transform, limit, smart.Filters, state.Bounds);
                        TileGrid grid;
                        if (smartCache.TryGetValue(layer.Id, out var hit) && hit.Key.Equals(key))
                        {
                            grid = hit.Value;
                        }
                        else
                        {
                            TileGrid rendered;
                            try { rendered = smartRenderer.Render(smart, limit); }
                            catch (RenderError) { rendered = TileGrid.Empty; }
                            // Smart filters run in document space on the transformed pixels.
                            foreach (var f in smart.Filters)
                            {
                                if (!f.IsEnabled) continue;
                                try { rendered = filterEngine.Value.Apply(f.Settings, rendered, state.Bounds, null); }
                                catch (RenderError) { }
                            }
                            grid = rendered;
                            smartCache[layer.Id] = (key, grid);
                        }
                        nodes.Add(PixelsNodeFor(layer, grid, common, o, overlay, limit));
                        break;
                    }
                    case TextContent tc:
                    {
                        var text = tc.Text;
                        TileGrid grid;
                        if (overrides.Pixels.TryGetValue(layer.Id, out var p)) grid = p;
                        else if (textCache.TryGetValue(layer.Id, out var hit) && hit.Key.Equals(text)) grid = hit.Value;
                        else
                        {
                            grid = Rasterize(text);
                            textCache[layer.Id] = (text, grid);
                        }
                        nodes.Add(PixelsNodeFor(layer, grid, common, o, overlay, limit));
                        break;
                    }
                    case FillContent fc:
                    {
                        // A masked fill (a shape) has an outline, so layer styles apply: they are rendered from the
                        // mask's shape and drawn around the fill.
                        var styleGrids = LayerStyleRenderer.Rendered.Empty;
                        if (layer.Effects.HasSpatialEffects && common.Mask is { } m && m.DefaultValue < 0.5f && m.Tiles.TileBounds is { } area)
                        {
                            var opaque = TileGrid.Solid(new Vector4(1, 1, 1, 1), area);
                            styleGrids = ((PixelsNode)PixelsNodeFor(layer, opaque, common, o, null, limit)).Effects;
                        }
                        if (styleGrids.Below.Length > 0)
                            nodes.Add(new PixelsNode(common, TileGrid.Empty, o, styleGrids.Below, [], null, effectsOnly: true));
                        nodes.Add(new FillNode(common, fc.Fill, overlay));
                        if (styleGrids.Above.Length > 0)
                            nodes.Add(new PixelsNode(common, TileGrid.Empty, o, [], styleGrids.Above, null, effectsOnly: true));
                        break;
                    }
                    case AdjustmentContent ac:
                        nodes.Add(new AdjustmentNode(common, ac.Adjustment));
                        break;
                    case GroupContent gc:
                        nodes.Add(new GroupNode(common, ResolveList(gc.Group.Children, o)));
                        break;
                }
            }
            return [.. nodes];
        }

        var result = ResolveList(state.Layers, (0, 0));
        // Forget caches for layers that are gone or hidden.
        Prune(effectsCache, seen);
        Prune(smartCache, seen);
        Prune(featherCache, seen);
        Prune(textCache, seen);
        return result;
    }

    private static void Prune<T>(Dictionary<Guid, T> dictionary, HashSet<Guid> keep)
    {
        if (dictionary.Count == 0) return;
        foreach (var id in dictionary.Keys.Where(id => !keep.Contains(id)).ToList()) dictionary.Remove(id);
    }

    private RenderNode PixelsNodeFor(Layer layer, TileGrid grid, NodeCommon common, (int X, int Y) offset,
                                     ColorOverlayEffect? overlay, PixelRect limit)
    {
        var rendered = LayerStyleRenderer.Rendered.Empty;
        if (layer.Effects.HasSpatialEffects)
        {
            var m = common.Mask;
            var key = new EffectsKey(grid.ContentSignature, layer.Effects, limit, m?.Tiles.ContentSignature ?? 0, m?.DefaultValue ?? 0, m is not null);
            if (effectsCache.TryGetValue(layer.Id, out var hit) && hit.Key == key)
            {
                rendered = hit.Value;
            }
            else
            {
                try { rendered = styles.Render(grid, common.Mask, layer.Effects, limit); }
                catch (RenderError) { rendered = LayerStyleRenderer.Rendered.Empty; }
                effectsCache[layer.Id] = (key, rendered);
            }
        }
        return new PixelsNode(common, grid, offset, rendered.Below, rendered.Above, overlay);
    }

    // MARK: Signatures

    /// <summary>Layer-space tiles feeding output tile <paramref name="coord"/> for content shifted by <paramref name="offset"/>.</summary>
    private static TileCoord[] SourceCoords(TileCoord coord, (int X, int Y) offset) =>
        offset == (0, 0) ? [coord] : coord.Rect.OffsetBy(-offset.X, -offset.Y).TileCoords;

    private static ulong CombineCommon(in NodeCommon c, ulong h)
    {
        h = ContentHash.Mix(h, (ulong)c.Mode);
        h = ContentHash.Mix(h, c.Opacity);
        h = ContentHash.Mix(h, c.IsClipped ? 1UL : 0UL);
        if (c.Mask is { } m) return ContentHash.Mix(ContentHash.Mix(h, m.Density), m.DefaultValue);
        return ContentHash.Mix(h, ulong.MaxValue);
    }

    /// <summary>The identity of the mask tile at <paramref name="coord"/> (a missing tile is a solid of the default).</summary>
    private static ulong CombineMask(ResolvedMask? mask, TileCoord coord, ulong h)
    {
        if (mask is null) return h;
        if (mask.Tiles[coord] is { } tile) return tile.CombineIdentity(h);
        uint bits = BitConverter.SingleToUInt32Bits(mask.DefaultValue);
        h = ContentHash.Mix(h, 0UL);
        for (int i = 0; i < 4; i++) h = ContentHash.Mix(h, (ulong)bits);
        return h;
    }

    /// <summary>Hashes everything that affects <paramref name="coord"/>. Returns whether anything could be visible there.</summary>
    private static bool Signature(RenderNode[] nodes, TileCoord coord, ref ulong h)
    {
        bool visible = false;
        foreach (var node in nodes)
        {
            h = CombineCommon(node.Common, h);
            switch (node)
            {
                case PixelsNode p:
                    h = ContentHash.Mix(h, 0UL);
                    h = ContentHash.Mix(ContentHash.Mix(h, (ulong)(uint)p.Offset.X), (ulong)(uint)p.Offset.Y);
                    foreach (var lc in SourceCoords(coord, p.Offset))
                    {
                        h = ContentHash.Mix(ContentHash.Mix(h, (ulong)(uint)lc.X), (ulong)(uint)lc.Y);
                        if (p.Grid[lc] is { } t)
                        {
                            h = t.CombineIdentity(h);
                            visible = true;
                        }
                        h = CombineMask(p.Common.Mask, lc, h);
                        foreach (var e in p.Below.Concat(p.Above))
                        {
                            h = ContentHash.Mix(h, (ulong)e.Mode);
                            if (e.Grid[lc] is { } et)
                            {
                                h = et.CombineIdentity(h);
                                visible = true;
                            }
                        }
                    }
                    h = ContentHash.Mix(h, ContentHash.Of(p.Overlay));
                    break;
                case FillNode f:
                    h = ContentHash.Mix(h, 1UL);
                    h = ContentHash.Mix(h, f.Hash);
                    h = ContentHash.Mix(h, ContentHash.Of(f.Overlay));
                    h = CombineMask(node.Common.Mask, coord, h);
                    visible = true;
                    break;
                case AdjustmentNode a:
                    h = ContentHash.Mix(h, 2UL);
                    h = ContentHash.Mix(h, a.Hash);
                    h = CombineMask(node.Common.Mask, coord, h);
                    break;
                case GroupNode g:
                    h = ContentHash.Mix(h, 3UL);
                    h = CombineMask(node.Common.Mask, coord, h);
                    h = ContentHash.Mix(h, (ulong)g.Children.Length);
                    if (Signature(g.Children, coord, ref h)) visible = true;
                    break;
            }
        }
        return visible;
    }

    // MARK: Updating

    /// <summary>
    /// Re-composites up to <paramref name="budget"/> stale tiles from <paramref name="coords"/> (in priority order).
    /// Returns true when stale tiles remain.
    /// </summary>
    public bool Update(DocumentState state, LayerOverrides? overrides, IReadOnlyList<TileCoord> coords, int budget = int.MaxValue,
                       int detailLevel = 0)
    {
        overrides ??= LayerOverrides.None;
        int level = Math.Max(0, Math.Min(detailLevel, 5));
        var nodes = Resolve(state, overrides);
        bool gamma = state.BlendSpace == BlendSpace.Gamma;

        var unique = new List<TileCoord>(coords.Count);
        var seen = new HashSet<TileCoord>();
        foreach (var c in coords) if (seen.Add(c)) unique.Add(c);
        var signatures = new ulong[unique.Count];
        var visible = new bool[unique.Count];
        ulong seed = ContentHash.Mix(0x6A09E667F3BCC909UL, gamma ? 1UL : 0UL);
        Parallel.For(0, unique.Count, i =>
        {
            ulong h = seed;
            visible[i] = Signature(nodes, unique[i], ref h);
            signatures[i] = h;
        });

        var toRender = new List<int>();
        var reductions = new List<TileCoord>();
        int done = 0;
        bool remaining = false;
        for (int i = 0; i < unique.Count; i++)
        {
            var coord = unique[i];
            if (cache.TryGetValue(coord, out var entry) && entry.Signature == signatures[i])
            {
                // Up to date: keep it if it has enough detail, shrink it if it has more than needed.
                if (entry.Level <= level)
                {
                    if (entry.Level < level && entry.Texture is not null) reductions.Add(coord);
                    continue;
                }
            }
            if (done >= budget)
            {
                remaining = true;
                break;
            }
            done++;
            if (!visible[i])
            {
                cache[coord] = new Entry { Signature = signatures[i] };
                continue;
            }
            toRender.Add(i);
        }

        var results = new CompositeTile[toRender.Count];
        Parallel.For(0, toRender.Count, () => new TileScratch(), (j, _, scratch) =>
        {
            results[j] = RenderTile(nodes, unique[toRender[j]], gamma, scratch);
            return scratch;
        }, _ => { });
        for (int j = 0; j < toRender.Count; j++)
        {
            int i = toRender[j];
            cache[unique[i]] = new Entry { Signature = signatures[i], Texture = results[j] };
            if (level > 0) reductions.Add(unique[i]);
        }

        // Keep only the levels a zoomed-out view samples.
        if (reductions.Count > 0)
        {
            var reduced = new CompositeTile?[reductions.Count];
            Parallel.For(0, reductions.Count, k =>
            {
                var e = cache[reductions[k]];
                int fromLevel = level - e.Level;
                reduced[k] = fromLevel > 0 && e.Texture is { } t ? t.Reduced(fromLevel) : null;
            });
            for (int k = 0; k < reductions.Count; k++)
            {
                if (reduced[k] is not { } r) continue;
                var e = cache[reductions[k]];
                e.Texture = r;
                e.Level = level;
                cache[reductions[k]] = e;
            }
        }
        return remaining;
    }

    /// <summary>Detail level for a view showing <paramref name="zoom"/> device pixels per document pixel.</summary>
    public static int DetailLevel(double zoom)
    {
        if (!(zoom > 0) || !(zoom < 0.5)) return 0;
        return Math.Min(5, (int)Math.Floor(Math.Log2(1 / zoom)));
    }

    // MARK: Textures

    /// <summary>The composited tile at <paramref name="coord"/>, if it has been rendered and isn't empty (Swift's <c>texture(at:)</c>).</summary>
    public CompositeTile? Texture(TileCoord coord) => cache.TryGetValue(coord, out var e) ? e.Texture : null;

    /// <summary>Drops cached tiles outside <paramref name="bounds"/> (e.g. after the document shrinks).</summary>
    public void Purge(PixelRect bounds)
    {
        var keep = new HashSet<TileCoord>(bounds.TileCoords);
        foreach (var coord in cache.Keys.Where(c => !keep.Contains(c)).ToList()) cache.Remove(coord);
    }

    // MARK: Rendering one tile

    private sealed class Piece
    {
        public Tile? Source;
        /// <summary>The layer-space mask tile for this source tile; null = the default value.</summary>
        public Tile? Mask;
        public Tile?[] Below = [];
        public Tile?[] Above = [];
        public int DstX0, DstX1, SrcX0, DstY0, DstY1, SrcY0;
    }

    private sealed class NodePlan
    {
        public required RenderNode Node;
        public Piece[] Pieces = [];
        /// <summary>Pixels nodes: the layer's own tile at the output coordinate (the clip base's alpha).</summary>
        public Tile? CoordTile;
        /// <summary>The node's mask tile at the output coordinate; null = the default value (or no mask).</summary>
        public Tile? CoordMask;
        public NodePlan[] Children = [];
        public LayerUniforms Uniforms;
        public AdjustUniforms Adjust;
        public Vector4[] Lut = [];
        /// <summary>A clipped layer follows, so this node's alpha and mask become the clipping base.</summary>
        public bool NeedsBase;
    }

    private sealed class DepthBuffers
    {
        public readonly Vector4[] Group = new Vector4[TileGeometry.Size];
        public readonly Vector4[] Backup = new Vector4[TileGeometry.Size];
        public readonly float[] BaseAlpha = new float[TileGeometry.Size];
        public readonly float[] BaseMask = new float[TileGeometry.Size];
        public bool HasBase;
        public float BaseDensity;
    }

    private sealed class TileScratch
    {
        public readonly Vector4[] Row = new Vector4[TileGeometry.Size];
        public readonly Vector4[] Source = new Vector4[TileGeometry.Size];
        public readonly float[] Mask = new float[TileGeometry.Size];
        public readonly float[] K = new float[TileGeometry.Size];
        public readonly float[] Floats = new float[TileGeometry.Size * 4];
        private readonly List<DepthBuffers> depths = [];

        public DepthBuffers Depth(int d)
        {
            while (depths.Count <= d) depths.Add(new DepthBuffers());
            return depths[d];
        }
    }

    private static NodePlan[] Plan(RenderNode[] nodes, TileCoord coord, bool gamma)
    {
        var plans = new NodePlan[nodes.Length];
        var origin = new Vector2(coord.Rect.X, coord.Rect.Y);
        for (int n = 0; n < nodes.Length; n++)
        {
            var node = nodes[n];
            var c = node.Common;
            var plan = new NodePlan { Node = node, CoordMask = c.Mask?.StoredTile(coord) };
            var u = new LayerUniforms
            {
                Mode = c.Mode.ShaderIndex(), Opacity = c.Opacity, GammaSpace = gamma ? 1 : 0,
                MaskDensity = c.Mask?.Density ?? 0, TileOrigin = origin,
            };
            switch (node)
            {
                case PixelsNode p:
                {
                    if (p.Overlay is { } overlay)
                    {
                        u.Overlay = new Vector4(overlay.Color.Linear, (float)overlay.Opacity);
                        u.OverlayMode = overlay.BlendMode.ShaderIndex();
                    }
                    if (p.Offset == (0, 0)) plan.CoordTile = p.Grid[coord];
                    var pieces = new List<Piece>(4);
                    var output = coord.Rect;
                    foreach (var lc in SourceCoords(coord, p.Offset))
                    {
                        var placed = lc.Rect.OffsetBy(p.Offset.X, p.Offset.Y).Intersection(output);
                        if (placed.IsEmpty) continue;
                        var piece = new Piece
                        {
                            Source = p.Grid[lc],
                            Mask = c.Mask?.StoredTile(lc),
                            Below = [.. p.Below.Select(e => e.Grid[lc])],
                            Above = [.. p.Above.Select(e => e.Grid[lc])],
                            DstX0 = placed.X - output.X, DstX1 = placed.MaxX - output.X,
                            DstY0 = placed.Y - output.Y, DstY1 = placed.MaxY - output.Y,
                            SrcX0 = placed.X - p.Offset.X - lc.Rect.X, SrcY0 = placed.Y - p.Offset.Y - lc.Rect.Y,
                        };
                        if (piece.Source is null && piece.Below.All(t => t is null) && piece.Above.All(t => t is null)) continue;
                        pieces.Add(piece);
                    }
                    plan.Pieces = [.. pieces];
                    break;
                }
                case FillNode f:
                {
                    if (f.Overlay is { } overlay)
                    {
                        // A Color Overlay recolours fills and shapes too.
                        u.Overlay = new Vector4(overlay.Color.Linear, (float)overlay.Opacity);
                        u.OverlayMode = overlay.BlendMode.ShaderIndex();
                    }
                    switch (f.Fill)
                    {
                        case Fill.Solid s:
                            u.PatternForeground = new Vector4(s.Color.Linear, 1);  // the solid source colour
                            break;
                        case Fill.Gradient g:
                            u.SourceKind = g.Value.Style == GradientStyle.Linear ? 1 : 2;
                            u.GradientStart = new Vector2((float)g.Value.Start.X, (float)g.Value.Start.Y);
                            u.GradientEnd = new Vector2((float)g.Value.End.X, (float)g.Value.End.Y);
                            u.GradientReverse = g.Value.Reverse ? 1 : 0;
                            plan.Lut = TilePipelines.GradientLut(g.Value.Stops);
                            break;
                        case Fill.Pattern pf:
                            u.SourceKind = 3;
                            u.PatternKind = pf.Value.Kind.ShaderIndex();
                            u.PatternScale = (float)pf.Value.Scale;
                            u.PatternForeground = new Vector4(pf.Value.Foreground.Linear, 1);
                            u.PatternBackground = new Vector4(pf.Value.Background.Linear, 1);
                            break;
                    }
                    break;
                }
                case AdjustmentNode a:
                {
                    var au = new AdjustUniforms
                    {
                        Kind = a.Adjustment.Kind.ShaderIndex(), Mode = c.Mode.ShaderIndex(), Opacity = c.Opacity,
                        GammaSpace = gamma ? 1 : 0, MaskDensity = c.Mask?.Density ?? 0,
                    };
                    au.SetParameters(a.Adjustment.ShaderParameters);
                    plan.Adjust = au;
                    plan.Lut = a.Adjustment.Kind.UsesLUT() ? TilePipelines.Lut(a.Adjustment) : [Vector4.One];
                    break;
                }
                case GroupNode g:
                    plan.Children = Plan(g.Children, coord, gamma);
                    break;
            }
            plan.Uniforms = u;
            plans[n] = plan;
        }
        // Only a node that a clipped layer will clip to needs to record its alpha and mask. Unclipped effects-only
        // nodes neither set nor use the base, so they are looked through.
        bool followingIsClipped = false;
        for (int n = plans.Length - 1; n >= 0; n--)
        {
            var node = plans[n].Node;
            bool clipped = node.Common.IsClipped;
            if (!clipped && node is PixelsNode { EffectsOnly: true }) continue;
            plans[n].NeedsBase = !clipped && followingIsClipped;
            followingIsClipped = clipped;
        }
        return plans;
    }

    private static CompositeTile RenderTile(RenderNode[] nodes, TileCoord coord, bool gamma, TileScratch scratch)
    {
        var plans = Plan(nodes, coord, gamma);
        const int s = TileGeometry.Size;
        var output = GC.AllocateUninitializedArray<Half>(TileGeometry.FloatCount);  // every row is written below
        var row = scratch.Row;
        for (int y = 0; y < s; y++)
        {
            Array.Clear(row);
            RenderRow(plans, y, row, 0, gamma, scratch);
            TensorPrimitives.ConvertToHalf(MemoryMarshal.Cast<Vector4, float>(row.AsSpan()), output.AsSpan(y * s * 4, s * 4));
        }
        return new CompositeTile(0, output);
    }

    /// <summary>One row of the output tile through a node list (a group's children recurse one depth down).</summary>
    /// <remarks>Compiled fully optimized from the first call: the first composite of a document is the one users wait for.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void RenderRow(NodePlan[] plans, int y, Vector4[] target, int depth, bool gamma, TileScratch s)
    {
        var db = s.Depth(depth);
        db.HasBase = false;
        const int size = TileGeometry.Size;
        foreach (var plan in plans)
        {
            var c = plan.Node.Common;
            bool clipped = c.IsClipped;
            if (clipped && !db.HasBase) continue;  // the base is hidden: so is the chain
            ref var u = ref plan.Uniforms;
            float clipDensity = clipped ? db.BaseDensity : 0;

            switch (plan.Node)
            {
                case PixelsNode p:
                {
                    foreach (var piece in plan.Pieces)
                    {
                        if (y < piece.DstY0 || y >= piece.DstY1) continue;
                        int sy = piece.SrcY0 + (y - piece.DstY0);
                        int x0 = piece.DstX0, n = piece.DstX1 - piece.DstX0, sx0 = piece.SrcX0;
                        var dst = target.AsSpan(x0, n);
                        var src = s.Source.AsSpan(0, n);
                        var k = s.K.AsSpan(0, n);
                        // Effects are already shaped by the mask: no mask density for them.
                        for (int e = 0; e < piece.Below.Length; e++)
                        {
                            if (piece.Below[e] is not { } tile) continue;
                            ReadRow(tile, sy, sx0, src, s.Floats);
                            Coverage(k, u.Opacity, default, 0, 0, clipped, db, x0, clipDensity);
                            BlendRow(dst, src, k, p.Below[e].Mode.ShaderIndex(), gamma, Vector4.Zero, 0);
                        }
                        if (piece.Source is { } source)
                        {
                            ReadRow(source, sy, sx0, src, s.Floats);
                            var m = s.Mask.AsSpan(0, n);
                            ReadMaskRow(c.Mask, piece.Mask, sy, sx0, m, s.Floats);
                            Coverage(k, u.Opacity, m, u.MaskDensity, c.Mask is null ? 0 : 1, clipped, db, x0, clipDensity);
                            BlendRow(dst, src, k, u.Mode, gamma, u.Overlay, u.OverlayMode);
                        }
                        for (int e = 0; e < piece.Above.Length; e++)
                        {
                            if (piece.Above[e] is not { } tile) continue;
                            ReadRow(tile, sy, sx0, src, s.Floats);
                            Coverage(k, u.Opacity, default, 0, 0, clipped, db, x0, clipDensity);
                            BlendRow(dst, src, k, p.Above[e].Mode.ShaderIndex(), gamma, Vector4.Zero, 0);
                        }
                    }
                    if (plan.NeedsBase)
                    {
                        if (p.Offset == (0, 0))
                        {
                            if (plan.CoordTile is { } t) ReadAlphaRow(t, y, 0, db.BaseAlpha, s.Floats);
                            else Array.Clear(db.BaseAlpha);
                        }
                        else
                        {
                            Array.Fill(db.BaseAlpha, 1f);
                        }
                        SetBaseMask(db, c, plan.CoordMask, y, s.Floats);
                    }
                    break;
                }
                case FillNode:
                {
                    var src = s.Source.AsSpan(0, size);
                    if (u.SourceKind == 0)
                    {
                        src.Fill(u.PatternForeground);
                    }
                    else
                    {
                        float py = u.TileOrigin.Y + y + 0.5f;
                        for (int x = 0; x < size; x++)
                            src[x] = CompositeShaders.ProceduralSource(new Vector2(u.TileOrigin.X + x + 0.5f, py), u, plan.Lut);
                    }
                    var m = s.Mask.AsSpan(0, size);
                    ReadMaskRow(c.Mask, plan.CoordMask, y, 0, m, s.Floats);
                    var k = s.K.AsSpan(0, size);
                    Coverage(k, u.Opacity, m, u.MaskDensity, c.Mask is null ? 0 : 1, clipped, db, 0, clipDensity);
                    BlendRow(target, src, k, u.Mode, gamma, u.Overlay, u.OverlayMode);
                    if (plan.NeedsBase) SetBase(db, c, plan.CoordMask, y, s.Floats);
                    break;
                }
                case AdjustmentNode:
                {
                    var m = s.Mask.AsSpan(0, size);
                    ReadMaskRow(c.Mask, plan.CoordMask, y, 0, m, s.Floats);
                    var k = s.K.AsSpan(0, size);
                    ref var au = ref plan.Adjust;
                    Coverage(k, au.Opacity, m, au.MaskDensity, c.Mask is null ? 0 : 1, clipped, db, 0, clipDensity);
                    var lut = plan.Lut;
                    for (int x = 0; x < size; x++)
                    {
                        if (k[x] <= 0f && au.Mode == 0) continue;
                        target[x] = CompositeShaders.AdjustFragment(target[x], au, lut, k[x]);
                    }
                    if (plan.NeedsBase) SetBase(db, c, plan.CoordMask, y, s.Floats);
                    break;
                }
                case GroupNode:
                {
                    if (c.Mode == BlendMode.PassThrough)
                    {
                        // Children blend straight into this target; opacity and mask then pull the result back toward
                        // the saved backdrop.
                        bool needsMix = c.Opacity < 1 || c.Mask is not null || clipped;
                        if (needsMix) target.AsSpan().CopyTo(db.Backup);
                        RenderRow(plan.Children, y, target, depth + 1, gamma, s);
                        if (needsMix)
                        {
                            var m = s.Mask.AsSpan(0, size);
                            ReadMaskRow(c.Mask, plan.CoordMask, y, 0, m, s.Floats);
                            var k = s.K.AsSpan(0, size);
                            Coverage(k, u.Opacity, m, u.MaskDensity, c.Mask is null ? 0 : 1, clipped, db, 0, clipDensity);
                            for (int x = 0; x < size; x++) target[x] = Vector4.Lerp(db.Backup[x], target[x], k[x]);
                        }
                        if (plan.NeedsBase) SetBase(db, c, plan.CoordMask, y, s.Floats);
                    }
                    else
                    {
                        var temp = db.Group;
                        Array.Clear(temp);
                        RenderRow(plan.Children, y, temp, depth + 1, gamma, s);
                        var m = s.Mask.AsSpan(0, size);
                        ReadMaskRow(c.Mask, plan.CoordMask, y, 0, m, s.Floats);
                        var k = s.K.AsSpan(0, size);
                        Coverage(k, u.Opacity, m, u.MaskDensity, c.Mask is null ? 0 : 1, clipped, db, 0, clipDensity);
                        BlendRow(target, temp, k, u.Mode, gamma, Vector4.Zero, 0);
                        if (plan.NeedsBase)
                        {
                            for (int x = 0; x < size; x++) db.BaseAlpha[x] = temp[x].W;
                            SetBaseMask(db, c, plan.CoordMask, y, s.Floats);
                        }
                    }
                    break;
                }
            }
        }
    }

    /// <summary>A fill, adjustment or pass-through group as the clipping base: full alpha, its mask.</summary>
    private static void SetBase(DepthBuffers db, in NodeCommon c, Tile? coordMask, int y, float[] floats)
    {
        Array.Fill(db.BaseAlpha, 1f);
        SetBaseMask(db, c, coordMask, y, floats);
    }

    private static void SetBaseMask(DepthBuffers db, in NodeCommon c, Tile? coordMask, int y, float[] floats)
    {
        ReadMaskRow(c.Mask, coordMask, y, 0, db.BaseMask, floats);
        db.BaseDensity = c.Mask?.Density ?? 0;
        db.HasBase = true;
    }

    /// <summary>k = opacity × maskValue(mask, density) × clip alpha × maskValue(clip mask, clip density).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Coverage(Span<float> k, float opacity, ReadOnlySpan<float> mask, float density, int hasMask, bool clipped,
                                 DepthBuffers db, int x0, float clipDensity)
    {
        if (hasMask == 0 || density == 0) k.Fill(opacity);
        else
            for (int x = 0; x < k.Length; x++) k[x] = opacity * CompositeShaders.MaskValue(mask[x], density);
        if (!clipped) return;
        var alpha = db.BaseAlpha.AsSpan(x0, k.Length);
        var clipMask = db.BaseMask.AsSpan(x0, k.Length);
        for (int x = 0; x < k.Length; x++) k[x] *= alpha[x] * CompositeShaders.MaskValue(clipMask[x], clipDensity);
    }

    /// <summary>Composites <paramref name="src"/> × k onto <paramref name="dst"/> (<c>composite_layer_fragment</c> for a row).</summary>
    private static void BlendRow(Span<Vector4> dst, ReadOnlySpan<Vector4> src, ReadOnlySpan<float> k, int mode, bool gamma,
                                 Vector4 overlay, int overlayMode)
    {
        if (!(overlay.W > 0f))
        {
            BlendRows.Blend(dst, src, k, mode, gamma);
            return;
        }
        for (int x = 0; x < dst.Length; x++)
        {
            Vector4 c = CompositeShaders.ApplyOverlay(src[x], overlay, overlayMode);
            dst[x] = CompositeShaders.BlendOver(dst[x], c * k[x], mode, gamma);
        }
    }

    /// <summary>Row <paramref name="y"/> of a tile from column <paramref name="x0"/>, as floats.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ReadRow(Tile tile, int y, int x0, Span<Vector4> dst, float[] floats)
    {
        if (tile.IsSolid)
        {
            dst.Fill(tile.SolidColor);
            return;
        }
        int o = (y * TileGeometry.Size + x0) * 4;
        TensorPrimitives.ConvertToSingle(tile.PixelSpan.Slice(o, dst.Length * 4), MemoryMarshal.Cast<Vector4, float>(dst));
    }

    /// <summary>The alpha channel of a tile row (mask and selection tiles hold their value there).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ReadAlphaRow(Tile tile, int y, int x0, Span<float> dst, float[] floats)
    {
        if (tile.IsSolid)
        {
            dst.Fill(tile.SolidColor.W);
            return;
        }
        int n = dst.Length;
        var f = floats.AsSpan(0, n * 4);
        TensorPrimitives.ConvertToSingle(tile.PixelSpan.Slice((y * TileGeometry.Size + x0) * 4, n * 4), f);
        for (int x = 0; x < n; x++) dst[x] = f[x * 4 + 3];
    }

    /// <summary>A mask row: the stored tile's value, the default where none is stored, 1 without a mask.</summary>
    private static void ReadMaskRow(ResolvedMask? mask, Tile? tile, int y, int x0, Span<float> dst, float[] floats)
    {
        if (mask is null) dst.Fill(1f);
        else if (tile is null) dst.Fill(mask.DefaultValue);
        else ReadAlphaRow(tile, y, x0, dst, floats);
    }

    /// <summary>Text layers render (SkiaSharp and HarfBuzz, in Glimmer.Core) into tiles at their own position.</summary>
    public static TileGrid Rasterize(TextLayer text) => text.RenderTiles();

    internal static Vector4[] GradientTable(ImmutableArray<GradientStop> stops)
    {
        var table = new Vector4[1024];
        for (int i = 0; i < 1024; i++)
        {
            var (c, o) = GradientStop.Sample(stops, i / 1023.0);
            table[i] = new Vector4((float)c.R, (float)c.G, (float)c.B, (float)o);
        }
        return table;
    }

    // MARK: Rasterizing

    /// <summary>
    /// Renders <paramref name="layer"/> on its own (normal blending, full opacity, no mask, no styles) into raster tiles
    /// covering <paramref name="bounds"/>. Used for Rasterize Layer and Merge Group; the caller keeps the layer's own
    /// properties.
    /// </summary>
    public static TileGrid Rasterize(Layer layer, PixelRect bounds, BlendSpace blendSpace)
    {
        var solo = layer with
        {
            IsVisible = true,
            Opacity = 1,
            BlendMode = layer.IsGroup && layer.BlendMode == BlendMode.PassThrough ? BlendMode.PassThrough : BlendMode.Normal,
            Mask = null,
            IsClipped = false,
            Effects = LayerEffects.None,
        };
        var state = new DocumentState(Math.Max(1, bounds.MaxX), Math.Max(1, bounds.MaxY), blendSpace: blendSpace, layers: [solo]);
        var compositor = new DocumentCompositor();
        var coords = bounds.TileCoords;
        var b = new TileGrid.Builder();
        for (int batch = 0; batch < coords.Length; batch += 64)
        {
            var slice = coords.AsSpan(batch, Math.Min(64, coords.Length - batch)).ToArray();
            compositor.Update(state, null, slice);
            foreach (var coord in slice)
            {
                if (compositor.Texture(coord) is not { } t) continue;
                if (Tile.Make(t.Data) is { } tile) b[coord] = tile;
            }
            compositor.Purge(PixelRect.Empty);
        }
        return b.ToImmutable();
    }

    // MARK: Full-document rendering (export, thumbnails, tests)

    /// <summary>
    /// Composites every tile of the document and returns premultiplied linear Float16 RGBA pixels for
    /// <paramref name="region"/> (default: the whole document).
    /// </summary>
    /// <exception cref="RenderError">The region is empty.</exception>
    public (Half[] Buffer, int BytesPerRow, PixelRect Rect) RenderPixels(DocumentState state, LayerOverrides? overrides = null,
                                                                         PixelRect? region = null)
    {
        var rect = (region ?? state.Bounds).Intersection(state.Bounds);
        if (rect.IsEmpty) throw RenderError.ResourceCreationFailed("empty region");
        int bytesPerRow = rect.Width * TileGeometry.BytesPerPixel;
        var buffer = new Half[(long)rect.Width * rect.Height * 4];
        var coords = rect.TileCoords;
        // Batch so huge documents keep the work in bounded steps.
        for (int batch = 0; batch < coords.Length; batch += 64)
        {
            var slice = coords.AsSpan(batch, Math.Min(64, coords.Length - batch)).ToArray();
            Update(state, overrides, slice);
            Parallel.ForEach(slice, coord =>
            {
                if (Texture(coord) is not { } texture) return;
                var part = coord.Rect.Intersection(rect);
                var data = texture.Data;
                for (int y = part.Y; y < part.MaxY; y++)
                {
                    int src = ((y - coord.Rect.Y) * TileGeometry.Size + (part.X - coord.Rect.X)) * 4;
                    long dst = ((long)(y - rect.Y) * rect.Width + (part.X - rect.X)) * 4;
                    data.AsSpan(src, part.Width * 4).CopyTo(buffer.AsSpan((int)dst, part.Width * 4));
                }
            });
        }
        return (buffer, bytesPerRow, rect);
    }
}

internal static class PixelRectExtensions
{
    extension(PixelRect)
    {
        public static PixelRect Empty => new(0, 0, 0, 0);
    }
}
