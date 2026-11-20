// Windows only: the selection outline the canvas draws the marching ants from (see SelectionOutline.cs).
using Glimmer.Core;

namespace Glimmer.Editor;

public sealed partial class EditorSession
{
    private IReadOnlyList<IReadOnlyList<Vec2>> selectionOutline = [];
    private (Selection? Selection, int Step, PixelRect Bounds) outlineKey;
    private int outlineGeneration;

    /// <summary>The selection's outline as closed polylines in document pixels. Traced off the UI thread when the
    /// selection changes or the zoom asks for a finer or coarser outline; until then the previous one stays.</summary>
    public IReadOnlyList<IReadOnlyList<Vec2>> SelectionOutline => selectionOutline;

    private int OutlineStep => SelectionOutlineTracer.StepFor(Canvas.Zoom, State.Bounds);

    /// <summary>The document changed: a new selection gets a new outline.</summary>
    private void NoteSelectionChanged() => RefreshOutline();

    /// <summary>The view zoomed: the outline may need another level of detail.</summary>
    internal void NoteZoomChanged()
    {
        if (State.Selection is not null && OutlineStep != outlineKey.Step) RefreshOutline();
    }

    private void RefreshOutline()
    {
        var selection = State.Selection;
        var bounds = State.Bounds;
        int step = OutlineStep;
        if (ReferenceEquals(selection, outlineKey.Selection) && step == outlineKey.Step && bounds == outlineKey.Bounds) return;
        outlineKey = (selection, step, bounds);
        int generation = ++outlineGeneration;
        if (selection is null)
        {
            selectionOutline = [];
            Raise(nameof(SelectionOutline));
            return;
        }
        Scheduler.Background(() => SelectionOutlineTracer.Trace(selection, bounds, step), outline =>
        {
            selectionOutline = outline;
            Raise(nameof(SelectionOutline));
        }, () => generation != outlineGeneration);
    }
}
