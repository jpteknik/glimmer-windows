// Ported from App/CanvasView.swift (TextLayerEditor, CanvasNSView.editorText/editorAttributes/styleRuns/style(from:),
// the TextStyler extension and the NSTextViewDelegate). The Mac types into an NSTextView laid over the text layer; here
// it is a RichTextBox: every stretch of the layer becomes a Run in its font, size and colour, and the Runs are read back
// into the layer's string and style runs when editing ends. IME composition, the candidate window and undo are the
// RichTextBox's own.
//
// Sizes: the editor is built at the zoom it opened at (FontSize = document size × that scale). Zooming while typing
// scales the whole editor instead of every font (CanvasSurface.Text.cs), so the undo history is never touched.
// Fonts and colours: the layer stores PostScript names and exact sRGB values, the RichTextBox only family/weight/style
// and 8-bit colours. Every face and colour the editor puts in is remembered, so what comes back is exactly what went
// in; a face or colour that never went in (none should) falls back to the layer's own.
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Glimmer.Core;
using Glimmer.Editor;
using TextRange = Glimmer.Core.TextRange;
using TextAlignment = Glimmer.Core.TextAlignment;

namespace Glimmer.App.Controls.Canvas;

internal sealed class TextLayerEditor : RichTextBox, ITextStyler
{
    private readonly record struct Face(string Family, int Weight, bool Italic, int Width);

    private readonly Dictionary<Face, string> faces = new();
    private readonly Dictionary<Color, SRGBColor> colors = new();
    private readonly List<double> sizes = [];
    private readonly TextStyle baseStyle;

    /// <summary>DIPs per document pixel the fonts were built at.</summary>
    public double Scale { get; }
    /// <summary>Paragraph text: the box width in the editor's own units; null for point text, which grows with its lines.</summary>
    public double? BoxWidth { get; set; }

    /// <summary>The text or its styles changed (the canvas refits the editor and tells the options bar).</summary>
    public event EventHandler? ContentChanged;
    public event EventHandler? SelectionStyleChanged;
    /// <summary>Esc, Ctrl+Return or Enter on the numeric keypad: editing ends (and the text is kept).</summary>
    public event EventHandler? EndRequested;

    /// <summary>Set by the canvas from the window's key message: the Enter being handled came from the numeric keypad.</summary>
    public Func<bool> KeypadEnter { get; set; } = () => false;

    public TextLayerEditor(TextLayer text, double scale)
    {
        Scale = scale;
        baseStyle = text.BaseStyle;
        Template = PlainTemplate();
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        FocusVisualStyle = null;
        AcceptsReturn = true;
        AcceptsTab = true;
        IsUndoEnabled = true;
        UndoLimit = 200;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        SpellCheck.SetIsEnabled(this, false);
        // The canvas turns the input method off so single-key shortcuts work; typing needs it back on.
        InputMethod.SetIsInputMethodEnabled(this, true);
        CaretBrush = new SolidColorBrush(ColorOf(text.Color));
        SelectionBrush = new SolidColorBrush(Theme.Accent);
        SelectionOpacity = 0.35;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        Document = Build(text);
        DataObject.AddPastingHandler(this, OnPasting);
        foreach (var command in new[]
                 {
                     EditingCommands.ToggleItalic, EditingCommands.ToggleUnderline, EditingCommands.IncreaseFontSize, EditingCommands.DecreaseFontSize,
                     EditingCommands.AlignLeft, EditingCommands.AlignCenter, EditingCommands.AlignRight, EditingCommands.AlignJustify,
                     EditingCommands.ToggleSubscript, EditingCommands.ToggleSuperscript, EditingCommands.ToggleBullets, EditingCommands.ToggleNumbering,
                     EditingCommands.IncreaseIndentation, EditingCommands.DecreaseIndentation,
                 })
        {
            // A text layer has no underline, per-paragraph alignment, lists or indents: formatting the layer can't keep is
            // never applied (alignment is the layer's, in the options bar).
            CommandBindings.Add(new CommandBinding(command, (_, e) => e.Handled = true, (_, e) => { e.CanExecute = false; e.Handled = true; }));
        }
        CommandBindings.Add(new CommandBinding(EditingCommands.ToggleBold, (_, e) => { ToggleBold(); e.Handled = true; }));
        // Shift+Return would insert a line break inside the paragraph; the layer only knows "\n", so make it a paragraph.
        CommandBindings.Add(new CommandBinding(EditingCommands.EnterLineBreak, (_, e) =>
        {
            EditingCommands.EnterParagraphBreak.Execute(null, this);
            e.Handled = true;
        }));
        TextChanged += (_, _) => ContentChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged += (_, _) => SelectionStyleChanged?.Invoke(this, EventArgs.Empty);
    }

    private static ControlTemplate PlainTemplate()
    {
        // Only the content host: no border, focus underline or padding from the theme, so the glyphs sit where the
        // layer's pixels were.
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.FocusableProperty, false);
        host.SetValue(ScrollViewer.PaddingProperty, new Thickness(0));
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        return new ControlTemplate(typeof(RichTextBox)) { VisualTree = host };
    }

    // ------------------------------------------------------------------------------------------------- building

    private FlowDocument Build(TextLayer text)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), LineStackingStrategy = LineStackingStrategy.MaxHeight };
        Apply(doc, baseStyle);
        var alignment = Alignment(text.Alignment);
        var paragraph = new Paragraph { Margin = new Thickness(0), TextAlignment = alignment };
        doc.Blocks.Add(paragraph);
        foreach (var (range, style) in text.ResolvedRuns())
        {
            var piece = text.String.Substring(range.Location, range.Length);
            var lines = piece.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    paragraph = new Paragraph { Margin = new Thickness(0), TextAlignment = alignment };
                    doc.Blocks.Add(paragraph);
                }
                if (lines[i].Length == 0) continue;
                var run = new Run(lines[i]);
                Apply(run, style);
                paragraph.Inlines.Add(run);
            }
        }
        return doc;
    }

    public void SetAlignment(TextAlignment alignment)
    {
        var value = Alignment(alignment);
        foreach (var block in Document.Blocks) if (block.TextAlignment != value) block.TextAlignment = value;
    }

    private static System.Windows.TextAlignment Alignment(TextAlignment a) => a switch
    {
        TextAlignment.Center => System.Windows.TextAlignment.Center,
        TextAlignment.Right => System.Windows.TextAlignment.Right,
        _ => System.Windows.TextAlignment.Left,
    };

    /// <summary>Puts a style on an element (a Run, the document, or the typing position through ApplyPropertyValue).</summary>
    private void Apply(TextElement element, TextStyle style)
    {
        var face = Register(style.FontName);
        element.FontFamily = new FontFamily(face.Family);
        element.FontWeight = FontWeight.FromOpenTypeWeight(face.Weight);
        element.FontStyle = face.Italic ? FontStyles.Italic : FontStyles.Normal;
        element.FontStretch = FontStretch.FromOpenTypeStretch(face.Width);
        element.FontSize = Math.Max(style.Size, 1) * Scale;
        element.Foreground = new SolidColorBrush(Register(style.Color));
        RegisterSize(style.Size);
    }

    private void Apply(FlowDocument doc, TextStyle style)
    {
        var face = Register(style.FontName);
        doc.FontFamily = new FontFamily(face.Family);
        doc.FontWeight = FontWeight.FromOpenTypeWeight(face.Weight);
        doc.FontStyle = face.Italic ? FontStyles.Italic : FontStyles.Normal;
        doc.FontStretch = FontStretch.FromOpenTypeStretch(face.Width);
        doc.FontSize = Math.Max(style.Size, 1) * Scale;
        doc.Foreground = new SolidColorBrush(Register(style.Color));
        RegisterSize(style.Size);
    }

    private void Apply(System.Windows.Documents.TextRange range, string? fontName, double? size, SRGBColor? color)
    {
        if (fontName is not null)
        {
            var face = Register(fontName);
            range.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily(face.Family));
            range.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeight.FromOpenTypeWeight(face.Weight));
            range.ApplyPropertyValue(TextElement.FontStyleProperty, face.Italic ? FontStyles.Italic : FontStyles.Normal);
            range.ApplyPropertyValue(TextElement.FontStretchProperty, FontStretch.FromOpenTypeStretch(face.Width));
        }
        if (size is { } s)
        {
            RegisterSize(Math.Max(s, 1));
            range.ApplyPropertyValue(TextElement.FontSizeProperty, Math.Max(s, 1) * Scale);
        }
        if (color is { } c) range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(Register(c)));
    }

    private Face Register(string postScriptName)
    {
        var r = FontResolver.Resolve(postScriptName);
        var face = new Face(r.Family, Math.Clamp(r.Weight, 1, 999), r.Italic, Math.Clamp(r.Width, 1, 9));
        // The first name for a face wins: the layer's own names go in first, so opening and closing the editor never
        // renames a font, even where two names fall back to the same installed face.
        faces.TryAdd(face, postScriptName);
        return face;
    }

    private Color Register(SRGBColor c)
    {
        var color = ColorOf(c);
        colors.TryAdd(color, c);
        return color;
    }

    private void RegisterSize(double size)
    {
        if (!sizes.Any(s => Math.Abs(s - size) < 1e-9)) sizes.Add(size);
    }

    private static Color ColorOf(SRGBColor c) =>
        Color.FromRgb((byte)Math.Round(Math.Clamp(c.R, 0, 1) * 255), (byte)Math.Round(Math.Clamp(c.G, 0, 1) * 255), (byte)Math.Round(Math.Clamp(c.B, 0, 1) * 255));

    // ------------------------------------------------------------------------------------------------- reading back

    /// <summary>The style an element is drawn with, in document pixels.</summary>
    private TextStyle StyleOf(TextElement e) => StyleOf(e.FontFamily, e.FontWeight, e.FontStyle, e.FontStretch, e.FontSize, e.Foreground);

    private TextStyle StyleOf(object family, object weight, object style, object stretch, object size, object foreground)
    {
        string fontName = baseStyle.FontName;
        if (family is FontFamily f && weight is FontWeight w && style is FontStyle s && stretch is FontStretch st)
        {
            var key = new Face(f.Source, w.ToOpenTypeWeight(), s != FontStyles.Normal, st.ToOpenTypeStretch());
            if (faces.TryGetValue(key, out var name)) fontName = name;
        }
        double points = baseStyle.Size;
        if (size is double px)
        {
            // Zoom makes sizes like 39.99999 (CanvasNSView.style(from:)): a size that went in comes back exactly.
            double doc = px / Math.Max(Scale, 1e-9);
            points = sizes.FirstOrDefault(k => Math.Abs(k - doc) < 0.006, Math.Round(doc * 100) / 100);
        }
        var color = baseStyle.Color;
        if (foreground is SolidColorBrush b)
            color = colors.TryGetValue(Color.FromRgb(b.Color.R, b.Color.G, b.Color.B), out var exact) ? exact : new SRGBColor(b.Color.R / 255.0, b.Color.G / 255.0, b.Color.B / 255.0);
        return new TextStyle(fontName, points, color);
    }

    /// <summary>The editor's text ("\n" between lines) and the style of every stretch, as TextLayer.SetStyledText takes them.</summary>
    public (string Text, IReadOnlyList<(TextRange Range, TextStyle Style)> Runs) Contents()
    {
        var text = new StringBuilder();
        var runs = new List<(TextRange, TextStyle)>();
        void Append(string s, TextStyle style)
        {
            if (s.Length == 0) return;
            if (runs.Count > 0 && runs[^1].Item2 == style)
                runs[^1] = (new TextRange(runs[^1].Item1.Location, runs[^1].Item1.Length + s.Length), style);
            else
                runs.Add((new TextRange(text.Length, s.Length), style));
            text.Append(s);
        }
        void Walk(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run: Append(run.Text, StyleOf(run)); break;
                    case LineBreak br: Append("\n", StyleOf(br)); break;
                    case Span span: Walk(span.Inlines); break;
                }
            }
        }
        bool first = true;
        foreach (var block in Document.Blocks)
        {
            if (block is not Paragraph p) continue;
            // The line break between paragraphs takes the style of the text before it, as the Mac's storage has it.
            if (!first) Append("\n", runs.Count > 0 ? runs[^1].Item2 : baseStyle);
            first = false;
            Walk(p.Inlines);
        }
        return (text.ToString(), runs);
    }

    /// <summary>The style at the caret, or of the first selected character (CanvasNSView.updateTextSelectionStyle).</summary>
    public TextStyle SelectionStyle()
    {
        var sel = Selection;
        if (Document.Blocks.Count == 0 || new System.Windows.Documents.TextRange(Document.ContentStart, Document.ContentEnd).IsEmpty)
            return StyleOf(Document);
        System.Windows.Documents.TextRange probe = sel.IsEmpty
            ? sel
            : new System.Windows.Documents.TextRange(sel.Start, sel.Start.GetNextInsertionPosition(LogicalDirection.Forward) ?? sel.Start);
        object V(DependencyProperty p) => probe.GetPropertyValue(p);
        return StyleOf(V(TextElement.FontFamilyProperty), V(TextElement.FontWeightProperty), V(TextElement.FontStyleProperty),
                       V(TextElement.FontStretchProperty), V(TextElement.FontSizeProperty), V(TextElement.ForegroundProperty));
    }

    private TextStyle StyleOf(FlowDocument d) => StyleOf(d.FontFamily, d.FontWeight, d.FontStyle, d.FontStretch, d.FontSize, d.Foreground);

    // ------------------------------------------------------------------------------------------------- styling

    void ITextStyler.Style(bool selectionOnly, string? fontName, double? size, SRGBColor? color) => Restyle(selectionOnly, fontName, size, color);

    /// <summary>ITextStyler.Style: restyles the selected words (or all of them), as one undo step in the editor.</summary>
    public void Restyle(bool selectionOnly, string? fontName, double? size, SRGBColor? color)
    {
        if (fontName is null && size is null && color is null) return;
        var range = selectionOnly ? Selection : new System.Windows.Documents.TextRange(Document.ContentStart, Document.ContentEnd);
        BeginChange();
        try
        {
            if (!range.IsEmpty) Apply(range, fontName, size, color);
            // What new typing looks like: at the caret when nothing is selected, and everywhere for the whole text.
            if (!selectionOnly || range.IsEmpty) Apply(Selection, fontName, size, color);
            if (!selectionOnly)
            {
                var s = StyleOf(Document);
                Apply(Document, new TextStyle(fontName ?? s.FontName, size ?? s.Size, color ?? s.Color));
            }
        }
        finally
        {
            EndChange();
        }
        ContentChanged?.Invoke(this, EventArgs.Empty);
        SelectionStyleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ctrl+B: the font's bold (or regular) face, when the family has one.</summary>
    private void ToggleBold()
    {
        var current = SelectionStyle().FontName;
        if (FontResolver.WithBold(current, !FontResolver.IsBold(current)) is { } other) Restyle(true, other, null, null);
    }

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        // Pasted or dropped text takes the style where it lands (TextLayerEditor.paste: pasteAsPlainText).
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText, true) is string text)
        {
            e.DataObject = new DataObject(DataFormats.UnicodeText, text.Replace("\r\n", "\n").Replace('\r', '\n'));
            e.FormatToApply = DataFormats.UnicodeText;
        }
        else
        {
            e.CancelCommand();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape || (e.Key == Key.Enter && (ctrl || KeypadEnter())))
        {
            e.Handled = true;
            EndRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // ------------------------------------------------------------------------------------------------- measuring

    /// <summary>Sizes the editor around its text (CanvasNSView.fitEditor): point text grows with its longest line plus a
    /// little room for the caret, paragraph text keeps its box width; the height follows the lines.</summary>
    public void Fit()
    {
        double fontSize = Math.Max(Document.FontSize, 1);
        if (BoxWidth is { } box)
        {
            Document.PageWidth = Math.Max(box, 1);
            Width = Math.Max(box, 1);
        }
        else
        {
            Document.PageWidth = 1_000_000;     // no wrapping while the lines are measured
            Width = 1_000_000;
            UpdateLayout();
            double widest = 0;
            foreach (var block in Document.Blocks)
            {
                var start = block.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                var end = block.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                if (!start.IsEmpty && !end.IsEmpty) widest = Math.Max(widest, end.Right - start.Left);
            }
            double width = Math.Max(widest + fontSize, fontSize * 2);
            Document.PageWidth = width;
            Width = width;
        }
        UpdateLayout();
        var last = Document.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        double used = last.IsEmpty ? fontSize * 1.3 : last.Bottom;
        Height = Math.Max(used, fontSize * 1.3) + 4;
    }
}
