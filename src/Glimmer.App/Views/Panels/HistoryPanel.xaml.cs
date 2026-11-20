// Ported from App/HistoryPanel.swift.
using System.Windows.Controls;
using Glimmer.App.Localization;
using Glimmer.Editor;

namespace Glimmer.App.Views.Panels;

/// <summary>One step: "Open" first, then every step name (a catalogue key). Steps after the current one are redoable
/// and shown faintly.</summary>
public sealed record HistoryRow(int Index, string Name, string Symbol, bool Current, bool Future)
{
    public double RowOpacity => Future ? 0.45 : 1;
    public string Hint => Loc.T(Future ? "Redo to this step" : "Undo to this step");
}

public partial class HistoryPanel : UserControl
{
    private IEditorSession? session;
    private bool refreshing;

    public HistoryPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (session is not null) session.Document.PropertyChanged -= OnDocumentChanged;
            session = (e.NewValue as DocumentChrome)?.Session;
            if (session is not null) session.Document.PropertyChanged += OnDocumentChanged;
            Refresh();
        };
        IsVisibleChanged += (_, _) => Refresh();
    }

    private void OnDocumentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (session is null || !IsVisible) return;
        refreshing = true;
        var doc = session.Document;
        var rows = new List<HistoryRow> { new(0, Loc.T("Open"), "doc", doc.HistoryIndex == 0, false) };
        for (int i = 0; i < doc.History.Count; i++)
            rows.Add(new HistoryRow(i + 1, Loc.T(doc.History[i]), "arrow.uturn.backward.circle", doc.HistoryIndex == i + 1, i + 1 > doc.HistoryIndex));
        Steps.ItemsSource = rows;
        Steps.SelectedItem = rows.FirstOrDefault(r => r.Current);
        Steps.ScrollIntoView(rows[^1]);
        Footnote.Text = doc.StepsOnDisk > 0
            ? Loc.F("Keeps the last {0} steps; {1} older ones are on disk. Change it in Settings › Performance.", doc.HistoryLimit, doc.StepsOnDisk)
            : Loc.F("Keeps the last {0} steps. Change it in Settings › Performance.", doc.HistoryLimit);
        refreshing = false;
    }

    private void OnStepSelected(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || session is null || Steps.SelectedItem is not HistoryRow row) return;
        session.CommitLiveEdit();
        session.Document.JumpToHistory(row.Index);
    }
}
