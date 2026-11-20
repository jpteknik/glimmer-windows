// Ported from App/PropertiesPanel.swift (ParameterSlider) and the options bar's slider (ToolOptionsBar.swift
// slider(_:_:_:percent:display:)).
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Glimmer.App.Localization;

namespace Glimmer.App.Controls;

/// <summary>A labelled slider that commits one undo step per drag: the label and the value above it, the slider
/// below (Layout Stacked: the panels) or in a row (Layout Inline: the options bar, 80 wide, value after it).</summary>
public sealed class ParameterSlider : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ParameterSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ParameterSlider)d).Sync()));

    private readonly TextBlock label = new(), shown = new();
    private readonly Slider slider = new();
    private bool syncing;

    /// <summary>Raised when a drag ends or a key changes the value (Swift: onEditingChanged(false) → commit()).</summary>
    public event EventHandler? Committed;

    public ParameterSlider(string labelKey, double min, double max, double step = 0.01, Func<double, string>? format = null, bool inline = false)
    {
        Format = format ?? (v => v.ToString(step >= 1 ? "0" : "0.00", System.Globalization.CultureInfo.CurrentCulture));
        Step = step;
        label.Text = Loc.T(labelKey);
        label.FontSize = 12.5;
        shown.FontSize = 12.5;
        shown.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Glimmer.MutedBrush");
        slider.Minimum = min;
        slider.Maximum = max;
        slider.SmallChange = step;
        slider.LargeChange = (max - min) / 10;
        slider.IsMoveToPointEnabled = true;
        slider.Style = Application.Current.TryFindResource("Glimmer.Compact.Slider") as Style;
        // The name and number sit beside the slider; a screen reader gets them on it.
        AutomationProperties.SetName(slider, label.Text);
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            Value = Math.Round(e.NewValue / Step) * Step;
        };
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => Committed?.Invoke(this, EventArgs.Empty)));
        slider.PreviewMouseUp += (_, _) => Committed?.Invoke(this, EventArgs.Empty);
        slider.KeyUp += (_, e) => { if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) Committed?.Invoke(this, EventArgs.Empty); };

        if (inline)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            label.Foreground = shown.Foreground;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 6, 0);
            shown.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Glimmer.InkBrush");
            shown.VerticalAlignment = VerticalAlignment.Center;
            shown.Margin = new Thickness(6, 0, 0, 0);
            SetColumn(slider, 1);
            SetColumn(shown, 2);
        }
        else
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SetColumn(shown, 1);
            SetRow(slider, 1);
            SetColumnSpan(slider, 2);
        }
        Children.Add(label);
        Children.Add(slider);
        Children.Add(shown);
        Sync();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Step { get; }
    public Func<double, string> Format { get; }

    private void Sync()
    {
        syncing = true;
        slider.Value = Value;
        shown.Text = Format(Value);
        AutomationProperties.SetItemStatus(slider, shown.Text);
        syncing = false;
    }
}
