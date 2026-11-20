// What DocumentView.swift decides with `if` around each overlay, as properties the XAML can bind: which floating
// parts show, given the session's state and the preferences. App-side; recomputed whenever the session changes.
using System.ComponentModel;
using Glimmer.Editor;

namespace Glimmer.App.Views;

public sealed class DocumentChrome : INotifyPropertyChanged
{
    public DocumentChrome(IEditorSession session)
    {
        Session = session;
        session.PropertyChanged += (_, _) => Raise(null);
        session.AI.PropertyChanged += (_, _) => Raise(null);
        session.Canvas.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(ICanvasController.Zoom)) Raise(nameof(ZoomText)); };
        AppServices.Settings.Changed += (_, _) => Raise(null);
    }

    public IEditorSession Session { get; }

    private bool Hidden => Session.InterfaceHidden;

    public bool ShowsToolPalette => Session.ShowTools && !Hidden;
    public bool ShowsTitleChip => !Hidden;
    public bool ShowsOptionsBar => !Hidden;
    public bool ShowsNavigator => Session.ShowNavigator && !Hidden;
    public bool ShowsSelectionBar => AppServices.Settings.Get(AppPreferences.ShowSelectionBarKey, true) && !Hidden;
    public bool ShowsOmnibar => AppServices.Settings.Get(Preferences.ShowOmnibarKey, true) && !Hidden && Session.Liquify is null
                                && Session.TransformSession is null && Session.CropSession is null && !Session.IsPaletteOpen;
    public bool ShowsChrome => !Hidden;
    public bool ShowsFloatingSidePanel => Session.ShowLayersPanel && !Session.ClassicLayout && !Hidden;
    public bool ShowsDockedSidePanel => Session.ClassicLayout && !Hidden;
    public bool ShowsPalette => Session.IsPaletteOpen;
    public bool ShowsFilterSheet => Session.FilterDialog is not null;
    public double FilterSheetLeft => Session.ShowTools ? 104 : 14;

    /// <summary>An untouched, empty document greets you like the web app does (DocumentView.showsStartCard).</summary>
    public bool ShowsStartCard => !Session.StartCardDismissed && Session.Document.History.Count == 0
                                  && Session.State.AllLayers.Count() <= 1 && Session.EditingText is null;

    // The title chip (DocumentView.swift TitleChip).
    public string DocumentTitle => Session.LinkedImage?.FileName ?? Session.BrokenLink?.Link.FileName ?? Session.ExplicitName ?? Session.DocumentName;
    public string SizeText => $"{Session.State.Width}×{Session.State.Height}";
    public string ZoomText => Session.Canvas.Zoom.ToString("P0", System.Globalization.CultureInfo.CurrentCulture);
    public string? LinkedForText => Session.LinkedImage is { } link ? Localization.Loc.F("For {0}", link.Requester) : null;
    /// <summary>The hint gives way to what must be said about a window that was kept or brought back.</summary>
    public bool ShowsSaveBackHint => Session.LinkedImage is not null && !Session.Recovered && Session.JournalRoomTrouble is null;
    public bool ShowsKeptFromLastTime => Session.Recovered && Session.KeptFromLastTime && Session.JournalRoomTrouble is null;
    public bool ShowsBroughtBack => Session.Recovered && !Session.KeptFromLastTime && Session.JournalRoomTrouble is null;
    public string? BrokenLinkTo => Session.BrokenLink is { } b ? Localization.Loc.F("to {0}", b.Link.Requester) : null;
    public string? RunningJob => Session.AI.Jobs.FirstOrDefault(j => j.Status == AIJobStatusKind.Running) is { } job
        ? Localization.Loc.T(job.Title) + "…" : null;

    // The chips' states (ChromeBar).
    public bool AIChipOn => Session.ShowLayersPanel && Session.InspectorTab == InspectorTab.AI;
    public bool LayersChipOn => Session.ShowLayersPanel && Session.InspectorTab != InspectorTab.AI;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
