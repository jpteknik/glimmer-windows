// Ported from App/AIPanel.swift.
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views.Panels;

public partial class AIPanel : UserControl
{
    private IEditorSession? session;

    public AIPanel()
    {
        InitializeComponent();
        BuildOnDevice();
        BuildSections();
        DataContextChanged += (_, e) =>
        {
            if (session is not null) { session.PropertyChanged -= OnSessionChanged; session.AI.PropertyChanged -= OnAIChanged; }
            session = (e.NewValue as DocumentChrome)?.Session;
            if (session is not null) { session.PropertyChanged += OnSessionChanged; session.AI.PropertyChanged += OnAIChanged; }
            Refresh();
        };
    }

    private IAIController? AI => session?.AI;

    /// <summary>For the plan rows inside the messages (PlanView).</summary>
    internal IAIController? Controller => AI;

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IEditorSession.AISection)) Reveal(session!.AISection);
        if (e.PropertyName is nameof(IEditorSession.State)) Refresh();
    }

    private void OnAIChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (AI is not { } ai || session is null) return;
        KeyBanner.Visibility = ai.HasAPIKey ? Visibility.Collapsed : Visibility.Visible;
        AutoEdit.IsChecked = ai.AutoRunAIPlans;
        Intro.Visibility = ai.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Messages.ItemsSource = ai.Messages.TakeLast(10).ToList();
        Planning.Visibility = ai.IsPlanning ? Visibility.Visible : Visibility.Collapsed;
        SuggestEdits.Visibility = ai.HasAPIKey ? Visibility.Visible : Visibility.Collapsed;
        SuggestEdits.IsEnabled = !ai.IsSuggesting;
        Send.IsEnabled = Message.Text.Trim().Length > 0 && !ai.IsPlanning;
        FillNeedsSelection.Visibility = session.State.Selection is null ? Visibility.Visible : Visibility.Collapsed;
        FillButton.IsEnabled = session.State.Selection is not null;
        Jobs.Visibility = ai.Jobs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        JobList.ItemsSource = ai.Jobs;
        RefreshSections();
    }

    /// <summary>A menu command opens its section and scrolls to it (AIPanel's onChange(of: aiSection)).</summary>
    private void Reveal(AISection section)
    {
        var name = section.ToString();
        foreach (var expander in ((Panel)Scroller.Content).Children.OfType<Expander>())
        {
            if (expander.Tag as string != name) continue;
            expander.IsExpanded = true;
            expander.BringIntoView();
        }
    }

    // ------------------------------------------------------------------------------------------------- assistant

    private void OnAutoEdit(object sender, RoutedEventArgs e) { if (AI is { } ai) ai.AutoRunAIPlans = AutoEdit.IsChecked == true; }
    private void OnSuggestEdits(object sender, RoutedEventArgs e) => AI?.SuggestEdits();
    private void OnOpenSettings(object sender, RoutedEventArgs e) => AppServices.Documents?.OpenSettings("ai");

    private void OnMessageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SendMessage(); e.Handled = true; }
        else Dispatcher.BeginInvoke(Refresh);
    }

    private void OnSend(object sender, RoutedEventArgs e) => SendMessage();

    private void SendMessage()
    {
        var text = Message.Text.Trim();
        if (text.Length == 0 || AI is not { } ai || ai.IsPlanning) return;
        ai.Ask(text);
        Message.Text = "";
    }

    // ------------------------------------------------------------------------------------------------- tools

    private void OnGenerate(object sender, RoutedEventArgs e)
    {
        if (session is null || GeneratePrompt.Text.Trim().Length == 0) return;
        var (w, h) = (GenerateSize.SelectedItem as ComboBoxItem)?.Tag switch
        {
            "square" => (1024, 1024), "portrait" => (832, 1216), "landscape" => (1216, 832), _ => (session.State.Width, session.State.Height),
        };
        session.AI.Generate(GeneratePrompt.Text, GenerateNegative.Text, w, h);
    }

    private void OnFill(object sender, RoutedEventArgs e) =>
        AI?.Fill(FillMode.SelectedIndex == 1 ? AIPipelines.InpaintMode.Remove : AIPipelines.InpaintMode.Fill, FillPrompt.Text, null);

    private void OnExpand(object sender, RoutedEventArgs e)
    {
        if (session is null) return;
        double factor = double.Parse((string)((ListBoxItem)ExpandFactor.SelectedItem).Tag, System.Globalization.CultureInfo.InvariantCulture);
        session.AI.Expand(AIPipelines.Margins.Scaled(session.State.Width, session.State.Height, factor), ExpandPrompt.Text);
    }

    private void OnMatchColor(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Images") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.heic" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AI?.MatchColor(dialog.FileName, 1);
    }

    private void OnClearJobs(object sender, RoutedEventArgs e) => AI?.ClearFinished();

    /// <summary>On This Mac: the free, on-device tools, each with what it does in its tooltip.</summary>
    private void BuildOnDevice()
    {
        (string Title, string Symbol, string Help, Action<IAIController> Run)[] tools =
        [
            ("Auto Enhance", "wand.and.rays", "Corrects tone and color as editable adjustment layers. Runs on this Mac, free. ⇧⌘L", a => a.AutoEnhance()),
            ("Auto Color", "eyedropper.halffull", "Removes a color cast with a Color Balance layer. Runs on this Mac, free. ⇧⌘B", a => a.AutoColor()),
            ("Auto Straighten", "level", "Finds a tilted horizon and opens Crop, levelled. Runs on this Mac, free.", a => a.AutoStraighten()),
            ("Suggest Crop", "crop", "Frames the subject at the crop's aspect ratio. Runs on this Mac, free.", a => a.SuggestCrop()),
            ("Remove Background", "person.crop.rectangle", "Masks the active layer to its subject. Runs on this Mac, free.", a => a.RemoveBackground()),
            ("Select Subject", "person.and.background.dotted", "Selects the main subject. Runs on this Mac, free.", a => a.SelectSubject()),
            ("Select People", "person.2", "Selects every person, with finer edges than Select Subject. Runs on this Mac, free.", a => a.SelectPeople()),
            ("Select Sky", "cloud.sun", "Selects the sky with the depth map and Segment Anything. Runs on this Mac, free.", a => a.SelectSky()),
            ("Straighten Document", "doc.viewfinder", "Flattens a document, sign or screen seen at an angle into a new document. Runs on this Mac, free.", a => a.StraightenDocument()),
            ("Image Text to Layers", "text.viewfinder", "Turns the text in the image into editable text layers. Runs on this Mac, free.", a => a.TextToLayers()),
        ];
        foreach (var (title, symbol, help, run) in tools)
        {
            var b = IconTextButton(title, symbol, help);
            b.Click += (_, _) => { if (AI is { } ai) run(ai); };
            OnDevice.Children.Add(b);
        }
        var magic = IconTextButton("Magic Select Tool (A)", "wand.and.sparkles", "Click an object to select it; ⌥-click to exclude. Return keeps, Esc cancels.");
        magic.Click += (_, _) => { if (session is not null) session.Tool = ToolID.MagicSelect; };
        OnDevice.Children.Add(magic);
    }

    private static Button IconTextButton(string title, string symbol, string help)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("Glimmer.IconButton"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 3, 8, 3),
            Margin = new Thickness(0, 0, 0, 4), ToolTip = Loc.T(help),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Icon { Symbol = symbol, Size = 13, Margin = new Thickness(0, 0, 8, 0) },
                                                                                          new TextBlock { Text = Loc.T(title), FontSize = 12.5 } } },
        };
        System.Windows.Automation.AutomationProperties.SetName(b, Loc.T(title));
        return b;
    }

}
