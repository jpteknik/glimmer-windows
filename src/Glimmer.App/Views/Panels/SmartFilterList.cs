// Ported from App/LayersPanel.swift (LayerRow.smartFilterList). The filters of a smart object under its row: each can
// be hidden or shown, edited (the button, a double-click, or the context menu) or deleted (context menu).
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Core;

namespace Glimmer.App.Views.Panels;

public sealed class SmartFilterList : StackPanel
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row), typeof(LayerRowItem), typeof(SmartFilterList),
        new PropertyMetadata(null, (d, _) => ((SmartFilterList)d).Build()));

    public LayerRowItem? Row { get => (LayerRowItem?)GetValue(RowProperty); set => SetValue(RowProperty, value); }

    private void Build()
    {
        Children.Clear();
        if (Row is not { SmartFilters.Count: > 0 } row)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        Margin = new Thickness(64, 0, 0, 2);
        var session = row.Session;
        var layerId = row.Id;
        Children.Add(new TextBlock { Text = Loc.T("Smart Filters"), Style = (Style)FindResource("Glimmer.Text.Caption") });
        for (int index = 0; index < row.SmartFilters.Count; index++)
        {
            int i = index;
            var filter = row.SmartFilters[i];
            var title = Loc.T(filter.Settings.Kind.Title());
            var line = new DockPanel { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent };
            var eye = new Button
            {
                Style = (Style)FindResource("Glimmer.IconButton"), Padding = new Thickness(2),
                Content = new Icon { Symbol = filter.IsEnabled ? "eye" : "eye.slash", Size = 11,
                                     Foreground = (Brush)FindResource(filter.IsEnabled ? "Glimmer.MutedBrush" : "Glimmer.TertiaryBrush") },
            };
            AutomationProperties.SetName(eye, Loc.F("{0}: {1}", Loc.T(filter.IsEnabled ? "Hide" : "Show"), title));
            eye.Click += (_, _) => session.SetSmartFilterEnabled(layerId, i, !filter.IsEnabled);
            DockPanel.SetDock(eye, Dock.Left);
            var edit = new Button
            {
                Style = (Style)FindResource("Glimmer.IconButton"), Padding = new Thickness(2), ToolTip = Loc.T("Edit the filter settings"),
                Content = new Icon { Symbol = "slider.horizontal.3", Size = 11 },
            };
            AutomationProperties.SetName(edit, Loc.F("{0}: {1}", Loc.T("Edit…"), title));
            edit.Click += (_, _) => session.EditSmartFilter(layerId, i);
            DockPanel.SetDock(edit, Dock.Right);
            line.Children.Add(eye);
            line.Children.Add(edit);
            line.Children.Add(new TextBlock
            {
                Text = title, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5,
                Foreground = (Brush)FindResource(filter.IsEnabled ? "Glimmer.InkBrush" : "Glimmer.TertiaryBrush"),
            });
            line.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2) return;
                session.EditSmartFilter(layerId, i);
                e.Handled = true;
            };
            var menu = new ContextMenu();
            var editItem = new MenuItem { Header = Loc.T("Edit…") };
            editItem.Click += (_, _) => session.EditSmartFilter(layerId, i);
            var deleteItem = new MenuItem { Header = Loc.T("Delete Smart Filter") };
            deleteItem.Click += (_, _) => session.DeleteSmartFilter(layerId, i);
            menu.Items.Add(editItem);
            menu.Items.Add(deleteItem);
            line.ContextMenu = menu;
            AutomationProperties.SetName(line, title);
            Children.Add(line);
        }
    }
}
