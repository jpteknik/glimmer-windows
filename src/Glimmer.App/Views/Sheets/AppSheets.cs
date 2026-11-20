// Ported from App/ModelDownloadSheet.swift (ModelDownloadSheet, ModelFacts), App/ImportReportSheet.swift,
// App/PSDExportSheet.swift, App/LinkedWaitingView.swift and App/AgentSplashView.swift.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Glimmer.AI;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;
using Glimmer.Render;

namespace Glimmer.App.Views.Sheets;

/// <summary>Sizes and times as the Mac's formatters write them ("18.4 MB", "2 hours ago").</summary>
public static class Readable
{
    /// <summary>ByteCountFormatter, file style: decimal units, KB whole, MB with one decimal, GB with two.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["KB", "MB", "GB", "TB"];
        double v = Math.Max(bytes, 0) / 1000.0;
        int u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
        if (u == 0) return $"{Math.Max(1, Math.Round(v)).ToString("0", CultureInfo.CurrentCulture)} {units[0]}";
        return $"{v.ToString(u == 1 ? "0.#" : "0.##", CultureInfo.CurrentCulture)} {units[u]}";
    }

    /// <summary>RelativeDateTimeFormatter for the past: "just now", "12 min ago", "3 h ago", "yesterday", then the date.</summary>
    public static string Ago(DateTimeOffset then)
    {
        var age = DateTimeOffset.Now - then;
        if (age < TimeSpan.FromMinutes(1)) return Loc.T("just now");
        if (age < TimeSpan.FromHours(1)) return Loc.F("{0} min ago", (int)age.TotalMinutes);
        if (age < TimeSpan.FromDays(1)) return Loc.F("{0} h ago", (int)age.TotalHours);
        if (then.Date == DateTimeOffset.Now.Date.AddDays(-1)) return Loc.T("yesterday");
        return then.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
    }
}

/// <summary>The question before Glimmer downloads a model: how much, from whom, from where and under which license.
/// Nothing is fetched until the person clicks Download. The same sheet serves Settings › Models.</summary>
public sealed class ModelDownloadSheet : SheetWindow
{
    private readonly IReadOnlyList<OnDeviceModel> models;
    private readonly IModelLibrary library;
    private readonly ProgressBar progress = new() { Height = 6, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock progressLabel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };
    private readonly StatusLine failure = new() { Kind = StatusKind.Error, Visibility = Visibility.Collapsed };
    private readonly Button download;
    private CancellationTokenSource? running;

    /// <summary>True when every model was downloaded and checked.</summary>
    public bool Succeeded { get; private set; }

    public ModelDownloadSheet(IReadOnlyList<OnDeviceModel> models, IModelLibrary library) : base("Glimmer", 460)
    {
        this.models = models;
        this.library = library;
        long total = models.Sum(m => (long)(m.Requirement().SizeBytes ?? 0));
        var publisher = string.Join(", ", models.Select(m => m.Requirement().Source.Publisher).Distinct().Order());
        var title = Headline("Glimmer");
        title.Text = Loc.F("Download {0} from {1} via Hugging Face?", Readable.Bytes(total), publisher);
        title.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(title);
        Body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Text = models.Count == 1
                ? Loc.T("This feature runs on your Mac with a model that isn't built into Glimmer. Glimmer downloads it once and checks every file against its published checksum before it uses it.")
                : Loc.T("This feature runs on your Mac with models that aren't built into Glimmer. Glimmer downloads them once and checks every file against its published checksum before it uses them."),
        });
        foreach (var model in models) Body.Children.Add(Facts(model));
        progressLabel.Text = Loc.T("Downloading and checking…");
        Body.Children.Add(progressLabel);
        Body.Children.Add(progress);
        AutomationProperties.SetName(progress, Loc.T("Downloading and checking…"));
        Body.Children.Add(failure);
        var buttons = Buttons("Download", Start);
        download = buttons.Children.OfType<Button>().First(b => b.IsDefault);
        Body.Children.Add(buttons);
        Closed += (_, _) => running?.Cancel();
    }

    /// <summary>A model's size, publisher, source and license, with links (ModelFacts).</summary>
    public static FrameworkElement Facts(OnDeviceModel model)
    {
        var r = model.Requirement();
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock { Text = Loc.T(model.Title()), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = Loc.F("{0} · by {1}", Readable.Bytes(r.SizeBytes ?? 0), r.Source.Publisher), Style = (Style)Application.Current.FindResource("Glimmer.Text.Secondary") });
        var links = new TextBlock { Margin = new Thickness(0, 2, 0, 0) };
        links.Inlines.Add(Link(Loc.T("Source"), r.Source.Url, Loc.F("Source of {0}", Loc.T(model.Title()))));
        links.Inlines.Add(new Run("   "));
        links.Inlines.Add(Link(Loc.F("License: {0}", r.License.Spdx), r.License.Url, null));
        panel.Children.Add(links);
        return panel;
    }

    internal static Hyperlink Link(string text, string url, string? name)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null, Foreground = (Brush)Application.Current.FindResource("Glimmer.AccentBrush") };
        if (name is not null) AutomationProperties.SetName(link, name);
        link.RequestNavigate += (_, e) => { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
        return link;
    }

    private async void Start()
    {
        failure.Visibility = Visibility.Collapsed;
        progress.Value = 0;
        progress.Visibility = progressLabel.Visibility = Visibility.Visible;
        download.IsEnabled = false;
        running = new CancellationTokenSource();
        double total = Math.Max(1, models.Sum(m => (double)(m.Requirement().SizeBytes ?? 0)));
        try
        {
            double before = 0;
            foreach (var model in models.Where(library.NeedsDownload))
            {
                double share = (model.Requirement().SizeBytes ?? 0) / total, start = before;
                await library.DownloadAsync(model, new Progress<double>(part => progress.Value = start + part * share), running.Token);
                before += share;
            }
            progress.Value = 1;
            Succeeded = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            progress.Visibility = progressLabel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            progress.Visibility = progressLabel.Visibility = Visibility.Collapsed;
            failure.Verbatim = ex.Message;
            failure.Visibility = Visibility.Visible;
            download.Content = Loc.T("Try Again");
        }
        finally
        {
            download.IsEnabled = true;
        }
    }
}

/// <summary>What came through when a Photoshop file was opened, and what did not. Shown when the file opens and from
/// File › Import Report…: an import never happens silently.</summary>
public sealed class ImportReportSheet : SheetWindow
{
    public ImportReportSheet(PSDImportReport report) : base("Import Report", 520)
    {
        Body.Children.Add(Headline("Import Report"));
        var list = new StackPanel();
        list.Children.Add(new TextBlock { Text = PsdText.Summary(report), FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var note in report.Notes)
            list.Children.Add(new TextBlock { Text = (note.Layer is { } l ? $"“{l}”: " : "") + PsdText.Note(note), TextWrapping = TextWrapping.Wrap,
                                              Style = (Style)FindResource("Glimmer.Text.Secondary"), Margin = new Thickness(0, 0, 0, 8) });
        if (PsdText.Picture(report) is { } picture) list.Children.Add(new TextBlock { Text = picture, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        // The list can be longer than the sheet: the scroll bar is always shown, so a row cut at the edge reads as "there is more".
        Body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = report.Notes.Length > 5 ? ScrollBarVisibility.Visible : ScrollBarVisibility.Auto });
        if (report.Notes.Length > 5) Body.Children.Add(Secondary(Loc.F("{0} points in all. Scroll the list to read them.", report.Notes.Length)));
        Body.Children.Add(Secondary(Loc.T("The Photoshop file itself is not changed. This is a new Glimmer document, and the report is saved with it: File › Import Report… shows it again.")));
        var ok = new Button { Content = Loc.T("OK"), IsDefault = true, IsCancel = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)FindResource("Glimmer.Compact.AccentButton") };
        ok.Click += (_, _) => Close();
        Body.Children.Add(ok);
    }
}

/// <summary>The import and export reports in the person's language (the extensions in ImportReportSheet.swift and
/// PSDExportSheet.swift).</summary>
public static class PsdText
{
    /// <summary>"RGB, 8 bits per channel" as the report keeps it, said in the person's language.</summary>
    public static string Source(string source) => string.Join(", ", source.Split(", ").Select(part =>
    {
        var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length > 0 && part.EndsWith("per channel", StringComparison.Ordinal))
            return int.Parse(digits, CultureInfo.InvariantCulture) == 1 ? Loc.T("1 bit per channel") : Loc.F("{0} bits per channel", int.Parse(digits, CultureInfo.InvariantCulture));
        return Loc.Maybe(part);
    }));

    public static string Summary(PSDImportReport r)
    {
        if (r.Flattened) return Loc.F("Opened as one layer: the file's own flattened picture ({0}).", Source(r.Source));
        if (r.Notes.IsEmpty) return Loc.F("Everything came through: {0} layers, {1} of them groups ({2}).", r.LayersKept, r.GroupsKept, Source(r.Source));
        return Loc.F("{0} of {1} layers came through as layers ({2}). These things did not come through as they were:", r.LayersKept, r.LayersInFile, Source(r.Source));
    }

    public static string? Picture(PSDImportReport r)
    {
        if (r.FileHasNoPicture == true)
            return Loc.T("The file carries no flattened picture of its own (it was saved with Maximize Compatibility off), so Glimmer's picture could not be compared with it.");
        if (r.MatchesTheFile is not { } matches || r.MeanDifference is not { } mean || r.ShareVisiblyDifferent is not { } share) return null;
        if (matches) return Loc.T("Glimmer's picture matches the one saved in the file.");
        return Loc.F("Glimmer's picture differs from the one saved in the file: {0} % of the pixels visibly, {1} of 255 on average.",
                     (share * 100).ToString("0.0", CultureInfo.CurrentCulture), mean.ToString("0.0", CultureInfo.CurrentCulture));
    }

    public static string Note(PSDImportReport.Note note)
    {
        var detail = note.Detail ?? "";
        detail = note.Kind switch
        {
            PSDImportReport.Kind.AdjustmentLeftOut or PSDImportReport.Kind.FillLeftOut or PSDImportReport.Kind.BlendModeBecameNormal => Loc.Maybe(detail),
            PSDImportReport.Kind.LayersNotRead => Source(detail),
            _ => detail,
        };
        return note.Kind switch
        {
            PSDImportReport.Kind.WasText => detail.Length == 0
                ? Loc.T("It was text and is now pixels: the words can't be edited as text.")
                : Loc.F("It was text (“{0}”) and is now pixels: the words can't be edited as text.", detail),
            PSDImportReport.Kind.CurvesMayDiffer => Loc.T("It is a Curves adjustment with the same points. Between the points the curve may run a little differently than in Photoshop."),
            PSDImportReport.Kind.WasSmartObject => Loc.T("It was a smart object and is now pixels. The file placed in it is not kept in this version."),
            PSDImportReport.Kind.AdjustmentLeftOut => Loc.F("It was a {0} adjustment layer. That kind is not read in this version, so it was left out.", detail),
            PSDImportReport.Kind.FillLeftOut => Loc.F("It was a fill or shape layer ({0}). That kind is not read in this version, so it was left out.", detail),
            PSDImportReport.Kind.EffectsNotRead => Loc.T("Its layer effects (shadows, strokes, glows) were not read."),
            PSDImportReport.Kind.VectorMaskNotRead => Loc.T("Its vector mask was not read; the layer's pixels are shown whole."),
            PSDImportReport.Kind.BlendModeBecameNormal => Loc.F("The blend mode {0} became Normal: Glimmer doesn't have it.", detail),
            PSDImportReport.Kind.PixelsIncomplete => Loc.T("Its pixels are incomplete in the file, so the layer came in empty."),
            PSDImportReport.Kind.GroupEndMissing => Loc.T("The group's end is missing in the file; it came in empty."),
            PSDImportReport.Kind.GroupRecordMissing => Loc.T("A group in the file has no record of its own; its layers came in without the group."),
            PSDImportReport.Kind.MasksMerged => Loc.T("It had an outline and a painted mask. The two are now one mask, so the outline can't be edited as a path."),
            _ => Loc.F("Glimmer reads the layers of RGB and grayscale files with 8 or 16 bits per channel. This file is {0}. Its layers were not read; the picture is the one saved in the file.", detail),
        };
    }

    public static string Note(PSDExportReport.Note note)
    {
        var detail = note.Detail ?? "";
        return note.Kind switch
        {
            PSDExportReport.Kind.WrittenAsPixels => Loc.F("It is written as a layer of pixels that looks the same (in Glimmer it is {0}).", detail),
            PSDExportReport.Kind.AdjustmentLeftOut => Loc.F("It is a {0} adjustment layer, which Glimmer can't write as one. It is not among the file's layers; the flattened picture in the file still shows it.", detail),
            PSDExportReport.Kind.MaskSimplified => Loc.T("Its mask is written without its feather and density."),
            PSDExportReport.Kind.ClippedToCanvas => Loc.T("It reaches outside the canvas, and what is outside is not written."),
            PSDExportReport.Kind.LinearBlending => Loc.T("This document blends colors in linear light. Other programs blend these layers in gamma, so the layers will look different there; the flattened picture in the file is right."),
            PSDExportReport.Kind.WrittenInSRGB => Loc.T("The document's colors are Display P3; the file is written in sRGB."),
            PSDExportReport.Kind.CurveDrawnDifferently => Loc.T("It is written as a Curves layer with points added along the curve, so that it looks the same in programs that draw curves their own way. It has more points to edit there."),
            _ => Loc.T("All layers are merged into one, as chosen: the file looks right everywhere and has no layers to edit."),
        };
    }
}

/// <summary>The document parts of File › Export › Photoshop File… (IPhotoshopDocument): Glimmer.Formats' PSDExport over
/// the session's state, encoded off the UI thread (PSDExport.data(for:depth:flattened:)).</summary>
public sealed class PhotoshopExport(IEditorSession session) : IPhotoshopDocument
{
    public bool BlendsInLinearLight => session.State.BlendSpace == BlendSpace.Linear;

    public Task<(byte[] Data, PSDExportReport Report)> PreparePSDExportAsync(int depth, bool flattened, CancellationToken cancellation)
    {
        var state = session.State;
        return Task.Run(() => PSDExport.Data(state, depth, flattened), cancellation);
    }
}

/// <summary>File › Export… › Photoshop File…: says what the file will hold before it is saved, and for a document that
/// blends in linear light offers to merge the layers instead.</summary>
public sealed class PSDExportSheet : SheetWindow
{
    private readonly IEditorSession session;
    private readonly IPhotoshopDocument photoshop;
    private readonly StackPanel reportPanel = new();
    private readonly Spinner working = new() { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private readonly StatusLine problem = new() { Kind = StatusKind.Error, Visibility = Visibility.Collapsed };
    private readonly Button save;
    private int depth = 8;
    private bool flattened;
    private (byte[] Data, PSDExportReport Report)? prepared;
    private CancellationTokenSource? preparing;

    public PSDExportSheet(IEditorSession session, IPhotoshopDocument photoshop) : base("Export as Photoshop File", 540)
    {
        this.session = session;
        this.photoshop = photoshop;
        Body.Children.Add(Headline("Export as Photoshop File"));
        var form = new List<UIElement>
        {
            Row("Bit depth", Picker([8, 16], d => d == 8 ? "8 bits per channel" : "16 bits per channel", 8, d => { depth = d; Prepare(); })),
        };
        if (photoshop.BlendsInLinearLight)
            form.Add(Row("Layers", Picker([false, true], f => f ? "Merged into one layer" : "As they are", false, f => { flattened = f; Prepare(); })));
        Body.Children.Add(Section([.. form]));
        AutomationProperties.SetName(working, Loc.T("Preparing the file"));
        Body.Children.Add(working);
        Body.Children.Add(problem);
        Body.Children.Add(reportPanel);
        Body.Children.Add(new TextBlock
        {
            Text = Loc.T("Text, smart objects and effects can't be edited as such in the file. Glimmer has read its own Photoshop files back and macOS shows their picture; the layers have not been tried in Photoshop."),
            TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Glimmer.Text.Secondary"), Margin = new Thickness(0, 8, 0, 0),
        });
        var buttons = Buttons("Save…", Save, () => prepared is not null);
        save = buttons.Children.OfType<Button>().First(b => b.IsDefault);
        Body.Children.Add(buttons);
        Closed += (_, _) => preparing?.Cancel();
        Prepare();
    }

    private async void Prepare()
    {
        preparing?.Cancel();
        var token = (preparing = new CancellationTokenSource()).Token;
        session.CommitLiveEdit();
        prepared = null;
        Validation();
        working.Visibility = Visibility.Visible;
        problem.Visibility = Visibility.Collapsed;
        reportPanel.Children.Clear();
        try
        {
            var result = await photoshop.PreparePSDExportAsync(depth, flattened, token);
            if (token.IsCancellationRequested) return;
            prepared = result;
            ShowReport(result.Report);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            problem.Verbatim = ex.Message;
            problem.Visibility = Visibility.Visible;
        }
        finally
        {
            if (!token.IsCancellationRequested) working.Visibility = Visibility.Collapsed;
        }
        Validation();
    }

    private void ShowReport(PSDExportReport report)
    {
        var list = new StackPanel();
        list.Children.Add(new TextBlock
        {
            Text = report.Notes.Count == 0 ? Loc.F("Every layer is written as it is: {0} layers and groups.", report.LayersWritten)
                                        : Loc.F("{0} layers and groups are written. These things are not written as they are:", report.LayersWritten),
            FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (var note in report.Notes)
            list.Children.Add(new TextBlock { Text = (note.Layer is { } l ? $"“{l}”: " : "") + PsdText.Note(note), TextWrapping = TextWrapping.Wrap,
                                              Style = (Style)FindResource("Glimmer.Text.Secondary"), Margin = new Thickness(0, 0, 0, 8) });
        if (report.GammaDifference is { } d && !flattened)
            list.Children.Add(new TextBlock
            {
                Text = Loc.F("Measured on this document: blended as other programs do, the layers differ from what you see here on {0} % of the pixels visibly, {1} of 255 on average.",
                             (d.ShareVisible * 100).ToString("0.0", CultureInfo.CurrentCulture), d.Mean.ToString("0.0", CultureInfo.CurrentCulture)),
                TextWrapping = TextWrapping.Wrap,
            });
        reportPanel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 280, VerticalScrollBarVisibility = report.Notes.Count > 4 ? ScrollBarVisibility.Visible : ScrollBarVisibility.Auto });
        if (report.Notes.Count > 4) reportPanel.Children.Add(Secondary(Loc.F("{0} points in all. Scroll the list to read them.", report.Notes.Count)));
    }

    private void Save()
    {
        if (prepared is not { } p) return;
        Close();
        if (AppServices.FileDialogs.ChooseSaveLocation(Loc.T("Untitled") + ".psd", ["psd"]) is not { } path) return;
        try
        {
            // Atomically, as Data.write(options: .atomic): a half-written file never replaces a good one.
            var temporary = path + ".glimmer-tmp";
            File.WriteAllBytes(temporary, p.Data);
            File.Move(temporary, path, overwrite: true);
            session.ShowToast(new UiText("Exported {0} ({1}).", Path.GetFileName(path), Readable.Bytes(p.Data.Length)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            session.Report(ex);
        }
    }
}

/// <summary>File › Linked Files Waiting…: the edits of files from Flimmer or Jämmer that are kept in the link journal
/// and are not open in a window now. Each one can be opened again, or moved to the Recycle Bin.</summary>
public sealed class LinkedWaitingSheet : SheetWindow
{
    private readonly ILinkedFilesWaiting journal;
    private readonly StackPanel list = new();
    private readonly StatusLine problem = new() { Kind = StatusKind.Error, Visibility = Visibility.Collapsed };

    public LinkedWaitingSheet(ILinkedFilesWaiting journal) : base("Linked Files Waiting", 520)
    {
        this.journal = journal;
        Body.Children.Add(Headline("Linked Files Waiting"));
        Body.Children.Add(Secondary(Loc.T("Edits of files from another app that are not saved back yet. Open one to go on with it and save it back.")));
        Body.Children.Add(list);
        Body.Children.Add(problem);
        var done = new Button { Content = Loc.T("Done"), IsDefault = true, IsCancel = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right,
                                Margin = new Thickness(0, 8, 0, 0), Style = (Style)FindResource("Glimmer.Compact.AccentButton") };
        done.Click += (_, _) => Close();
        Body.Children.Add(done);
        Load();
    }

    private void Load()
    {
        list.Children.Clear();
        var entries = journal.Entries();
        if (entries.Count == 0)
            list.Children.Add(new TextBlock { Text = Loc.T("Nothing is waiting."), Style = (Style)FindResource("Glimmer.Text.Secondary"), MinHeight = 60,
                                              HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 20, 0, 20) });
        foreach (var entry in entries)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var open = new Button { Content = Loc.T("Open"), Style = (Style)FindResource("Glimmer.Compact.Button"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            open.Click += (_, _) =>
            {
                if (journal.Open(entry)) Close();
                else Problem(Loc.T("Glimmer couldn't read these edits. They are left where they are."));
            };
            var discard = new Button { Content = Loc.T("Discard…"), Style = (Style)FindResource("Glimmer.Compact.Button"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            discard.Click += (_, _) => AskDiscard(entry);
            DockPanel.SetDock(discard, Dock.Right);
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(discard);
            row.Children.Add(open);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = entry.FileName, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = Detail(entry), TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Glimmer.Text.Secondary") });
            row.Children.Add(text);
            AutomationProperties.SetName(row, entry.FileName);
            list.Children.Add(row);
            list.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Glimmer.EdgeBrush") });
        }
        if (journal.Broken.Count > 0)
            Problem(Loc.F("{0} kept edit(s) can't be read. They are left in Glimmer's folder and are not removed.", journal.Broken.Count));
    }

    /// <summary>"For Flimmer · 3 steps not saved back · 2 h ago · 18.4 MB"</summary>
    private static string Detail(WaitingLink entry)
    {
        int steps = entry.UnreturnedSteps ?? 1;
        string state = !entry.Unreturned ? Loc.T("saved back") : steps == 1 ? Loc.T("1 step not saved back") : Loc.F("{0} steps not saved back", steps);
        return Loc.F("For {0} · {1} · {2} · {3}", entry.Requester, state, Readable.Ago(entry.WrittenAt), Readable.Bytes(entry.Bytes));
    }

    private void AskDiscard(WaitingLink entry)
    {
        int steps = entry.UnreturnedSteps ?? 1;
        // What is lost, and that the file itself is not touched.
        var message = steps == 1
            ? Loc.F("The step you made in Glimmer is not saved back to {0} and will no longer be here. {1} itself stays as it is. The layered copy goes to the Trash, where you can still get it.", entry.Requester, entry.FileName)
            : Loc.F("The {0} steps you made in Glimmer are not saved back to {1} and will no longer be here. {2} itself stays as it is. The layered copy goes to the Trash, where you can still get it.", steps, entry.Requester, entry.FileName);
        int answer = AlertWindow.Show(this, Loc.F("Move the edits of {0} to the Trash?", entry.FileName), message,
                                      [("Move to Trash", AlertRole.Destructive), ("Cancel", AlertRole.Cancel)]);
        if (answer != 0) return;
        if (!journal.Discard(entry)) Problem(Loc.T("Glimmer couldn't move the edits to the Trash, so they are kept."));
        Load();
    }

    private void Problem(string text)
    {
        problem.Verbatim = text;
        problem.Visibility = Visibility.Visible;
    }
}

/// <summary>Shown at launch: the line to hand to an AI agent so it can learn Glimmer's local API. Also under
/// Help › Agent Access. Dark, with the Neon icon family's violet.</summary>
public sealed class AgentSplashWindow : SheetWindow
{
    public const string ShowAtLaunchKey = "agent.splashAtLaunch";
    private static readonly Color Neon = Color.FromRgb(0xC4, 0x5C, 0xFF);
    private static readonly Color InkOnNeon = Color.FromRgb(13, 10, 20);
    private readonly IAgentServer server;
    private readonly ContentControl promptArea = new();

    public AgentSplashWindow(IAgentServer server) : base("Glimmer", 560)
    {
        this.server = server;
        Background = new SolidColorBrush(InkOnNeon);
        Body.Margin = new Thickness(24);
        var neon = new SolidColorBrush(Neon);

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        var icon = new Grid { Width = 72, Height = 72, Margin = new Thickness(0, 0, 14, 0) };
        // The icon's own shape is a little inside its square: the glow follows that.
        icon.Children.Add(new GlowPulse { GlowColor = Neon, Strength = 0.5, Radius = 18, CornerRadius = 16, Margin = new Thickness(7) });
        icon.Children.Add(new Border { CornerRadius = new CornerRadius(16), Margin = new Thickness(7), Background = (Brush)FindResource("Glimmer.RaisedBrush"),
                                       Child = new Icon { Symbol = "sparkle", Size = 34, Foreground = neon } });
        header.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = "Glimmer", FontSize = 26, FontWeight = FontWeights.Bold,
                                   Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Neon, Opacity = 0.6, BlurRadius = 20, ShadowDepth = 0 } };
        AutomationProperties.SetHeadingLevel(name, AutomationHeadingLevel.Level1);
        titles.Children.Add(name);
        titles.Children.Add(new TextBlock { Text = Loc.T("A photo editor for the Mac that agents can drive too."), Style = (Style)FindResource("Glimmer.Text.Secondary") });
        header.Children.Add(titles);
        Body.Children.Add(header);

        var heading = new TextBlock { Text = Loc.T("For your agent"), Style = (Style)FindResource("Glimmer.Text.Headline"), Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        Body.Children.Add(heading);
        Body.Children.Add(new TextBlock
        {
            Text = Loc.T("Paste this into Claude or another AI agent. It fetches the guide from Glimmer on this Mac and can then open, edit and export images in your open windows."),
            TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Glimmer.Text.Secondary"), Margin = new Thickness(0, 0, 0, 8),
        });
        Body.Children.Add(promptArea);

        var bottom = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        var atLaunch = new CheckBox { Content = Loc.T("Show at launch"), IsChecked = AppServices.Settings.Get(ShowAtLaunchKey, true), Style = (Style)FindResource("Glimmer.Compact.CheckBox") };
        atLaunch.Click += (_, _) => AppServices.Settings.Set(ShowAtLaunchKey, atLaunch.IsChecked == true);
        var close = new Button { Content = Loc.T("Close"), IsDefault = true, IsCancel = true, MinWidth = 80, Style = (Style)FindResource("Glimmer.Compact.AccentButton"),
                                 Background = neon, Foreground = new SolidColorBrush(InkOnNeon) };   // dark on neon: 5.9 : 1 (white is 3.3 : 1)
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(close);
        bottom.Children.Add(atLaunch);
        Body.Children.Add(bottom);

        server.PropertyChanged += OnServerChanged;
        Closed += (_, _) => server.PropertyChanged -= OnServerChanged;
        ShowPrompt();
    }

    private void OnServerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(ShowPrompt);

    private void ShowPrompt()
    {
        if (server.AgentPrompt is { } prompt)
        {
            var neon = new SolidColorBrush(Neon);
            var text = new TextBox
            {
                Text = prompt, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("Glimmer.InkBrush"), FontFamily = (FontFamily)FindResource("Glimmer.MonoFont"), FontSize = 12.5, Padding = new Thickness(10),
                Style = (Style)FindResource("Glimmer.PlainTextBox"),
            };
            // The field is named by its group, never by a label on the selectable text itself.
            AutomationProperties.SetName(text, Loc.T("Text for your agent"));
            var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Theme.WithAlpha(Neon, 0.8)),
                                     Background = new SolidColorBrush(Theme.WithAlpha(Colors.White, 0.05)), Child = text };
            var copy = new Button { MinWidth = 70, MinHeight = 34, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top,
                                    Style = (Style)FindResource("Glimmer.Compact.AccentButton"), Background = neon, Foreground = new SolidColorBrush(InkOnNeon),
                                    ToolTip = "Ctrl+Shift+C" };
            void Label(bool copied) => copy.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { new Icon { Symbol = copied ? "checkmark" : "doc.on.doc", Size = 12, Margin = new Thickness(0, 0, 6, 0) }, new TextBlock { Text = Loc.T(copied ? "Copied" : "Copy") } },
            };
            Label(false);
            AutomationProperties.SetName(copy, Loc.T("Copy"));
            async void Copy()
            {
                AppServices.Clipboard.SetText(prompt);
                Label(true);
                AutomationProperties.SetName(copy, Loc.T("Copied"));
                if (AutomationPeer(copy) is { } peer) peer.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, Loc.T("Copied"), "glimmer.copied");
                await Task.Delay(2000);
                Label(false);
                AutomationProperties.SetName(copy, Loc.T("Copy"));
            }
            copy.Click += (_, _) => Copy();
            InputBindings.Clear();
            InputBindings.Add(new KeyBinding(new RelayCommand(Copy), Key.C, ModifierKeys.Control | ModifierKeys.Shift));
            var row = new DockPanel();
            DockPanel.SetDock(copy, Dock.Right);
            row.Children.Add(copy);
            row.Children.Add(frame);
            promptArea.Content = row;
        }
        else if (server.Failure is { } failure)
        {
            promptArea.Content = new StatusLine { Kind = StatusKind.Error, Verbatim = failure };
        }
        else
        {
            var spinner = new Spinner { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(spinner, Loc.T("Starting the server for agents"));
            promptArea.Content = spinner;
        }
    }

    private static System.Windows.Automation.Peers.AutomationPeer? AutomationPeer(UIElement element) =>
        System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(element);

    private sealed class RelayCommand(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }
}
