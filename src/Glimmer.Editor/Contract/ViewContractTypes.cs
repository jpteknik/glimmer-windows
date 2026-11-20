// The view contract: what the WPF views need from the editor library (Glimmer.Editor). Written by the WPF port and
// implemented here by the real types (EditorSession, CanvasController, AIController, GlimmerDocument). Swift camelCase
// names become PascalCase; everything else keeps the original's name.
//
// Rules that hold for every member:
//  * Main thread. Every member is called on the UI thread, and every change notification is raised on it. Work done
//    in the background posts its result back through the IUiScheduler the object was given (by default the
//    SynchronizationContext captured when it was constructed: Swift's @MainActor).
//  * Observation. Each interface extends INotifyPropertyChanged and raises PropertyChanged with the member's name
//    whenever a value a view can read changes (Swift's @Observable). A change to State also raises it for every member
//    derived from State (ActiveLayer, IsUntouchedBlank, ...). Raising more often than needed is fine; raising less is
//    a bug that shows as a panel that does not update.
//  * Values. Core model types (DocumentState, Layer, BrushSettings, SRGBColor, Vec2, ...) are the immutable records
//    of Glimmer.Core and Glimmer.Tools. A view never mutates them; it hands a new value back (Brush = Brush with { Size = 20 }).
//  * Text. Text the views show is English, word for word as in the Swift source. Text the editor composes (toasts,
//    errors, step names, job titles) is a UiText, so the app can look the template up in its Swedish table before
//    formatting. A UiText argument may itself be a UiText (a translated value inside a sentence: "New {0}" with
//    "Curves").
using System.ComponentModel;
using Glimmer.Core;

namespace Glimmer.Editor;

/// <summary>A text for a person, as the English template and its arguments. The app translates the template (the
/// table is keyed by it) and then formats; ToString() gives the English. Templates use .NET composite placeholders:
/// Swift's "Exported \(name) (\(size))." is new UiText("Exported {0} ({1}).", name, size).</summary>
public sealed class UiText : IEquatable<UiText>
{
    public UiText(string template, params object?[] args)
    {
        Template = template;
        Args = args;
    }

    public string Template { get; }
    public IReadOnlyList<object?> Args { get; }

    /// <summary>A text that is shown exactly as given, never looked up (a file name, an error's own words).</summary>
    public static UiText Verbatim(string text) => new("{0}", text) { IsVerbatim = true };

    public bool IsVerbatim { get; private init; }

    public override string ToString() =>
        Args.Count == 0 ? Template : string.Format(System.Globalization.CultureInfo.CurrentCulture, Template, Args.ToArray());

    public bool Equals(UiText? other) =>
        other is not null && Template == other.Template && IsVerbatim == other.IsVerbatim && Args.SequenceEqual(other.Args);

    public override bool Equals(object? obj) => Equals(obj as UiText);
    public override int GetHashCode() => HashCode.Combine(Template, Args.Count, IsVerbatim);
    public static implicit operator UiText(string template) => new(template);
}

/// <summary>Modifier keys of a pointer or key event, in the original's terms. The app maps Windows keys onto them as
/// Photoshop does: Ctrl is Command, Alt is Option, Shift is Shift. Control (the Mac's ⌃) has no key of its own on
/// Windows and is never set by the app.</summary>
[Flags]
public enum InputModifiers
{
    None = 0,
    Shift = 1,
    /// <summary>⌥ on the Mac, Alt on Windows.</summary>
    Option = 2,
    /// <summary>⌘ on the Mac, Ctrl on Windows.</summary>
    Command = 4,
    Control = 8,
}

/// <summary>One pointer sample for painting tools, in document pixels. A mouse gives Pressure 1, Tilt zero and
/// Rotation 0. Pressure has already been through the pressure curve in Settings › Tablet (the app does that with
/// TabletSettings.Curve, as CanvasNSView.pressure(_:) did); Tilt is in document orientation (the app undoes the view
/// rotation).</summary>
public readonly record struct PointerSample(Vec2 Position, double Pressure = 1, Vec2 Tilt = default, double Rotation = 0);

/// <summary>A small picture the editor hands to the views (layer thumbnails, the navigator, the depth map): 8-bit
/// BGRA, premultiplied, sRGB, rows top-down, Stride bytes apart. Immutable; Id never changes for the same pixels, so
/// the app can cache the bitmap it makes from it.</summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height, int stride, ReadOnlyMemory<byte> bgra)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Bgra = bgra;
    }

    public long Id { get; } = Interlocked.Increment(ref next);
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public ReadOnlyMemory<byte> Bgra { get; }
    private static long next;
}

/// <summary>Which tab the side panel shows (EditorSession.InspectorTab). The raw values are the English tab titles.</summary>
public enum InspectorTab { Layers, Paths, Channels, History, AI }

/// <summary>The AI panel section to reveal (EditorSession.AISection).</summary>
public enum AISection { Assistant, Generate, Fill, Expand, ColorMatch, Relight, Reflections, Harmonize, Upscale, Cleanup, Presets, LensBlur }

/// <summary>EditorSession.ShapeKind.</summary>
public enum ShapeKind { Rectangle, RoundedRectangle, Ellipse, Line }

/// <summary>EditorSession.TransformMode.</summary>
public enum TransformMode { ScaleRotate, Distort, Skew, Perspective }

/// <summary>Selection.Operation, as the marquee and lasso tools pick it from the modifiers held at the press.</summary>
public enum SelectionOperation { Replace, Add, Subtract, Intersect }

/// <summary>The marquee being dragged (EditorSession.Marquee), in document pixels.</summary>
public readonly record struct Marquee(Vec2 Start, Vec2 Current, bool Ellipse, SelectionOperation Operation, bool FromCentre)
{
    /// <summary>The rectangle the marquee covers: from the press point, or centred on it while Option is held.</summary>
    public (double X0, double Y0, double X1, double Y1) Rect => FromCentre
        ? (Math.Min(2 * Start.X - Current.X, Current.X), Math.Min(2 * Start.Y - Current.Y, Current.Y),
           Math.Max(2 * Start.X - Current.X, Current.X), Math.Max(2 * Start.Y - Current.Y, Current.Y))
        : (Math.Min(Start.X, Current.X), Math.Min(Start.Y, Current.Y), Math.Max(Start.X, Current.X), Math.Max(Start.Y, Current.Y));

    /// <summary>The same rectangle as Glimmer.Core takes it.</summary>
    public CGRectLike CGRect
    {
        get
        {
            var (x0, y0, x1, y1) = Rect;
            return new CGRectLike(x0, y0, x1, y1);
        }
    }
}

/// <summary>A guide being dragged out of a ruler (Id null) or moved (EditorSession.guideDrag).</summary>
public readonly record struct GuideDrag(Guid? Id, GuideAxis Axis, double Position);

/// <summary>A drag between two document points: the gradient tool and the shape tool.</summary>
public readonly record struct DragSegment(Vec2 Start, Vec2 End);

/// <summary>A link that can't be reached after a restart (EditorSession.brokenLink): what it was, and why not.</summary>
public sealed record BrokenLink(DocumentLink Link, string Reason);

/// <summary>A question or refusal about the text service while it is up (AIController.TextNotice). Proceed: what Send
/// does (null: the notice only explains).</summary>
public sealed record TextNotice(TextNoticeKind Kind, UiText Title, UiText Message, Action? Proceed)
{
    public Guid Id { get; } = Guid.NewGuid();
}

public enum TextNoticeKind { FirstUse, UnknownPrice, CantSee }

/// <summary>Text tool defaults for new text (EditorSession.textDefaults).</summary>
public readonly record struct TextDefaults(string FontName, double Size, TextAlignment Alignment);

/// <summary>The input histogram of the Levels layer being edited.</summary>
public sealed record LevelsHistogramFor(Guid LayerId, Histogram Histogram);

/// <summary>A filter dialog that is open (EditorSession.filterDialog): what it edits.</summary>
public sealed record FilterDialogItem(FilterSettings Settings)
{
    public string Id => Settings.Kind.RawValue();
}
