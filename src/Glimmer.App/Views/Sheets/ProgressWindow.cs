// IDocumentHost.ShowProgress: a small window while something slow runs (developing a RAW file at open). Disposing it
// closes it; it has no buttons, as the Mac's progress panel has none.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Glimmer.App.Controls;

namespace Glimmer.App.Views.Sheets;

public sealed class ProgressWindow : SheetWindow, IDisposable
{
    public ProgressWindow(string title, string message) : base("Glimmer", 380)
    {
        Title = title;
        WindowStyle = WindowStyle.ToolWindow;
        var spinner = new Spinner { Width = 18, Height = 18, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(spinner, message);
        Body.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Glimmer.Text.Headline"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        Body.Children.Add(new DockPanel { Children = { spinner, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } } });
    }

    public void Dispose()
    {
        if (IsLoaded || IsVisible) Close();
    }
}
