// Ported from App/LayersPanel.swift (LayerRowItem, LayerRow's derived text, LayerIcon, Thumbnail).
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views.Panels;

/// <summary>A row in the flattened, top-down layer tree.</summary>
public sealed class LayerRowItem
{
    public LayerRowItem(Layer layer, int depth, IEditorSession session)
    {
        Layer = layer;
        Depth = depth;
        Session = session;
        IsActive = session.State.ActiveLayerID == layer.Id;
        Thumbnail = session.Thumbnails.TryGetValue(layer.Id, out var t) ? Thumbnails.Bitmap(t) : null;
        MaskThumbnail = session.MaskThumbnails.TryGetValue(layer.Id, out var m) ? Thumbnails.Bitmap(m) : null;
        EditingMask = session.EditingMask;
    }

    public Layer Layer { get; }
    public int Depth { get; }
    public IEditorSession Session { get; }
    public Guid Id => Layer.Id;
    public bool IsActive { get; }
    public bool EditingMask { get; }
    public ImageSource? Thumbnail { get; }
    public ImageSource? MaskThumbnail { get; }

    public string Name => Layer.Name;
    public double Indent => Depth * 14;
    public bool IsGroup => Layer.IsGroup;
    public bool IsExpanded => Layer.Content is GroupContent { Group.IsExpanded: true };
    public bool IsVisible => Layer.IsVisible;
    public bool HasMask => Layer.Mask is not null;
    public bool MaskEnabled => Layer.Mask?.IsEnabled ?? true;
    public bool HasEffects => Layer.Effects.HasEnabled;
    public bool IsClipped => Layer.IsClipped;
    public double ContentOpacity => Layer.IsVisible ? 1 : 0.5;
    /// <summary>The ring on the layer's thumbnail while the layer (not its mask) takes the paint.</summary>
    public bool LayerTargeted => IsActive && HasMask && !EditingMask;
    public bool MaskTargeted => IsActive && EditingMask;
    public IReadOnlyList<SmartFilter> SmartFilters => Layer.Content is SmartContent s ? s.Smart.Filters : [];

    /// <summary>The symbol for everything but pixel and smart layers (LayerIcon).</summary>
    public string? Symbol => Layer.Content switch
    {
        GroupContent => "folder",
        AdjustmentContent a => a.Adjustment.Kind.SymbolName(),
        FillContent { Fill: Fill.Pattern } => "square.grid.3x3",
        TextContent => "textformat",
        _ => null,
    };

    public bool ShowsThumbnail => Layer.Content is RasterContent or SmartContent;
    public bool IsSmart => Layer.Content is SmartContent;

    /// <summary>A solid or gradient fill shows its colours.</summary>
    public Brush? FillPreview => Layer.Content switch
    {
        FillContent { Fill: Fill.Solid solid } => new SolidColorBrush(Controls.SRGBBrushConverter.ToColor(solid.Color)),
        FillContent { Fill: Fill.Gradient gradient } => new LinearGradientBrush(new GradientStopCollection(
            gradient.Value.Stops.Select(s => new System.Windows.Media.GradientStop(Controls.SRGBBrushConverter.ToColor(s.Color, s.Opacity), s.Position))), 0),
        _ => null,
    };

    /// <summary>Blend mode and opacity when they are not the defaults ("Screen · 90 %").</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (Layer.BlendMode is not (BlendMode.Normal or BlendMode.PassThrough)) parts.Add(Loc.T(Layer.BlendMode.Title()));
            if (Layer.Opacity < 1) parts.Add($"{(int)Math.Round(Layer.Opacity * 100)} %");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>One element per layer for a screen reader: name, detail, and what is unusual about it.</summary>
    public string AccessibilityDescription
    {
        get
        {
            var parts = new List<string> { Layer.Name };
            if (Detail.Length > 0) parts.Add(Detail);
            if (!Layer.IsVisible) parts.Add(Loc.T("hidden"));
            if (Layer.Mask is not null) parts.Add(Loc.T("has mask"));
            if (Layer.IsClipped) parts.Add(Loc.T("clipped"));
            if (Layer.Effects.HasEnabled) parts.Add(Loc.T("has layer style"));
            return string.Join(", ", parts);
        }
    }

    public string VisibilityName => Loc.F(Layer.IsVisible ? "Hide {0}" : "Show {0}", Layer.Name);
    public string VisibilityHelp => Loc.T(Layer.IsVisible ? "Hide layer" : "Show layer");
    public string ExpandName => Loc.F(IsExpanded ? "Collapse {0}" : "Expand {0}", Layer.Name);

    /// <summary>Top-down rows, descending into expanded groups (LayersPanel.rows).</summary>
    public static List<LayerRowItem> Rows(IEditorSession session)
    {
        var rows = new List<LayerRowItem>();
        void Walk(IEnumerable<Layer> layers, int depth)
        {
            foreach (var layer in layers.Reverse())
            {
                rows.Add(new LayerRowItem(layer, depth, session));
                if (layer.Content is GroupContent { Group.IsExpanded: true } g) Walk(g.Group.Children, depth + 1);
            }
        }
        Walk(session.State.Layers, 0);
        return rows;
    }
}

/// <summary>Bitmaps of the editor's small pictures, made once per picture (RgbaImage.Id never changes for the same
/// pixels).</summary>
public static class Thumbnails
{
    private static readonly Dictionary<long, BitmapSource> cache = [];

    public static BitmapSource Bitmap(RgbaImage image)
    {
        if (cache.TryGetValue(image.Id, out var b)) return b;
        b = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null, image.Bgra.ToArray(), image.Stride);
        b.Freeze();
        if (cache.Count > 512) cache.Clear();
        cache[image.Id] = b;
        return b;
    }
}
