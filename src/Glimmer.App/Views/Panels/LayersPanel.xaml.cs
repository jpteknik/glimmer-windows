// Ported from App/LayersPanel.swift (LayersPanel, LayerPropertiesBar, LayerRow).
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views.Panels;

public partial class LayersPanel : UserControl
{
    public static readonly Brush CheckerBrush = Checkerboard.Brush(5);
    public static readonly FuncConverter FilterTitle = new(v => v is FilterKind k ? Loc.T(k.Title()) : "");

    private IEditorSession? session;
    private bool refreshing;
    private Point dragStart;
    private LayerRowItem? dragRow;

    public LayersPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (session is not null) session.PropertyChanged -= OnSessionChanged;
            session = (e.NewValue as DocumentChrome)?.Session;
            if (session is not null) session.PropertyChanged += OnSessionChanged;
            Refresh();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IEditorSession.State) or nameof(IEditorSession.Thumbnails) or nameof(IEditorSession.MaskThumbnails)
            or nameof(IEditorSession.EditingMask) or nameof(IEditorSession.ActiveLayer) or nameof(IEditorSession.RenameRequest))
            Refresh();
    }

    private void Refresh()
    {
        if (session is null || !IsVisible) return;
        refreshing = true;
        var rows = LayerRowItem.Rows(session);
        List.ItemsSource = rows;
        List.SelectedItem = rows.FirstOrDefault(r => r.IsActive);
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var layer = session.ActiveLayer;
        // Properties of the active layer, when it has any (adjustment, fill, mask).
        bool props = layer is not null && PropertiesPanel.HasProperties(layer);
        PropertiesScroller.Visibility = PropertiesDivider.Visibility = props ? Visibility.Visible : Visibility.Collapsed;
        if (props) Properties.Show(session, layer!);
        RefreshPropertiesBar(layer);
        foreach (var b in Footer.Children.OfType<Panel>().SelectMany(p => p.Children.OfType<Button>()))
            if (b.Tag is string raw) b.IsEnabled = session.CanPerform(new CommandID(raw));
        refreshing = false;
    }

    // ------------------------------------------------------------------------------------------------- properties bar

    private void RefreshPropertiesBar(Layer? layer)
    {
        PropertiesBar.IsEnabled = layer is not null;
        BlendModeBox.Items.Clear();
        if (layer?.IsGroup == true)
        {
            BlendModeBox.Items.Add(new ComboBoxItem { Content = Loc.T(BlendMode.PassThrough.Title()), Tag = BlendMode.PassThrough });
            BlendModeBox.Items.Add(new Separator());
        }
        for (int g = 0; g < BlendMode.MenuGroups.Length; g++)
        {
            if (g > 0) BlendModeBox.Items.Add(new Separator());
            foreach (var mode in BlendMode.MenuGroups[g]) BlendModeBox.Items.Add(new ComboBoxItem { Content = Loc.T(mode.Title()), Tag = mode });
        }
        var current = layer?.BlendMode ?? BlendMode.Normal;
        BlendModeBox.SelectedItem = BlendModeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (BlendMode)i.Tag == current);
        double percent = Math.Round((layer?.Opacity ?? 1) * 100);
        OpacitySlider.Value = percent;
        OpacityText.Text = $"{percent:0} %";
        System.Windows.Automation.AutomationProperties.SetItemStatus(OpacitySlider, OpacityText.Text);
    }

    private void OnBlendModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshing && BlendModeBox.SelectedItem is ComboBoxItem { Tag: BlendMode mode }) session?.SetBlendMode(mode);
    }

    /// <summary>Live opacity change during a slider drag; one undo step on release.</summary>
    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (refreshing || session is null) return;
        session.SetOpacity(e.NewValue / 100);
        OpacityText.Text = $"{Math.Round(e.NewValue):0} %";
    }

    private void OnOpacityCommitted(object sender, RoutedEventArgs e) => session?.CommitLiveEdit();

    // ------------------------------------------------------------------------------------------------- footer

    private void OnCommand(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string raw) session?.Perform(new CommandID(raw));
    }

    private void OnAdjustmentMenu(object sender, RoutedEventArgs e)
    {
        if (session is null) return;
        var menu = new ContextMenu { PlacementTarget = AdjustmentMenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var kind in Enum.GetValues<AdjustmentKind>())
            menu.Items.Add(Item(kind.Title(), CommandID.NewAdjustment(kind), kind.SymbolName()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Solid Color Fill", CommandID.NewSolidFill));
        menu.Items.Add(Item("Gradient Fill", CommandID.NewGradientFill));
        menu.Items.Add(Item("Pattern Fill", CommandID.NewPatternFill));
        menu.IsOpen = true;
    }

    private MenuItem Item(string title, CommandID id, string? symbol = null)
    {
        var item = new MenuItem { Header = Loc.T(title), Icon = symbol is null ? null : new Icon { Symbol = symbol, Size = 13 } };
        item.Click += (_, _) => session?.Perform(id);
        return item;
    }

    // ------------------------------------------------------------------------------------------------- rows

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshing && List.SelectedItem is LayerRowItem row) session?.SetActiveLayer(row.Id);
    }

    /// <summary>Return renames the selected layer, as Explorer does with F2 (both work).</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.F2 && session?.State.ActiveLayerID is { } id)
        {
            session.RenameRequest = id;
            e.Handled = true;
        }
        else if (e.Key == Key.Apps && List.SelectedItem is LayerRowItem row)
        {
            OpenContextMenu(row, List.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement);
            e.Handled = true;
        }
    }

    private void OnToggleVisibility(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LayerRowItem row) session?.SetVisibility(row.Id, !row.IsVisible);
    }

    private void OnToggleExpanded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LayerRowItem row) session?.SetExpanded(row.Id, !row.IsExpanded);
    }

    private void OnLayerThumbnail(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LayerRowItem row || session is null) return;
        session.SetActiveLayer(row.Id);
        session.EditingMask = false;
    }

    private void OnMaskThumbnail(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LayerRowItem row || session is null) return;
        session.SetActiveLayer(row.Id);
        session.EditingMask = true;
    }

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        base.OnContextMenuOpening(e);
        if ((e.OriginalSource as FrameworkElement)?.DataContext is LayerRowItem row)
        {
            OpenContextMenu(row, e.OriginalSource as FrameworkElement);
            e.Handled = true;
        }
    }

    private void OpenContextMenu(LayerRowItem row, FrameworkElement? target)
    {
        if (session is null) return;
        var layer = row.Layer;
        void Activate() => session.SetActiveLayer(layer.Id);
        MenuItem Do(string title, CommandID id, bool enabled = true)
        {
            var item = new MenuItem { Header = Loc.T(title), IsEnabled = enabled };
            item.Click += (_, _) => { Activate(); session.Perform(id); };
            return item;
        }
        var rename = new MenuItem { Header = Loc.T("Rename…") };
        rename.Click += (_, _) => session.RenameRequest = layer.Id;
        var menu = new ContextMenu { PlacementTarget = target };
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        menu.Items.Add(Do("Duplicate Layer", CommandID.DuplicateLayer));
        menu.Items.Add(Do(layer.IsGroup ? "Merge Group" : "Rasterize Layer", CommandID.RasterizeLayer, !(layer.IsRaster || layer.IsAdjustment)));
        menu.Items.Add(Do("Convert to Smart Object", CommandID.ConvertToSmartObject, layer.IsRaster || layer.IsText));
        menu.Items.Add(new Separator());
        menu.Items.Add(Do(layer.IsClipped ? "Release Clipping Mask" : "Create Clipping Mask", CommandID.ToggleClipping));
        if (layer.Mask is null) menu.Items.Add(Do("Add Layer Mask", CommandID.AddMaskRevealAll));
        else
        {
            menu.Items.Add(Do(layer.Mask.IsEnabled ? "Disable Layer Mask" : "Enable Layer Mask", CommandID.ToggleMask));
            menu.Items.Add(Do("Delete Layer Mask", CommandID.DeleteMask));
        }
        menu.Items.Add(Do("Layer Style…", CommandID.LayerStyle));
        menu.Items.Add(new Separator());
        menu.Items.Add(Do("Delete Layer", CommandID.DeleteLayer));
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------------------------------------- reordering

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(this);
        dragRow = (sender as FrameworkElement)?.DataContext as LayerRowItem;
    }

    private void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (dragRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - dragStart;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        var row = dragRow;
        dragRow = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject("Glimmer.Layer", row.Id.ToString()), DragDropEffects.Move);
    }

    /// <summary>Dropped on the upper half of a row: directly above it. On a group's lower half: into the group.
    /// (Rows are top-down: landing before a row means sitting directly above it.)</summary>
    private void OnRowDrop(object sender, DragEventArgs e)
    {
        if (session is null || e.Data.GetData("Glimmer.Layer") is not string raw || !Guid.TryParse(raw, out var id)) return;
        if ((sender as FrameworkElement)?.DataContext is not LayerRowItem target || target.Id == id) return;
        bool upper = e.GetPosition((IInputElement)sender).Y < ((FrameworkElement)sender).ActualHeight / 2;
        if (!upper && target.IsGroup) session.MoveLayerIntoGroup(id, target.Id);
        else if (upper) session.MoveLayer(id, target.Id);
        else
        {
            // Below the row: above the row that follows it (or at the bottom).
            var rows = (List<LayerRowItem>)List.ItemsSource;
            int index = rows.FindIndex(r => r.Id == target.Id);
            session.MoveLayer(id, index + 1 < rows.Count ? rows[index + 1].Id : null);
        }
        e.Handled = true;
    }
}

/// <summary>A layer's name, which turns into a text field to rename it (double-click, Return/F2, or Rename…).
/// Return keeps the new name, Esc cancels, and leaving the field keeps it (LayerRow.startRename/finishRename).</summary>
public sealed class LayerNameField : ContentControl
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(LayerRowItem), typeof(LayerNameField), new PropertyMetadata(null, (d, _) => ((LayerNameField)d).Show()));

    private TextBox? editor;

    public LayerRowItem? Row { get => (LayerRowItem?)GetValue(RowProperty); set => SetValue(RowProperty, value); }

    public LayerNameField() => Focusable = false;

    private IEditorSession? Session => (Window.GetWindow(this) is { } w ? FindSession() : null);

    private IEditorSession? FindSession()
    {
        DependencyObject? d = this;
        while (d is not null)
        {
            if (d is FrameworkElement { DataContext: DocumentChrome chrome }) return chrome.Session;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void Show()
    {
        if (Row is null) return;
        var session = FindSession();
        if (session?.RenameRequest == Row.Id)
        {
            session.RenameRequest = null;
            StartRename();
            return;
        }
        var text = new TextBlock { Text = Row.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        text.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { StartRename(); e.Handled = true; } };
        Content = text;
    }

    private void StartRename()
    {
        if (Row is null) return;
        editor = new TextBox { Text = Row.Name, Style = (Style)FindResource("Glimmer.Compact.TextBox") };
        System.Windows.Automation.AutomationProperties.SetName(editor, Loc.T("Name"));
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        };
        editor.LostKeyboardFocus += (_, _) => Finish(true);
        Content = editor;
        Dispatcher.BeginInvoke(() => { editor?.Focus(); editor?.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Finish(bool keep)
    {
        if (editor is null || Row is null) return;
        var name = editor.Text;
        editor = null;
        if (keep) Session?.Rename(Row.Id, name);
        Show();
    }
}
