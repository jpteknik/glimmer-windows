// Ported from Packages/GlimmerKit/Sources/GlimmerCore/Layer.swift

using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimmer.Core;

/// <summary>The Photoshop-style blend modes Glimmer supports (W3C compositing formulas), in the Mac's declared order.</summary>
[JsonConverter(typeof(RawValueEnumConverter<BlendMode>))]
public enum BlendMode
{
    /// <summary>Groups only: children blend straight into what's below.</summary>
    [RawValue("pass-through")] PassThrough,
    [RawValue("normal")] Normal,
    [RawValue("darken")] Darken,
    [RawValue("multiply")] Multiply,
    [RawValue("color-burn")] ColorBurn,
    [RawValue("linear-burn")] LinearBurn,
    [RawValue("darker-color")] DarkerColor,
    [RawValue("lighten")] Lighten,
    [RawValue("screen")] Screen,
    [RawValue("color-dodge")] ColorDodge,
    [RawValue("linear-dodge")] LinearDodge,
    [RawValue("lighter-color")] LighterColor,
    [RawValue("overlay")] Overlay,
    [RawValue("soft-light")] SoftLight,
    [RawValue("hard-light")] HardLight,
    [RawValue("vivid-light")] VividLight,
    [RawValue("linear-light")] LinearLight,
    [RawValue("pin-light")] PinLight,
    [RawValue("hard-mix")] HardMix,
    [RawValue("difference")] Difference,
    [RawValue("exclusion")] Exclusion,
    [RawValue("subtract")] Subtract,
    [RawValue("divide")] Divide,
    [RawValue("hue")] Hue,
    [RawValue("saturation")] Saturation,
    [RawValue("color")] Color,
    [RawValue("luminosity")] Luminosity,
}

public static class BlendModeExtensions
{
    public static string RawValue(this BlendMode mode) => RawValues<BlendMode>.Of(mode);

    /// <summary>Swift's <c>BlendMode(rawValue:)</c>: null for an unknown raw value.</summary>
    public static BlendMode? Parse(string? rawValue) => RawValues<BlendMode>.Parse(rawValue);

    /// <summary>Stable index used by the blend kernels (the Mac's <c>blendColor</c> in Tiles.metal).</summary>
    public static int ShaderIndex(this BlendMode mode) => mode switch
    {
        BlendMode.Normal or BlendMode.PassThrough => 0,
        BlendMode.Multiply => 1,
        BlendMode.Screen => 2,
        BlendMode.Overlay => 3,
        BlendMode.Darken => 4,
        BlendMode.Lighten => 5,
        BlendMode.ColorDodge => 6,
        BlendMode.ColorBurn => 7,
        BlendMode.HardLight => 8,
        BlendMode.SoftLight => 9,
        BlendMode.Difference => 10,
        BlendMode.Exclusion => 11,
        BlendMode.LinearDodge => 12,
        BlendMode.LinearBurn => 13,
        BlendMode.Hue => 14,
        BlendMode.Saturation => 15,
        BlendMode.Color => 16,
        BlendMode.Luminosity => 17,
        BlendMode.VividLight => 18,
        BlendMode.LinearLight => 19,
        BlendMode.PinLight => 20,
        BlendMode.HardMix => 21,
        BlendMode.Subtract => 22,
        BlendMode.Divide => 23,
        BlendMode.DarkerColor => 24,
        BlendMode.LighterColor => 25,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string Title(this BlendMode mode) => mode switch
    {
        BlendMode.PassThrough => "Pass Through",
        BlendMode.Normal => "Normal",
        BlendMode.Darken => "Darken",
        BlendMode.Multiply => "Multiply",
        BlendMode.ColorBurn => "Color Burn",
        BlendMode.LinearBurn => "Linear Burn",
        BlendMode.DarkerColor => "Darker Color",
        BlendMode.LighterColor => "Lighter Color",
        BlendMode.Lighten => "Lighten",
        BlendMode.Screen => "Screen",
        BlendMode.ColorDodge => "Color Dodge",
        BlendMode.LinearDodge => "Linear Dodge (Add)",
        BlendMode.Overlay => "Overlay",
        BlendMode.SoftLight => "Soft Light",
        BlendMode.HardLight => "Hard Light",
        BlendMode.VividLight => "Vivid Light",
        BlendMode.LinearLight => "Linear Light",
        BlendMode.PinLight => "Pin Light",
        BlendMode.HardMix => "Hard Mix",
        BlendMode.Difference => "Difference",
        BlendMode.Exclusion => "Exclusion",
        BlendMode.Subtract => "Subtract",
        BlendMode.Divide => "Divide",
        BlendMode.Hue => "Hue",
        BlendMode.Saturation => "Saturation",
        BlendMode.Color => "Color",
        BlendMode.Luminosity => "Luminosity",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>Every mode, in declaration order (Swift's <c>allCases</c>).</summary>
    public static IReadOnlyList<BlendMode> AllCases => RawValues<BlendMode>.AllCases;

    /// <summary>Menu groups, in Photoshop order (without Pass Through).</summary>
    public static ImmutableArray<ImmutableArray<BlendMode>> MenuGroups { get; } =
    [
        [BlendMode.Normal],
        [BlendMode.Darken, BlendMode.Multiply, BlendMode.ColorBurn, BlendMode.LinearBurn, BlendMode.DarkerColor],
        [BlendMode.Lighten, BlendMode.Screen, BlendMode.ColorDodge, BlendMode.LinearDodge, BlendMode.LighterColor],
        [BlendMode.Overlay, BlendMode.SoftLight, BlendMode.HardLight, BlendMode.VividLight, BlendMode.LinearLight, BlendMode.PinLight, BlendMode.HardMix],
        [BlendMode.Difference, BlendMode.Exclusion, BlendMode.Subtract, BlendMode.Divide],
        [BlendMode.Hue, BlendMode.Saturation, BlendMode.Color, BlendMode.Luminosity],
    ];

    /// <summary>
    /// The mode a Glimmer for the web project gets: the web app has the W3C modes only, so the Photoshop extras map to
    /// the nearest one there.
    /// </summary>
    public static BlendMode WebMode(this BlendMode mode) => mode switch
    {
        BlendMode.PassThrough => BlendMode.Normal,
        BlendMode.VividLight or BlendMode.LinearLight or BlendMode.PinLight or BlendMode.HardMix => BlendMode.HardLight,
        BlendMode.Subtract => BlendMode.Difference,
        BlendMode.DarkerColor => BlendMode.Darken,
        BlendMode.LighterColor => BlendMode.Lighten,
        BlendMode.Divide => BlendMode.ColorDodge,
        _ => mode,
    };

    /// <summary>All modes the W3C formulas cover (everything except Pass Through).</summary>
    public static ImmutableArray<BlendMode> PixelModes { get; } = [.. RawValues<BlendMode>.AllCases.Where(m => m != BlendMode.PassThrough)];
}

/// <summary>The same statics on the type itself: <c>BlendMode.MenuGroups</c>, <c>BlendMode.FromRawValue("multiply")</c>.</summary>
public static class BlendModeTypeExtensions
{
    extension(BlendMode)
    {
        public static IReadOnlyList<BlendMode> AllCases => BlendModeExtensions.AllCases;
        public static ImmutableArray<ImmutableArray<BlendMode>> MenuGroups => BlendModeExtensions.MenuGroups;
        public static ImmutableArray<BlendMode> PixelModes => BlendModeExtensions.PixelModes;
        public static BlendMode? FromRawValue(string? rawValue) => BlendModeExtensions.Parse(rawValue);
    }
}

/// <summary>What a layer is (Swift's <c>enum LayerContent</c>).</summary>
public abstract record LayerContent
{
    private protected LayerContent() { }

    public abstract string KindName { get; }
}

public sealed record RasterContent(TileGrid Tiles) : LayerContent
{
    public override string KindName => "raster";
}

public sealed record AdjustmentContent(Adjustment Adjustment) : LayerContent
{
    public override string KindName => "adjustment";
}

public sealed record GroupContent(LayerGroup Group) : LayerContent
{
    public override string KindName => "group";
}

public sealed record FillContent(Fill Fill) : LayerContent
{
    public override string KindName => "fill";
}

public sealed record SmartContent(SmartObject Smart) : LayerContent
{
    public override string KindName => "smart";
}

public sealed record TextContent(TextLayer Text) : LayerContent
{
    public override string KindName => "text";
}

public sealed record Layer
{
    public Layer(string name, LayerContent content, Guid? id = null, bool isVisible = true, double opacity = 1,
                 BlendMode blendMode = BlendMode.Normal, LayerMask? mask = null, VectorPath? vectorMask = null,
                 bool isClipped = false, LayerEffects? effects = null)
    {
        Id = id ?? Guid.NewGuid();
        Name = name;
        IsVisible = isVisible;
        Opacity = opacity;
        BlendMode = blendMode;
        Mask = mask;
        VectorMask = vectorMask;
        IsClipped = isClipped;
        Effects = effects ?? LayerEffects.None;
        Content = content;
    }

    public Guid Id { get; init; }
    public string Name { get; init; }
    public bool IsVisible { get; init; }
    /// <summary>0...1, applies to the layer and its effects.</summary>
    public double Opacity { get; init; }
    public BlendMode BlendMode { get; init; }
    public LayerMask? Mask { get; init; }
    /// <summary>
    /// Shape layers: the path the mask is rasterized from (kept so the shape stays editable and scales without losing
    /// quality).
    /// </summary>
    public VectorPath? VectorMask { get; init; }
    /// <summary>
    /// The words this layer held when it was a text layer in another program's file and came in as pixels (saved):
    /// what it says stays findable though it can't be edited as text.
    /// </summary>
    public string? FormerText { get; init; }
    /// <summary>
    /// A layer developed from a RAW file keeps the file and its settings (saved), so it can be developed again. Its
    /// content is the developed picture, as ordinary pixels.
    /// </summary>
    public RawSource? Raw { get; init; }
    /// <summary>Clips to the nearest non-clipped sibling below.</summary>
    public bool IsClipped { get; init; }
    public LayerEffects Effects { get; init; }
    public LayerContent Content { get; init; }

    /// <summary>Pixels of a raster layer (empty for other kinds). Setting (with <c>with</c>) converts the layer to raster.</summary>
    public TileGrid Tiles
    {
        get => Content is RasterContent r ? r.Tiles : TileGrid.Empty;
        init => Content = new RasterContent(value);
    }

    public bool IsGroup => Content is GroupContent;
    public bool IsRaster => Content is RasterContent;
    public bool IsAdjustment => Content is AdjustmentContent;
    public bool IsText => Content is TextContent;

    public ImmutableArray<Layer> Children => Content is GroupContent g ? g.Group.Children : [];

    /// <summary>This layer and all descendants, depth first.</summary>
    public ImmutableArray<Layer> Subtree
    {
        get
        {
            var b = ImmutableArray.CreateBuilder<Layer>();
            AddSubtree(this, b);
            return b.ToImmutable();
        }
    }

    internal static void AddSubtree(Layer layer, ImmutableArray<Layer>.Builder into)
    {
        into.Add(layer);
        if (layer.Content is GroupContent g)
            foreach (var child in g.Group.Children) AddSubtree(child, into);
    }

    /// <summary>The layer's own fields (a record would also walk its subtree).</summary>
    public override string ToString() =>
        $"Layer {{ Id = {Id}, Name = {Name}, Kind = {Content.KindName}, IsVisible = {IsVisible}, Opacity = {Opacity}, BlendMode = {BlendMode} }}";

    /// <summary>A deep copy with fresh ids.</summary>
    public Layer Duplicated(string? name = null)
    {
        var copy = this with { Id = Guid.NewGuid(), Name = name ?? Name };
        if (Content is GroupContent g)
            copy = copy with { Content = new GroupContent(g.Group with { Children = [.. g.Group.Children.Select(c => c.Duplicated())] }) };
        return copy;
    }
}

/// <summary>
/// "Edit in Glimmer" from a sibling app: which file the document writes back to, kept with the document so a restored
/// window is still linked (SIBLINGS.md §6).
/// </summary>
[JsonConverter(typeof(DocumentLinkJsonConverter))]
public sealed record DocumentLink(byte[]? Bookmark, string Path, string Requester)
{
    /// <summary>
    /// A security-scoped bookmark of the file, when the Mac could make one. Kept as opaque bytes; Windows has no such
    /// thing, so a link made here has none.
    /// </summary>
    public byte[]? Bookmark { get; init; } = Bookmark;

    /// <summary>The last component of <see cref="Path"/> (a Mac path or a Windows one).</summary>
    public string FileName
    {
        get
        {
            var p = Path.TrimEnd('/', '\\');
            int cut = p.LastIndexOfAny(['/', '\\']);
            return cut >= 0 ? p[(cut + 1)..] : p;
        }
    }

    public bool Equals(DocumentLink? other) =>
        other is not null && Path == other.Path && Requester == other.Requester
        && Seq.BytesEqual(AsMemory(Bookmark), AsMemory(other.Bookmark));

    /// <summary>The bytes, or null for none (a conditional with a bare <c>null</c> would make an empty memory of it).</summary>
    internal static ReadOnlyMemory<byte>? AsMemory(byte[]? bytes) => bytes is null ? default(ReadOnlyMemory<byte>?) : bytes;

    public override int GetHashCode() => HashCode.Combine(Path, Requester, Bookmark?.Length ?? -1);
}

public sealed class DocumentLinkJsonConverter : JsonConverter<DocumentLink>
{
    public override DocumentLink Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(DocumentLink));
        return new DocumentLink(o.OptData("bookmark"), o.ReqString("path"), o.ReqString("requester"));
    }

    public override void Write(Utf8JsonWriter writer, DocumentLink value, JsonSerializerOptions options) =>
        new JsonFields(options).Data("bookmark", DocumentLink.AsMemory(value.Bookmark))
            .String("path", value.Path).String("requester", value.Requester).WriteTo(writer);
}

/// <summary>
/// The complete, immutable state of a document. Snapshots are cheap: tiles are shared, immutable references. Every
/// change returns a new state (<c>state = state.InsertLayerAboveActive(layer)</c>); callers register undo.
/// </summary>
public sealed partial record DocumentState
{
    public DocumentState(int width, int height, WorkingColorSpace colorSpace = WorkingColorSpace.SRGB,
                         BlendSpace blendSpace = BlendSpace.Linear, IEnumerable<Layer>? layers = null, Guid? activeLayerID = null)
    {
        Width = width;
        Height = height;
        ColorSpace = colorSpace;
        BlendSpace = blendSpace;
        Layers = layers is null ? [] : [.. layers];
        ActiveLayerID = activeLayerID ?? (Layers.IsEmpty ? null : Layers[^1].Id);
    }

    public int Width { get; init; }
    public int Height { get; init; }
    public WorkingColorSpace ColorSpace { get; init; }
    public BlendSpace BlendSpace { get; init; }
    /// <summary>Root layers, bottom → top.</summary>
    public ImmutableArray<Layer> Layers { get; init; }
    public Guid? ActiveLayerID { get; init; }
    /// <summary>The current pixel selection (null = nothing selected). Not saved with the document.</summary>
    public Selection? Selection { get; init; }
    /// <summary>Pen-tool paths (saved).</summary>
    public ImmutableArray<VectorPath> Paths { get; init; } = [];
    /// <summary>Guides, grid and rulers (saved).</summary>
    public ViewAids Aids { get; init; } = new();
    /// <summary>
    /// The file of another app this document is an edit of (saved, so the link survives a restart). Only set on the
    /// copy that is being written or was just read; the editor keeps the live link itself.
    /// </summary>
    public DocumentLink? Link { get; init; }
    /// <summary>What was kept and lost when this document was made from a Photoshop file (saved).</summary>
    public PSDImportReport? ImportReport { get; init; }

    public Vec2 Size => new(Width, Height);
    public PixelRect Bounds => new(0, 0, Width, Height);

    /// <summary>A summary (a record would also compute every tile id the document holds).</summary>
    public override string ToString() =>
        $"DocumentState {{ Width = {Width}, Height = {Height}, Layers = {Layers.Length}, ActiveLayerID = {ActiveLayerID} }}";

    // MARK: Tree access

    /// <summary>
    /// Index path from the root (<c>[2, 0]</c> = first child of the third root layer) of the layer with
    /// <paramref name="id"/>, or null.
    /// </summary>
    public int[]? PathOf(Guid? id)
    {
        if (id is not { } target) return null;
        var prefix = new List<int>();
        return Search(Layers, prefix) ? [.. prefix] : null;

        bool Search(ImmutableArray<Layer> list, List<int> path)
        {
            for (int i = 0; i < list.Length; i++)
            {
                path.Add(i);
                if (list[i].Id == target) return true;
                if (Search(list[i].Children, path)) return true;
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }
    }

    public Layer? LayerAt(IReadOnlyList<int> path)
    {
        var list = Layers;
        Layer? result = null;
        foreach (int i in path)
        {
            if (i < 0 || i >= list.Length) return null;
            result = list[i];
            list = list[i].Children;
        }
        return result;
    }

    /// <summary>Swift's <c>layer(id:)</c>.</summary>
    public Layer? LayerById(Guid? id) => PathOf(id) is { } path ? LayerAt(path) : null;

    /// <summary>Siblings at the given parent path (<c>[]</c> = root).</summary>
    public ImmutableArray<Layer> ChildrenOf(IReadOnlyList<int> parent) =>
        parent.Count == 0 ? Layers : (LayerAt(parent)?.Children ?? []);

    internal DocumentState ModifyChildren(IReadOnlyList<int> parent, Func<ImmutableArray<Layer>, ImmutableArray<Layer>> change)
    {
        if (parent.Count == 0) return this with { Layers = change(Layers) };
        return this with { Layers = Walk(Layers, 0) };

        ImmutableArray<Layer> Walk(ImmutableArray<Layer> list, int depth)
        {
            int i = parent[depth];
            if (i < 0 || i >= list.Length || list[i].Content is not GroupContent g) return list;
            var children = depth == parent.Count - 1 ? change(g.Group.Children) : Walk(g.Group.Children, depth + 1);
            return list.SetItem(i, list[i] with { Content = new GroupContent(g.Group with { Children = children }) });
        }
    }

    public Layer? ActiveLayer => LayerById(ActiveLayerID);

    /// <summary>Every layer in the tree, depth first.</summary>
    public ImmutableArray<Layer> AllLayers
    {
        get
        {
            var b = ImmutableArray.CreateBuilder<Layer>();
            foreach (var layer in Layers) Layer.AddSubtree(layer, b);
            return b.ToImmutable();
        }
    }

    /// <summary>The bytes of the RAW files this document's layers keep (they are saved with it and held while it is open).</summary>
    public long KeptRawBytes => AllLayers.Sum(l => (long)(l.Raw?.Data.Length ?? 0));

    public DocumentState UpdateLayer(Guid id, Func<Layer, Layer> change)
    {
        if (PathOf(id) is not { } path) return this;
        return ModifyChildren(path[..^1], list => list.SetItem(path[^1], change(list[path[^1]])));
    }

    // MARK: Factories

    /// <summary>What a new document starts with: nothing, or one colour (premultiplied linear).</summary>
    public readonly record struct Background(Vector4? Color)
    {
        public static Background Transparent => new(null);
        public static Background White => new(new Vector4(1, 1, 1, 1));
        public static Background Black => new(new Vector4(0, 0, 0, 1));
        public static Background Of(Vector4 color) => new(color);
    }

    /// <summary>Swift's <c>DocumentState.new(width:height:background:)</c>.</summary>
    public static DocumentState New(int width, int height, Background background)
    {
        var bounds = new PixelRect(0, 0, width, height);
        var layer = background.Color is { } c
            ? new Layer("Background", new RasterContent(TileGrid.Solid(c, bounds)))
            : new Layer("Layer 1", new RasterContent(TileGrid.Empty));
        return new DocumentState(width, height, layers: [layer]);
    }

    // MARK: Layer operations (pure; callers register undo)

    /// <summary>A name like "Layer 3" that isn't taken yet.</summary>
    public string NextLayerName(string prefix = "Layer")
    {
        var taken = AllLayers.Select(l => l.Name).ToHashSet();
        int n = 1;
        while (taken.Contains($"{prefix} {n}")) n++;
        return $"{prefix} {n}";
    }

    /// <summary>Inserts above the active layer, in the same group, and activates it.</summary>
    public DocumentState InsertLayerAboveActive(Layer layer)
    {
        var state = PathOf(ActiveLayerID) is { } path
            ? ModifyChildren(path[..^1], list => list.Insert(path[^1] + 1, layer))
            : this with { Layers = Layers.Add(layer) };
        return state with { ActiveLayerID = layer.Id };
    }

    /// <summary>Duplicates the active layer above itself and activates the copy (returned too).</summary>
    public (DocumentState State, Layer? Copy) DuplicateActiveLayer()
    {
        if (PathOf(ActiveLayerID) is not { } path || LayerAt(path) is not { } original) return (this, null);
        var copy = original.Duplicated(original.Name + " copy");
        var state = ModifyChildren(path[..^1], list => list.Insert(path[^1] + 1, copy)) with { ActiveLayerID = copy.Id };
        return (state, copy);
    }

    public DocumentState DeleteActiveLayer()
    {
        if (PathOf(ActiveLayerID) is not { } path) return this;
        var parent = path[..^1];
        int i = path[^1];
        var state = ModifyChildren(parent, list => list.RemoveAt(i));
        var siblings = state.ChildrenOf(parent);
        Guid? active;
        if (!siblings.IsEmpty) active = siblings[Math.Max(0, i - 1)].Id;
        else if (parent.Length > 0) active = state.LayerAt(parent)?.Id;
        else active = null;
        return state with { ActiveLayerID = active };
    }

    /// <summary>Moves the active layer by <paramref name="delta"/> positions among its siblings (positive = up).</summary>
    public (DocumentState State, bool Moved) MoveActiveLayer(int delta)
    {
        if (PathOf(ActiveLayerID) is not { } path) return (this, false);
        var parent = path[..^1];
        int i = path[^1];
        int count = ChildrenOf(parent).Length;
        int target = Math.Min(Math.Max(i + delta, 0), count - 1);
        if (target == i) return (this, false);
        return (ModifyChildren(parent, list =>
        {
            var layer = list[i];
            return list.RemoveAt(i).Insert(target, layer);
        }), true);
    }

    /// <summary>Removes a layer (and its subtree) and returns it.</summary>
    public (DocumentState State, Layer? Removed) RemoveLayer(Guid id)
    {
        if (PathOf(id) is not { } path || LayerAt(path) is not { } layer) return (this, null);
        return (ModifyChildren(path[..^1], list => list.RemoveAt(path[^1])), layer);
    }

    /// <summary>
    /// Moves <paramref name="id"/> so it sits directly above <paramref name="target"/> in target's parent, or into the
    /// bottom of the root when <paramref name="target"/> is null (Swift's <c>moveLayer(_:above:)</c>).
    /// </summary>
    public DocumentState MoveLayerAbove(Guid id, Guid? target)
    {
        if (id == target) return this;
        if (target is { } t && LayerById(id) is { } m && m.Subtree.Any(l => l.Id == t)) return this;   // can't move a group into itself
        var (state, moving) = RemoveLayer(id);
        if (moving is null) return this;
        if (target is { } tt && state.PathOf(tt) is { } tp)
            return state.ModifyChildren(tp[..^1], list => list.Insert(tp[^1] + 1, moving));
        return state with { Layers = state.Layers.Insert(0, moving) };
    }

    /// <summary>Moves <paramref name="id"/> to the top of <paramref name="group"/>'s children (Swift's <c>moveLayer(_:intoTopOf:)</c>).</summary>
    public DocumentState MoveLayerIntoTopOf(Guid id, Guid group)
    {
        if (id == group || LayerById(id) is not { } moving || moving.Subtree.Any(l => l.Id == group)) return this;
        var (state, _) = RemoveLayer(id);
        if (state.PathOf(group) is not { } gp) return state;
        return state.ModifyChildren(gp, list => list.Add(moving));
    }

    /// <summary>Wraps the active layer in a new group and activates the group. Returns the group's id.</summary>
    public (DocumentState State, Guid? GroupID) GroupActiveLayer(string name)
    {
        if (PathOf(ActiveLayerID) is not { } path || LayerAt(path) is not { } layer) return (this, null);
        var group = new Layer(name, new GroupContent(new LayerGroup([layer])), blendMode: BlendMode.PassThrough);
        var state = ModifyChildren(path[..^1], list => list.SetItem(path[^1], group)) with { ActiveLayerID = group.Id };
        return (state, group.Id);
    }

    /// <summary>Replaces the active group with its children.</summary>
    public DocumentState UngroupActiveLayer()
    {
        if (PathOf(ActiveLayerID) is not { } path || LayerAt(path) is not { IsGroup: true } group) return this;
        var kids = group.Children;
        var state = ModifyChildren(path[..^1], list => list.RemoveAt(path[^1]).InsertRange(path[^1], kids));
        return state with { ActiveLayerID = kids.IsEmpty ? null : kids[^1].Id };
    }

    /// <summary>The layer a clipped layer at <paramref name="path"/> clips to (nearest non-clipped sibling below).</summary>
    public Layer? ClipBase(IReadOnlyList<int> path)
    {
        var siblings = ChildrenOf(path.Take(path.Count - 1).ToArray());
        for (int i = path[^1] - 1; i >= 0; i--)
        {
            if (!siblings[i].IsClipped) return siblings[i];
        }
        return null;
    }
}
