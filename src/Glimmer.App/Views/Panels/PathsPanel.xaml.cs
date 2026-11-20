// Ported from App/PathsPanel.swift.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views.Panels;

public partial class PathsPanel : UserControl
{
    private IEditorSession? session;
    private bool refreshing;

    public PathsPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (session is not null) session.PropertyChanged -= OnChanged;
            session = (e.NewValue as DocumentChrome)?.Session;
            if (session is not null) session.PropertyChanged += OnChanged;
            Refresh();
        };
        IsVisibleChanged += (_, _) => Refresh();
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IEditorSession.State) or nameof(IEditorSession.ActivePathID) or nameof(IEditorSession.ActiveLayer)) Refresh();
    }

    private void Refresh()
    {
        if (session is null || !IsVisible) return;
        refreshing = true;
        var paths = session.State.Paths;
        List.ItemsSource = paths;
        List.SelectedItem = paths.FirstOrDefault(p => p.Id == session.ActivePathID);
        List.Visibility = paths.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = paths.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Actions.IsEnabled = session.ActivePathID is not null;
        if (session.ActiveLayer is { VectorMask: { } vector } shape)
        {
            ShapePath.Visibility = Visibility.Visible;
            ShapePathName.Text = Loc.F("{0} Shape Path", shape.Name);
            ShapeEditing.Visibility = session.ActivePathID == vector.Id ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            ShapePath.Visibility = Visibility.Collapsed;
        }
        refreshing = false;
    }

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshing && session is not null) session.ActivePathID = (List.SelectedItem as VectorPath)?.Id;
    }

    private void OnShapePath(object sender, RoutedEventArgs e)
    {
        if (session?.ActiveLayer?.VectorMask is not { } vector) return;
        session.ActivePathID = session.ActivePathID == vector.Id ? null : vector.Id;
        if (session.ActivePathID is not null) session.Tool = ToolID.Pen;
    }

    private void OnFill(object sender, RoutedEventArgs e) { if (session?.ActivePathID is { } id) session.FillPath(id); }
    private void OnStroke(object sender, RoutedEventArgs e) { if (session?.ActivePathID is { } id) session.StrokePath(id); }
    private void OnSelection(object sender, RoutedEventArgs e) { if (session?.ActivePathID is { } id) session.MakeSelectionFromPath(id); }
    private void OnTypeOnPath(object sender, RoutedEventArgs e) { if (session?.ActivePathID is { } id) session.AddTextOnPath(id); }
    private void OnDelete(object sender, RoutedEventArgs e) { if (session?.ActivePathID is { } id) session.DeletePath(id); }

    /// <summary>Double-click a path's name to rename it.</summary>
    private void OnNameClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not TextBlock { DataContext: VectorPath path } text || session is null) return;
        var parent = (Panel)text.Parent;
        var box = new TextBox { Text = path.Name, Style = (Style)FindResource("Glimmer.Compact.TextBox") };
        System.Windows.Automation.AutomationProperties.SetName(box, Loc.T("Name"));
        int index = parent.Children.IndexOf(text);
        parent.Children[index] = box;
        void Done(bool keep)
        {
            if (!parent.Children.Contains(box)) return;
            parent.Children[parent.Children.IndexOf(box)] = text;
            if (keep) session.RenamePath(path.Id, box.Text);
        }
        box.KeyDown += (_, k) => { if (k.Key == Key.Enter) Done(true); else if (k.Key == Key.Escape) Done(false); };
        box.LostKeyboardFocus += (_, _) => Done(true);
        Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
        e.Handled = true;
    }
}
