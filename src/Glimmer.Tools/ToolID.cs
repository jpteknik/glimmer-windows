// Ported from Packages/GlimmerKit/Sources/GlimmerTools/ToolID.swift
namespace Glimmer.Tools;

/// <summary>
/// Every tool in the tool palette, with its single-key shortcut. Mirrors the web app's rail
/// (<c>web/src/ui/ToolRail.tsx</c>); tools are implemented phase by phase (see docs/PLAN.md §3).
/// </summary>
public enum ToolID
{
    Move, Hand,
    RectangleMarquee, EllipseMarquee, Lasso, PolygonLasso, MagicWand, MagicSelect,
    Crop, FreeTransform,
    Brush, Eraser, PaintBucket, Gradient, PatternStamp,
    CloneStamp, HealingBrush, Dodge, Burn, Blur, Sharpen, Smudge,
    Pen, Type, Shape,
    Eyedropper,
}

public static class ToolIDExtensions
{
    extension(ToolID)
    {
        /// <summary>Swift's <c>CaseIterable.allCases</c>, in declaration order.</summary>
        public static IReadOnlyList<ToolID> AllCases => Cases;

        /// <summary>Swift's <c>init(rawValue:)</c>: null for an unknown raw value.</summary>
        public static ToolID? FromRawValue(string? raw)
        {
            if (raw is null) return null;
            foreach (var t in Cases) if (t.RawValue() == raw) return t;
            return null;
        }
    }

    private static readonly ToolID[] Cases = Enum.GetValues<ToolID>();

    /// <summary>The Swift raw value (the case name), used wherever the id is stored or sent.</summary>
    public static string RawValue(this ToolID tool)
    {
        string name = tool.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static char? ShortcutKey(this ToolID tool) => tool switch
    {
        ToolID.Move => 'v',
        ToolID.Hand => 'h',
        ToolID.RectangleMarquee or ToolID.EllipseMarquee => 'm',
        ToolID.Lasso => 'l',
        ToolID.MagicWand => 'w',
        ToolID.MagicSelect => 'a',
        ToolID.Crop => 'c',
        ToolID.FreeTransform => 't',
        ToolID.Brush => 'b',
        ToolID.Eraser => 'e',
        ToolID.PaintBucket => 'k',
        ToolID.Gradient => 'g',
        ToolID.CloneStamp => 's',
        ToolID.HealingBrush => 'j',
        ToolID.Dodge or ToolID.Burn => 'o',
        ToolID.Pen => 'p',
        ToolID.Type => 'y',
        ToolID.Shape => 'u',
        ToolID.Eyedropper => 'i',
        _ => null,  // patternStamp, blur, sharpen, smudge, polygonLasso
    };
}
