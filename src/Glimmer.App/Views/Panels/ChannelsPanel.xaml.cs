// Ported from App/ChannelsPanel.swift.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Editor;

namespace Glimmer.App.Views.Panels;

public partial class ChannelsPanel : UserControl
{
    /// <summary>id, name, colour, and the bits of the channel mask the row stands for.</summary>
    private static readonly (int Id, string Name, Color Color, int Bits)[] Channels =
    [
        (0, "RGB", Theme.Ink, 7), (1, "Red", Color.FromRgb(0xff, 0x45, 0x3a), 1), (2, "Green", Color.FromRgb(0x30, 0xd1, 0x58), 2),
        (3, "Blue", Color.FromRgb(0x0a, 0x84, 0xff), 4), (4, "Alpha", Colors.White, 8),
    ];

    private IEditorSession? session;

    public ChannelsPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (session is not null) session.PropertyChanged -= OnChanged;
            session = (e.NewValue as DocumentChrome)?.Session;
            if (session is not null) session.PropertyChanged += OnChanged;
            Build();
        };
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IEditorSession.ChannelMask)) Build();
    }

    private void Build()
    {
        Rows.Children.Clear();
        if (session is null) return;
        foreach (var row in Channels)
        {
            bool on = (session.ChannelMask & row.Bits) == row.Bits;
            var name = Loc.T(row.Name);
            var eye = new Button { Style = (Style)FindResource("Glimmer.IconButton"), Width = 26,
                                   Content = new Icon { Symbol = on ? "eye" : "eye.slash", Size = 13, Foreground = (Brush)FindResource(on ? "Glimmer.InkBrush" : "Glimmer.TertiaryBrush") },
                                   ToolTip = Loc.T(on ? "Hide this channel" : "Show this channel") };
            AutomationProperties.SetName(eye, Loc.F(on ? "Hide {0}" : "Show {0}", name));
            eye.Click += (_, _) => session.ChannelMask = on ? session.ChannelMask & ~row.Bits : session.ChannelMask | row.Bits;
            var load = new Button { Style = (Style)FindResource("Glimmer.IconButton"), Content = new Icon { Symbol = "selection.pin.in.out", Size = 13 },
                                    ToolTip = Loc.T(row.Id == 0 ? "Load luminosity as selection" : "Load this channel as selection") };
            AutomationProperties.SetName(load, row.Id == 0 ? Loc.T("Load Luminosity as Selection") : Loc.F("Load {0} as Selection", name));
            int id = row.Id;
            load.Click += (_, _) => session.LoadChannelAsSelection(id);
            var line = new DockPanel { Height = 30, Margin = new Thickness(0, 0, 0, 2) };
            DockPanel.SetDock(eye, Dock.Left);
            DockPanel.SetDock(load, Dock.Right);
            line.Children.Add(eye);
            line.Children.Add(load);
            line.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                Children =
                {
                    new Icon { Symbol = row.Id == 0 ? "circle.grid.cross" : "circle.fill", Size = 12, Foreground = new SolidColorBrush(row.Color), Margin = new Thickness(0, 0, 8, 0) },
                    new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center },
                },
            });
            var frame = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 0, 4, 0), Child = line,
                                     Background = on ? (Brush)FindResource("Glimmer.RaisedBrush") : Brushes.Transparent, Cursor = Cursors.Hand };
            int bits = row.Bits;
            // Show this channel alone; RGB is the normal view.
            frame.MouseLeftButtonUp += (_, _) => session.ChannelMask = id == 0 ? 15 : bits;
            Rows.Children.Add(frame);
        }
        ShowAll.Visibility = session.ChannelMask != 15 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShowAll(object sender, RoutedEventArgs e) { if (session is not null) session.ChannelMask = 15; }
}
