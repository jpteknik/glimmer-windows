// The Mac's grouped Form (.formStyle(.grouped)) and its LabeledContent rows, used by Settings, the sheets and the
// popovers: sections of rows on a raised, rounded group, a label on the left and its control on the right, an optional
// header above the group and a caption below it. Popover shows a view in a floating panel under a button, as a SwiftUI
// popover does. Every string passed in is an English catalogue key; Verbatim marks one that is shown as given.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Glimmer.App.Localization;

namespace Glimmer.App.Controls;

/// <summary>A text shown exactly as given (a file name, a provider's name, a number), never looked up.</summary>
public readonly record struct Verbatim(string Text)
{
    public override string ToString() => Text;
}

public sealed class FormPanel : StackPanel
{
    public FormPanel()
    {
        Margin = new Thickness(18, 4, 18, 16);
        SetValue(TextBlock.FontSizeProperty, 13.0);
    }

    public FormSection Section(string? header = null, string? footer = null)
    {
        var section = new FormSection(header is null ? null : Loc.T(header), footer is null ? null : Loc.T(footer));
        Children.Add(section);
        return section;
    }

    public FormSection Section(Verbatim header, string? footer = null)
    {
        var section = new FormSection(header.Text, footer is null ? null : Loc.T(footer));
        Children.Add(section);
        return section;
    }
}

public sealed class FormSection : StackPanel
{
    private readonly StackPanel rows = new();
    private readonly TextBlock? footerBlock;

    internal FormSection(string? header, string? footer)
    {
        Margin = new Thickness(0, 10, 0, 4);
        if (header is not null)
        {
            Children.Add(new TextBlock
            {
                Text = header, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 10, 6),
                Foreground = (Brush)Application.Current.FindResource("Glimmer.InkSoftBrush"),
            });
        }
        Children.Add(new Border
        {
            Background = (Brush)Application.Current.FindResource("Glimmer.RaisedBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("Glimmer.EdgeBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = rows,
        });
        footerBlock = new TextBlock
        {
            Text = footer ?? "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 6, 10, 0),
            Style = (Style)Application.Current.FindResource("Glimmer.Text.Caption"),
            Visibility = footer is null ? Visibility.Collapsed : Visibility.Visible,
        };
        Children.Add(footerBlock);
    }

    /// <summary>The caption under the group; null hides it.</summary>
    public string? FooterText
    {
        get => footerBlock?.Visibility == Visibility.Visible ? footerBlock.Text : null;
        set
        {
            if (footerBlock is null) return;
            footerBlock.Text = value ?? "";
            footerBlock.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>A whole-width row.</summary>
    public T Add<T>(T row) where T : UIElement
    {
        if (rows.Children.Count > 0)
            rows.Children.Add(new Border { Height = 1, Margin = new Thickness(12, 0, 0, 0), Background = (Brush)Application.Current.FindResource("Glimmer.EdgeBrush") });
        if (row is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(12, 8, 12, 8);
        rows.Children.Add(row);
        return row;
    }

    /// <summary>LabeledContent: the label on the left, the control on the right; `help` is a caption under the label.</summary>
    public FormRow Row(string label, UIElement control, string? help = null) => Labeled(Loc.T(label), control, help is null ? null : Loc.T(help));

    public FormRow Row(Verbatim label, UIElement control, string? help = null) => Labeled(label.Text, control, help is null ? null : Loc.T(help));

    private FormRow Labeled(string label, UIElement control, string? help)
    {
        var row = new FormRow(label, control, help);
        Add(row);
        return row;
    }

    /// <summary>A Toggle in a form: the label on the left, the switch on the right.</summary>
    public CheckBox Toggle(string label, bool isOn, Action<bool> changed, string? help = null)
    {
        var box = new CheckBox { IsChecked = isOn, Style = (Style)Application.Current.FindResource("Glimmer.Compact.CheckBox"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, Loc.T(label));
        box.Click += (_, _) => changed(box.IsChecked == true);
        Row(label, box, help);
        return box;
    }

    /// <summary>A line of secondary text inside the group.</summary>
    public TextBlock Note(string text, params object?[] args)
    {
        var block = new TextBlock { Text = args.Length == 0 ? Loc.T(text) : Loc.F(text, args), TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.FindResource("Glimmer.Text.Secondary") };
        return Add(block);
    }

    public TextBlock Note(Verbatim text)
    {
        var block = new TextBlock { Text = text.Text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.FindResource("Glimmer.Text.Secondary") };
        return Add(block);
    }

    public void Clear() => rows.Children.Clear();
}

/// <summary>One labelled row of a form.</summary>
public sealed class FormRow : Grid
{
    public FormRow(string label, UIElement control, string? help)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        LabelBlock = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
        text.Children.Add(LabelBlock);
        if (help is not null)
            text.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Style = (Style)Application.Current.FindResource("Glimmer.Text.Caption") });
        Children.Add(text);
        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            if (fe.HorizontalAlignment == HorizontalAlignment.Stretch) fe.HorizontalAlignment = HorizontalAlignment.Right;
            if (AutomationProperties.GetName(fe) is null or "") AutomationProperties.SetName(fe, label);
        }
        SetColumn(control, 1);
        Children.Add(control);
    }

    public TextBlock LabelBlock { get; }
}

/// <summary>A slider with its value written beside it (the Mac's Slider + monospaced Text in an HStack).</summary>
public sealed class ValueSlider : StackPanel
{
    private readonly Slider slider;
    private readonly TextBlock readout;
    private readonly Func<double, string> format;
    private bool setting;

    public event EventHandler<double>? Changed;
    /// <summary>The drag ended (the Mac's onEditingChanged(false)): the moment to record one undo step.</summary>
    public event EventHandler? Committed;

    public ValueSlider(string label, double min, double max, double value, Func<double, string> format, double width = 160, double readoutWidth = 48)
    {
        Orientation = Orientation.Horizontal;
        this.format = format;
        slider = new Slider { Minimum = min, Maximum = max, Width = width, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true,
                              SmallChange = (max - min) / 100, LargeChange = (max - min) / 10 };
        AutomationProperties.SetName(slider, Loc.T(label));
        readout = new TextBlock { Width = readoutWidth, TextAlignment = System.Windows.TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                                  FontFamily = (FontFamily)Application.Current.FindResource("Glimmer.MonoFont") };
        Children.Add(slider);
        Children.Add(readout);
        Value = value;
        slider.ValueChanged += (_, e) =>
        {
            readout.Text = format(e.NewValue);
            AutomationProperties.SetItemStatus(slider, readout.Text);
            if (!setting) Changed?.Invoke(this, e.NewValue);
        };
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => Committed?.Invoke(this, EventArgs.Empty)));
        slider.PreviewMouseUp += (_, _) => { if (!slider.IsMouseCaptureWithin) Committed?.Invoke(this, EventArgs.Empty); };
        slider.KeyUp += (_, _) => Committed?.Invoke(this, EventArgs.Empty);
    }

    public double Value
    {
        get => slider.Value;
        set
        {
            setting = true;
            slider.Value = value;
            readout.Text = format(value);
            setting = false;
        }
    }
}

/// <summary>A floating panel under a control, closed by a click outside it or Esc (SwiftUI's popover).</summary>
public static class Popover
{
    public static Popup Show(FrameworkElement anchor, UIElement content, double width = double.NaN, PlacementMode placement = PlacementMode.Bottom, Action? closed = null)
    {
        var surface = new PanelSurface { Content = content, CornerRadius = new CornerRadius(10), Width = width };
        var popup = new Popup
        {
            Child = surface, PlacementTarget = anchor, Placement = placement, VerticalOffset = placement == PlacementMode.Bottom ? 6 : 0,
            HorizontalOffset = placement == PlacementMode.Right ? 6 : 0, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
        };
        surface.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            popup.IsOpen = false;
            e.Handled = true;
        };
        popup.Closed += (_, _) =>
        {
            closed?.Invoke();
            if (anchor.IsVisible) anchor.Focus();
        };
        popup.Opened += (_, _) => surface.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        popup.IsOpen = true;
        return popup;
    }
}
