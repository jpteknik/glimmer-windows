// The Mac's sheets (.sheet over the document window) as Windows dialogs: modal, centred on the window, Enter for the
// default button and Esc for Cancel, and a grouped form like SwiftUI's Form(.formStyle(.grouped)).
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Glimmer.App.Controls;
using Glimmer.App.Localization;

namespace Glimmer.App.Views.Sheets;

public class SheetWindow : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(20) };

    public SheetWindow(string titleKey, double width)
    {
        Title = Loc.T(titleKey);
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("Glimmer.PanelBrush");
        Foreground = (Brush)FindResource("Glimmer.InkBrush");
        FontFamily = (FontFamily)FindResource("Glimmer.Font");
        FontSize = 13;
        UseLayoutRounding = true;
        Content = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 };
    }

    protected TextBlock Headline(string key)
    {
        var t = new TextBlock { Text = Loc.T(key), Style = (Style)FindResource("Glimmer.Text.Headline"), Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetHeadingLevel(t, AutomationHeadingLevel.Level1);
        return t;
    }

    protected TextBlock Secondary(string text) =>
        new() { Text = text, Style = (Style)FindResource("Glimmer.Text.Secondary"), Margin = new Thickness(0, 0, 0, 10) };

    /// <summary>A grouped form section: rounded raised surface with rows separated by hairlines.</summary>
    protected StackPanel Section(params UIElement[] rows)
    {
        var inner = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) inner.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Glimmer.EdgeBrush"), Margin = new Thickness(0, 0, 0, 0) });
            if (rows[i] is FrameworkElement fe) fe.Margin = new Thickness(12, 8, 12, 8);
            inner.Children.Add(rows[i]);
        }
        var frame = new Border { Background = (Brush)FindResource("Glimmer.RaisedBrush"), CornerRadius = new CornerRadius(8), Child = inner, Margin = new Thickness(0, 0, 0, 12) };
        var holder = new StackPanel();
        holder.Children.Add(frame);
        return holder;
    }

    /// <summary>LabeledContent: the label at the left, the control at the right.</summary>
    protected static DockPanel Row(string labelKey, UIElement control)
    {
        var row = new DockPanel();
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        var label = new TextBlock { Text = Loc.T(labelKey), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(label);
        if (control is FrameworkElement fe && AutomationProperties.GetName(fe).Length == 0) AutomationProperties.SetLabeledBy(fe, label);
        return row;
    }

    protected ComboBox Picker<T>(IEnumerable<T> values, Func<T, string> title, T selected, Action<T> changed)
    {
        var box = new ComboBox { Style = (Style)FindResource("Glimmer.Compact.ComboBox"), MinWidth = 160 };
        foreach (var v in values) box.Items.Add(new ComboBoxItem { Content = Loc.T(title(v)), Tag = v });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected));
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: T v }) changed(v); };
        return box;
    }

    protected TextBox NumberField(int value, string nameKey, double width = 90)
    {
        var box = new TextBox { Text = value.ToString(System.Globalization.CultureInfo.CurrentCulture), Width = width, Style = (Style)FindResource("Glimmer.Compact.TextBox"),
                                HorizontalContentAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(box, Loc.T(nameKey));
        return box;
    }

    protected static int? ParseInt(TextBox box) =>
        int.TryParse(box.Text.Replace(" ", "").Replace(" ", ""), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out var v) ? v : null;

    /// <summary>The bottom row: optional extra buttons at the left, Cancel and the default button at the right.</summary>
    protected DockPanel Buttons(string defaultKey, Action ok, Func<bool>? enabled = null, params Button[] leading)
    {
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        // Windows' order: the default button, then Cancel at the far right (the Mac puts the default last).
        var okButton = new Button { Content = Loc.T(defaultKey), IsDefault = true, Style = (Style)FindResource("Glimmer.Compact.AccentButton"), MinWidth = 80 };
        okButton.Click += (_, _) => ok();
        var cancel = new Button { Content = Loc.T("Cancel"), IsCancel = true, Style = (Style)FindResource("Glimmer.Compact.Button"), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        DockPanel.SetDock(okButton, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        row.Children.Add(cancel);
        row.Children.Add(okButton);
        foreach (var b in leading) { DockPanel.SetDock(b, Dock.Left); row.Children.Add(b); }
        if (enabled is not null) Validation = () => okButton.IsEnabled = enabled();
        return row;
    }

    /// <summary>Re-checks whether the default button may be pressed (set by Buttons when it is given a rule).</summary>
    protected Action Validation { get; private set; } = () => { };
}

public enum AlertRole { Default, Cancel, Destructive, Normal }

/// <summary>SwiftUI's .alert: a title, a message and buttons. Returns the index of the button chosen (the cancel
/// button's when the window is closed).</summary>
public sealed class AlertWindow : SheetWindow
{
    private int answer;

    private AlertWindow(string title, string message, (string Text, AlertRole Role)[] buttons) : base("Glimmer", 420)
    {
        Title = title;
        Body.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Glimmer.Text.Headline"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        Body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        answer = Array.FindIndex(buttons, b => b.Role == AlertRole.Cancel);
        // Windows' order: the buttons as given, the default first and Cancel last.
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var (text, role) = buttons[i];
            var b = new Button
            {
                Content = Loc.Maybe(text), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0),
                IsDefault = role == AlertRole.Default, IsCancel = role == AlertRole.Cancel,
                Style = (Style)FindResource(role is AlertRole.Default or AlertRole.Destructive ? "Glimmer.Compact.AccentButton" : "Glimmer.Compact.Button"),
            };
            b.Click += (_, _) => { answer = index; Close(); };
            row.Children.Add(b);
        }
        Body.Children.Add(row);
    }

    public static int Show(Window? owner, string title, string message, (string Text, AlertRole Role)[] buttons)
    {
        var alert = new AlertWindow(title, message, buttons) { Owner = owner };
        alert.ShowDialog();
        return alert.answer;
    }
}
