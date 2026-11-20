// Ported from App/DocumentView.swift (SidePanel, SidePanelChrome).
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Glimmer.Editor;

namespace Glimmer.App.Views;

public partial class SidePanel : UserControl
{
    public static readonly DependencyProperty DockedProperty = DependencyProperty.Register(
        nameof(Docked), typeof(bool), typeof(SidePanel), new PropertyMetadata(false, (d, _) => ((SidePanel)d).UpdateChrome()));

    private IEditorSession? session;
    private bool updating;

    public SidePanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (session is not null) session.PropertyChanged -= OnSessionChanged;
            session = (e.NewValue as DocumentChrome)?.Session;
            if (session is not null) session.PropertyChanged += OnSessionChanged;
            Refresh();
        };
    }

    /// <summary>Docked (Classic Layout): full height at the window's right edge, with every tab.</summary>
    public bool Docked { get => (bool)GetValue(DockedProperty); set => SetValue(DockedProperty, value); }

    private void UpdateChrome()
    {
        if (Docked && Body.Parent == FloatingFrame)
        {
            FloatingFrame.Content = null;
            DockedFrame.Child = Body;
            FloatingFrame.Visibility = Visibility.Collapsed;
            DockedFrame.Visibility = Visibility.Visible;
        }
        else if (!Docked && Body.Parent == DockedFrame)
        {
            DockedFrame.Child = null;
            FloatingFrame.Content = Body;
            DockedFrame.Visibility = Visibility.Collapsed;
            FloatingFrame.Visibility = Visibility.Visible;
        }
        CloseButton.Visibility = Docked ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IEditorSession.InspectorTab) or nameof(IEditorSession.ClassicLayout)) Refresh();
    }

    private void Refresh()
    {
        if (session is null) return;
        updating = true;
        var tabs = Docked ? Enum.GetValues<InspectorTab>() : [InspectorTab.Layers, InspectorTab.Paths, InspectorTab.Channels, InspectorTab.History];
        if (Tabs.ItemsSource is not InspectorTab[] current || current.Length != tabs.Length) Tabs.ItemsSource = tabs;
        Tabs.SelectedItem = session.InspectorTab;
        bool assistantHeader = session.InspectorTab == InspectorTab.AI && !Docked;
        AssistantTitle.Visibility = assistantHeader ? Visibility.Visible : Visibility.Collapsed;
        Tabs.Visibility = assistantHeader ? Visibility.Collapsed : Visibility.Visible;
        Layers.Visibility = Show(InspectorTab.Layers);
        Paths.Visibility = Show(InspectorTab.Paths);
        Channels.Visibility = Show(InspectorTab.Channels);
        History.Visibility = Show(InspectorTab.History);
        Assistant.Visibility = Show(InspectorTab.AI);
        updating = false;
    }

    private Visibility Show(InspectorTab tab) => session?.InspectorTab == tab ? Visibility.Visible : Visibility.Collapsed;

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updating && session is not null && Tabs.SelectedItem is InspectorTab tab) session.InspectorTab = tab;
    }

    private void OnClose(object sender, RoutedEventArgs e) { if (session is not null) session.ShowLayersPanel = false; }
}
