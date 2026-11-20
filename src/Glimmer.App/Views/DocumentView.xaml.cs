// Ported from App/DocumentView.swift (DocumentView: the sheets and alerts it presents; ChromeBar.toggle; StartCard).
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glimmer.App.Localization;
using Glimmer.App.Views.Sheets;
using Glimmer.Editor;

namespace Glimmer.App.Views;

public partial class DocumentView : UserControl
{
    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(
        nameof(Session), typeof(IEditorSession), typeof(DocumentView), new PropertyMetadata(null, (d, e) => ((DocumentView)d).Attach(e)));

    private DocumentChrome? chrome;

    public DocumentView()
    {
        InitializeComponent();
    }

    public IEditorSession? Session { get => (IEditorSession?)GetValue(SessionProperty); set => SetValue(SessionProperty, value); }

    private void Attach(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is IEditorSession old)
        {
            old.PropertyChanged -= OnSessionChanged;
            old.AI.PropertyChanged -= OnAIChanged;
            old.Document.PropertyChanged -= OnDocumentChanged;
        }
        if (e.NewValue is not IEditorSession session) { DataContext = null; return; }
        chrome = new DocumentChrome(session);
        DataContext = chrome;
        session.PropertyChanged += OnSessionChanged;
        session.AI.PropertyChanged += OnAIChanged;
        session.Document.PropertyChanged += OnDocumentChanged;
    }

    /// <summary>The disk is too full for the whole undo history, or the document outgrew memory: said once, in the
    /// window's alert (DocumentView.swift, onChange(of: document.diskNotice)).</summary>
    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IEditorDocument.DiskNotice) || Session is not { Document.DiskNotice: { } notice } s) return;
        s.Document.DiskNotice = null;
        s.ErrorMessage = notice;
    }

    /// <summary>A feature needs models that aren't on this computer: ask (ModelDownloadSheet), and run it once they are.</summary>
    private void OnAIChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAIController.ModelRequest) || Session is not { AI.ModelRequest: { } request } s || AppServices.Models is not { } models || !IsVisible) return;
        s.AI.ModelRequest = null;
        var sheet = new ModelDownloadSheet(request.Models, models) { Owner = Window.GetWindow(this) };
        sheet.ShowDialog();
        if (sheet.Succeeded) request.Retry?.Invoke();
    }

    public void FocusCanvas() => Dispatcher.BeginInvoke(() => Canvas.Focus(), System.Windows.Threading.DispatcherPriority.Input);

    /// <summary>F6: the canvas, the tool rail, the options bar, the side panel, the omnibar, in turn (whichever show).</summary>
    public void FocusNextRegion(bool backwards)
    {
        var regions = new FrameworkElement[] { Canvas, ToolRail, OptionsBar, FloatingPanel.IsVisible ? FloatingPanel : DockedPanel, Omnibar }
            .Where(r => r.IsVisible).ToList();
        if (regions.Count == 0) return;
        int current = regions.FindIndex(r => r.IsKeyboardFocusWithin);
        var next = regions[((current < 0 ? 0 : current + (backwards ? -1 : 1)) + regions.Count) % regions.Count];
        if (next == Canvas) Canvas.Focus();
        else next.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    // ------------------------------------------------------------------------------------------------- chips

    private void OnAIChip(object sender, RoutedEventArgs e) => Toggle(InspectorTab.AI);
    private void OnToolsChip(object sender, RoutedEventArgs e) { if (Session is { } s) s.ShowTools = !s.ShowTools; }
    private void OnLayersChip(object sender, RoutedEventArgs e) { if (Session is { } s) Toggle(s.InspectorTab == InspectorTab.AI ? InspectorTab.Layers : s.InspectorTab); }
    private void OnExportChip(object sender, RoutedEventArgs e) => Session?.Perform(Core.CommandID.Export);
    private void OnPaletteChip(object sender, RoutedEventArgs e) { if (Session is { } s) s.IsPaletteOpen = true; }
    private void OnScrimClicked(object sender, MouseButtonEventArgs e) { if (Session is { } s) s.IsPaletteOpen = false; }
    private void OnOpenImage(object sender, RoutedEventArgs e) => AppServices.Opener?.ShowOpenPanel();
    private void OnStartBlank(object sender, RoutedEventArgs e) { if (Session is { } s) s.StartCardDismissed = true; }

    /// <summary>ChromeBar.toggle: a chip shows its panel, or hides it when it is already showing. In Classic Layout the
    /// docked panel is always there; the chips just switch what it shows.</summary>
    private void Toggle(InspectorTab tab)
    {
        if (Session is not { } s) return;
        if (s.ClassicLayout)
        {
            s.InspectorTab = tab;
            s.ShowLayersPanel = true;
            return;
        }
        bool showing = s.ShowLayersPanel && (tab == InspectorTab.AI) == (s.InspectorTab == InspectorTab.AI);
        if (showing) s.ShowLayersPanel = false;
        else { s.InspectorTab = tab; s.ShowLayersPanel = true; }
    }

    // ------------------------------------------------------------------------------------------------- sheets & alerts

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Session is not { } s || !IsVisible) return;
        switch (e.PropertyName)
        {
            case nameof(IEditorSession.IsExportSheetPresented) when s.IsExportSheetPresented:
                Present(new ExportSheet(s), () => s.IsExportSheetPresented = false);
                break;
            case nameof(IEditorSession.IsImageSizePresented) when s.IsImageSizePresented:
                Present(new ImageSizeSheet(s), () => s.IsImageSizePresented = false);
                break;
            case nameof(IEditorSession.IsCanvasSizePresented) when s.IsCanvasSizePresented:
                Present(new CanvasSizeSheet(s), () => s.IsCanvasSizePresented = false);
                break;
            case nameof(IEditorSession.IsLayerStylePresented) when s.IsLayerStylePresented:
                Present(new LayerStyleSheet(s), () => s.IsLayerStylePresented = false);
                break;
            case nameof(IEditorSession.SelectionDialog) when s.SelectionDialog is { } command:
                Present(new SelectionModifySheet(s, command), () => s.SelectionDialog = null);
                break;
            case nameof(IEditorSession.IsBatchSheetPresented) when s.IsBatchSheetPresented:
                if (AppServices.BatchRunners is { } runners)
                {
                    Present(new BatchSheet(s, runners()), () => s.IsBatchSheetPresented = false);
                }
                else
                {
                    s.IsBatchSheetPresented = false;
                    s.ErrorMessage = "Batch Process isn’t available in Glimmer for Windows yet.";
                }
                break;
            case nameof(IEditorSession.IsPSDExportPresented) when s.IsPSDExportPresented:
                Present(new PSDExportSheet(s, AppServices.PhotoshopDocuments(s)), () => s.IsPSDExportPresented = false);
                break;
            case nameof(IEditorSession.IsImportReportPresented) when s.IsImportReportPresented && s.ImportReport is { } report:
                Present(new ImportReportSheet(report), () => s.IsImportReportPresented = false);
                break;
            case nameof(IEditorSession.IsRawDevelopPresented) when s.IsRawDevelopPresented:
                if (AppServices.RawDevelopModels(s) is { } model)
                {
                    Present(new RawDevelopSheet(model), () => s.IsRawDevelopPresented = false);
                }
                else
                {
                    s.IsRawDevelopPresented = false;
                    // The Mac's wording (AIController+Raw.swift) for a layer that wasn't opened from a RAW file.
                    s.ErrorMessage = "This layer has no RAW file. Only a layer opened from a camera's RAW file has one.";
                }
                break;
            case nameof(IEditorSession.FilterDialog):
                FilterSheetHost.Content = s.FilterDialog is { } settings ? new FilterSheet(s, settings) : null;
                FilterSheetHost.Margin = new Thickness(s.ShowTools ? 104 : 14, 96, 0, 0);
                break;
            case nameof(IEditorSession.ErrorMessage) when s.ErrorMessage is { } message:
                AlertWindow.Show(Window.GetWindow(this), "Glimmer", Loc.T(message), [("OK", AlertRole.Default)]);
                s.ErrorMessage = null;
                break;
            case nameof(IEditorSession.HealWarning) when s.HealWarning is { } warning:
            {
                var answer = AlertWindow.Show(Window.GetWindow(this), Loc.T("This is a large area to heal"),
                    Loc.F("{0}\n\nGenerative Fill (AI ▸ Generative Fill…) can replace larger areas.", warning),
                    [("Fill Anyway", AlertRole.Default), ("Cancel", AlertRole.Cancel)]);
                s.HealWarning = null;
                if (answer == 0) s.AI.HealSelection(allowLarge: true);
                break;
            }
            case nameof(IEditorSession.RasterizeTextConfirmation) when s.RasterizeTextConfirmation is { } count:
            {
                var text = count == 1
                    ? Loc.T("Distort and perspective can't keep text editable: the text layer becomes pixels and can't be edited as text anymore. Undo brings the text back.")
                    : Loc.F("Distort and perspective can't keep text editable: {0} text layers become pixels and can't be edited as text anymore. Undo brings the text back.", count);
                var answer = AlertWindow.Show(Window.GetWindow(this), Loc.T("Rasterize text?"), text,
                    [("Rasterize", AlertRole.Destructive), ("Cancel", AlertRole.Cancel)]);
                s.RasterizeTextConfirmation = null;
                if (answer == 0) s.CommitTransform(confirmed: true);
                break;
            }
            case nameof(IEditorSession.TextNotice) when s.TextNotice is { } notice:
            {
                (string, AlertRole)[] buttons = notice.Kind switch
                {
                    TextNoticeKind.FirstUse => [("Send", AlertRole.Default), ("Not Now", AlertRole.Cancel)],
                    TextNoticeKind.UnknownPrice => [("I Know, Send", AlertRole.Default), ("Not Now", AlertRole.Cancel)],
                    _ => [("Open Settings…", AlertRole.Normal), ("OK", AlertRole.Cancel)],
                };
                var answer = AlertWindow.Show(Window.GetWindow(this), Loc.T(notice.Title), Loc.T(notice.Message), buttons);
                s.TextNotice = null;
                if (answer == 0)
                {
                    if (notice.Kind == TextNoticeKind.CantSee) AppServices.Documents?.OpenSettings("ai");
                    else notice.Proceed?.Invoke();
                }
                break;
            }
            case nameof(IEditorSession.OfflineSibling) when s.OfflineSibling is { } app:
            {
                // The Mac offers "Open {0}" (AIController.launch); Windows has no way to start a sibling app yet, so only OK.
                AlertWindow.Show(Window.GetWindow(this), Loc.F("{0} is not running.", app.DisplayName()),
                    Loc.F("The assistant needs {0} for this. Open it, then ask again.", app.DisplayName()), [("OK", AlertRole.Default)]);
                s.OfflineSibling = null;
                break;
            }
        }
    }

    /// <summary>A sheet: modal over this window; `closed` puts the session's flag back.</summary>
    private void Present(Window sheet, Action closed)
    {
        sheet.Owner = Window.GetWindow(this);
        sheet.Closed += (_, _) => { closed(); FocusCanvas(); };
        Dispatcher.BeginInvoke(() => sheet.ShowDialog());
    }
}
