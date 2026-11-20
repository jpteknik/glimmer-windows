// Ported from App/EditorSession+Text.swift
using Glimmer.Core;
using Glimmer.Tools;

namespace Glimmer.Editor;

public sealed partial class EditorSession
{
    // MARK: Text

    /// <summary>The topmost visible text layer under a document point.</summary>
    public Layer? TextLayerAtPoint(Vec2 doc) => State.AllLayers.Reverse().FirstOrDefault(layer =>
    {
        if (!layer.IsVisible || layer.Content is not TextContent t) return false;
        var b = t.Text.Bounds;
        return doc.X >= b.X && doc.X <= b.MaxX && doc.Y >= b.Y && doc.Y <= b.MaxY;
    });

    public TextLayer? TextLayerAt(Vec2 point) => (TextLayerAtPoint(point)?.Content as TextContent)?.Text;

    /// <summary>Type tool click: edit the text layer under the pointer, or start a new one.</summary>
    public void TypeClick(Vec2 doc)
    {
        CommitLiveEdit();
        if (TextLayerAtPoint(doc) is { } hit)
        {
            SetActiveLayer(hit.Id);
            EditingText = hit.Id;
            return;
        }
        // Clicking on the active path starts text along it.
        if (ActivePath is { } path && path.Polygons().FirstOrDefault() is { Length: > 1 } line)
        {
            double tol = 8 / Math.Max(Canvas.Transform.Scale, 0.001);
            bool near = line.Zip(line.Skip(1)).Any(seg => Distance(doc, seg.First, seg.Second) < tol);
            if (near)
            {
                AddTextOnPath(path.Id);
                return;
            }
        }
        var text = new TextLayer("", doc - new Vec2(0, textDefaults.Size * 0.9), textDefaults.FontName, textDefaults.Size, foreground, textDefaults.Alignment);
        var layer = new Layer(Environment.LayerName("Text"), new TextContent(text));
        Edit("New Text Layer", s => s.InsertLayerAboveActive(layer));
        EditingText = layer.Id;
    }

    /// <summary>A new text layer whose baseline follows the path (its first subpath).</summary>
    public void AddTextOnPath(Guid pathID)
    {
        if (State.Paths.FirstOrDefault(p => p.Id == pathID) is not { } path || path.Polygons().FirstOrDefault() is not { Length: > 1 } line) return;
        var text = new TextLayer("", line[0] - new Vec2(0, textDefaults.Size * 0.9), textDefaults.FontName, textDefaults.Size, foreground, textDefaults.Alignment)
        {
            OnPath = [.. line],
            OnPathID = pathID,
        };
        var layer = new Layer(Environment.LayerName("Text"), new TextContent(text));
        Edit("New Text Layer", s => s.InsertLayerAboveActive(layer));
        Tool = ToolID.Type;
        EditingText = layer.Id;
    }

    public static double Distance(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double t = ab.LengthSquared > 0 ? Math.Max(0, Math.Min(1, Vec2.Dot(p - a, ab) / ab.LengthSquared)) : 0;
        return EditorHelpers.Distance(p, a + ab * t);
    }

    /// <summary>Stores what the text editor holds when it closes: the text and the style of each stretch (UTF-16 ranges:
    /// WPF's TextBox offsets, NSRange on the Mac).</summary>
    public void CommitText(string text, IReadOnlyList<(TextRange Range, TextStyle Style)> runs, Guid layerId)
    {
        if (State.LayerById(layerId)?.Content is not TextContent content) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            Edit("Delete Empty Text", s => s.RemoveLayer(layerId).State);
            return;
        }
        var before = content.Text;
        var styled = before.SetStyledText(text, runs);
        if (styled == before) return;
        var firstLine = text.Split('\n').FirstOrDefault(l => l.Length > 0) ?? "Text";
        // The first 32 characters (grapheme clusters, as Swift counts them).
        var info = new System.Globalization.StringInfo(firstLine);
        var name = info.LengthInTextElements > 32 ? info.SubstringByTextElements(0, 32) : firstLine;
        UiText action = styled.String == before.String ? "Style Text" : "Edit Text";
        Edit(action, s => s.UpdateLayer(layerId, l => l with { Content = new TextContent(styled), Name = name }));
    }

    /// <summary>Live change to the active text layer's style (options bar). While text is being edited with letters
    /// selected, font, size and colour change only those letters; otherwise the whole layer (and the per-word values of
    /// that property give way).</summary>
    public void UpdateText(Func<TextLayer, TextLayer> change)
    {
        if (ActiveLayer?.Content is TextContent tc && TextStyler is { } styler)
        {
            var t = tc.Text;
            if (textSelectionIsRange && textSelectionStyle is { } selected)
            {
                var probe = change(t with { FontName = selected.FontName, Size = selected.Size, Color = selected.Color });
                styler.Style(true,
                             probe.FontName != selected.FontName ? probe.FontName : null,
                             probe.Size != selected.Size ? probe.Size : null,
                             probe.Color != selected.Color ? probe.Color : null);
                // Alignment, tracking and line height belong to the whole layer.
                var rest = change(t) with { FontName = t.FontName, Size = t.Size, Color = t.Color };
                if (rest != t) LiveLayer("Text Style", l => l with { Content = new TextContent(rest) });
                return;
            }
            var changed = change(t);
            styler.Style(false,
                         changed.FontName != t.FontName ? changed.FontName : null,
                         changed.Size != t.Size ? changed.Size : null,
                         changed.Color != t.Color ? changed.Color : null);
        }
        if (ActiveLayer?.Content is TextContent)
        {
            LiveLayer("Text Style", layer =>
            {
                if (layer.Content is not TextContent current) return layer;
                var old = current.Text;
                var t = change(old).DropStyles(old);
                return layer with { Content = new TextContent(t) };
            });
        }
        var d = change(new TextLayer("", Vec2.Zero, textDefaults.FontName, textDefaults.Size, SRGBColor.Black, textDefaults.Alignment));
        TextDefaults = new TextDefaults(d.FontName, d.Size, d.Alignment);
    }

    /// <summary>Puts the active text layer on a path (its first subpath), or back on a straight line.</summary>
    public void SetTextPath(Guid? pathID)
    {
        if (ActiveLayer is not { Content: TextContent tc } layer) return;
        var t = tc.Text;
        if (pathID is { } pid)
        {
            if (State.Paths.FirstOrDefault(p => p.Id == pid)?.Polygons().FirstOrDefault() is not { Length: > 1 } line) return;
            t = t with { OnPath = [.. line], OnPathID = pid };
        }
        else
        {
            // Stay where the text was: its box starts at the beginning of the old baseline.
            if (t.OnPath is { Length: > 0 } onPath) t = t with { Origin = onPath[0] - new Vec2(0, t.Size * 0.9) };
            t = t with { OnPath = null, OnPathID = null };
        }
        var id = layer.Id;
        Edit(pathID is null ? "Remove Text from Path" : "Text on Path", s => s.UpdateLayer(id, l => l with { Content = new TextContent(t) }));
    }

    /// <summary>Live-updates the active text layer's warp (null removes it).</summary>
    public void UpdateTextWarp(TextWarp? warp)
    {
        if (ActiveLayer?.Content is not TextContent) return;
        LiveLayer("Warp Text", layer => layer.Content is TextContent t ? layer with { Content = new TextContent(t.Text with { Warp = warp }) } : layer);
    }
}
