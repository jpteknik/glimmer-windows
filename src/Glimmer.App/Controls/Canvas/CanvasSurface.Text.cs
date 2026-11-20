// Ported from App/CanvasView.swift (CanvasNSView: updateTextEditor, fitEditor, endTextEditing, the editor's place).
// The editor (TextLayerEditor) is the canvas's one child element, laid over the text layer: at the text's transformed
// origin, turned like the text and the view together, and scaled with the zoom (a skew is not shown while typing, as
// on the Mac). While it is open the layer's own pixels are left out by the renderer (IEditorSession.EditingText).
using System.Collections;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Controls.Canvas;

public sealed partial class CanvasSurface
{
    private TextLayerEditor? textEditor;
    private Guid? editorLayerID;
    private Point editorOrigin;
    private bool editorUpdateQueued;
    /// <summary>The last Return came from the numeric keypad (seen in the window's WM_KEYDOWN; WPF doesn't say).</summary>
    private bool keypadEnter;

    protected override int VisualChildrenCount => textEditor is null ? 0 : 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 && textEditor is not null ? textEditor : throw new ArgumentOutOfRangeException(nameof(index));

    protected override IEnumerator LogicalChildren => (textEditor is null ? Array.Empty<object>() : new object[] { textEditor }).GetEnumerator();

    protected override Size ArrangeOverride(Size finalSize)
    {
        textEditor?.Arrange(new Rect(editorOrigin, new Size(textEditor.Width, textEditor.Height)));
        return finalSize;
    }

    /// <summary>The event started inside the text editor: the editor handles it, the tools must not.</summary>
    private bool FromEditor(RoutedEventArgs e)
    {
        if (textEditor is null || e.OriginalSource is not DependencyObject d) return false;
        for (DependencyObject? x = d; x is not null; x = x is Visual ? VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
        {
            if (ReferenceEquals(x, textEditor)) return true;
            if (ReferenceEquals(x, this)) return false;
        }
        return false;
    }

    /// <summary>Coalesces the many reasons to look at the editor (the session, the view, the tool) into one pass.</summary>
    private void QueueTextEditorUpdate()
    {
        if (editorUpdateQueued) return;
        editorUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            editorUpdateQueued = false;
            UpdateTextEditor();
        });
    }

    private void UpdateTextEditor()
    {
        if (Session is not { } session || Controller is not { } canvas) return;
        // Commit the open editor when editing ended (Esc, a click elsewhere, a tool change).
        if (session.EditingText is not null && session.EffectiveTool != ToolID.Type) session.EditingText = null;
        if (textEditor is not null && editorLayerID is { } open && session.EditingText != open) CloseTextEditor(session, open);
        if (session.EditingText is not { } id || session.State.LayerById(id)?.Content is not TextContent { Text: var text }) return;

        // Where the text shows: its origin and one pixel along its baseline, through its own transform and the view's.
        var t = text.Transform ?? AffineTransform.Identity;
        var o = canvas.Transform.ViewPoint(t.Apply(text.Origin));
        var x = canvas.Transform.ViewPoint(t.Apply(text.Origin + new Vec2(1, 0)));
        double scale = Math.Max((x - o).Length, 1e-6);
        double angle = Math.Atan2(x.Y - o.Y, x.X - o.X) * 180 / Math.PI;

        bool isNew = false;
        if (textEditor is null)
        {
            var editor = new TextLayerEditor(text, scale) { KeypadEnter = () => keypadEnter };
            editor.ContentChanged += (_, _) => { FitTextEditor(); UpdateTextSelectionStyle(); };
            editor.SelectionStyleChanged += (_, _) => UpdateTextSelectionStyle();
            editor.EndRequested += (_, _) => EndTextEditing();
            editor.PreviewMouseWheel += (_, e) => { HandleWheel(e); e.Handled = true; };   // zoom and pan still work over the text
            textEditor = editor;
            editorLayerID = id;
            AddLogicalChild(editor);
            AddVisualChild(editor);
            session.TextStyler = editor;
            isNew = true;
        }
        textEditor.SetAlignment(text.Alignment);
        textEditor.BoxWidth = text.BoxWidth is { } box ? box * textEditor.Scale : null;
        // Built at the zoom it opened at; a later zoom scales the editor as a whole (see TextLayerEditor).
        double ratio = scale / textEditor.Scale;
        var turn = new TransformGroup();
        if (Math.Abs(ratio - 1) > 1e-9) turn.Children.Add(new ScaleTransform(ratio, ratio));
        turn.Children.Add(new RotateTransform(angle));
        textEditor.RenderTransform = turn;
        editorOrigin = new Point(o.X, o.Y);
        FitTextEditor();
        if (isNew)
        {
            var editor = textEditor;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!ReferenceEquals(editor, textEditor)) return;
                editor.Focus();
                Keyboard.Focus(editor);
                editor.SelectAll();
            });
        }
        UpdateTextSelectionStyle();
    }

    private void FitTextEditor()
    {
        if (textEditor is null) return;
        textEditor.Fit();
        InvalidateArrange();
    }

    /// <summary>Tells the options bar the style at the caret or the start of the selection.</summary>
    private void UpdateTextSelectionStyle()
    {
        if (textEditor is null || Session is not { } session) return;
        var style = textEditor.SelectionStyle();
        if (session.TextSelectionStyle != style) session.TextSelectionStyle = style;
        bool isRange = !textEditor.Selection.IsEmpty;
        if (session.TextSelectionIsRange != isRange) session.TextSelectionIsRange = isRange;
    }

    private void CloseTextEditor(IEditorSession session, Guid id)
    {
        var editor = textEditor!;
        textEditor = null;
        editorLayerID = null;
        var (text, runs) = editor.Contents();
        RemoveVisualChild(editor);
        RemoveLogicalChild(editor);
        session.TextStyler = null;
        session.TextSelectionStyle = null;
        session.TextSelectionIsRange = false;
        session.CommitText(text, runs, id);
        Focus();
    }

    /// <summary>Ends text editing and keeps the text (Esc, Ctrl+Return, Enter on the keypad, a click elsewhere).</summary>
    public void EndTextEditing()
    {
        if (Session is not { EditingText: not null } session) return;
        session.EditingText = null;
        UpdateTextEditor();
    }
}
