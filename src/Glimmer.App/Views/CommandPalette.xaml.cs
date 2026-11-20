// Ported from App/CommandPalette.swift (CommandPalette).
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glimmer.Editor;

namespace Glimmer.App.Views;

public partial class CommandPalette : UserControl
{
    public CommandPalette()
    {
        InitializeComponent();
        // The canvas holds the keyboard; take it once the field is on screen.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            Query.Text = "";
            Update();
            Dispatcher.BeginInvoke(() => Query.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private IEditorSession? Session => (DataContext as DocumentChrome)?.Session;

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => Update();

    private void Update()
    {
        if (Session is not { } s) return;
        var rows = Palette.Rows(Query.Text, s);
        Rows.ItemsSource = rows;
        Rows.SelectedIndex = rows.Count > 0 ? 0 : -1;
        bool any = rows.Count > 0;
        Rows.Visibility = Divider.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Hint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnQueryKey(object sender, KeyEventArgs e)
    {
        var count = Rows.Items.Count;
        switch (e.Key)
        {
            case Key.Down: Rows.SelectedIndex = Math.Min(Rows.SelectedIndex + 1, Math.Max(count - 1, 0)); Rows.ScrollIntoView(Rows.SelectedItem); break;
            case Key.Up: Rows.SelectedIndex = Math.Max(Rows.SelectedIndex - 1, 0); Rows.ScrollIntoView(Rows.SelectedItem); break;
            case Key.Enter: RunSelected(); break;
            case Key.Escape: if (Session is { } s) s.IsPaletteOpen = false; break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PaletteRow row) { Rows.SelectedItem = row; RunSelected(); }
    }

    private void RunSelected()
    {
        if (Session is not { } s || Rows.SelectedItem is not PaletteRow row) return;
        s.IsPaletteOpen = false;
        Palette.Run(row, s);
    }
}
