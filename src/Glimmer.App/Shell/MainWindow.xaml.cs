// Ported from App/GlimmerApp.swift (the document window) with Windows' window conventions: one window with a tab per
// document (each tab has its own editor session), a custom title bar with the menu bar in it, and Snap Layouts.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Glimmer.App.Localization;
using Glimmer.App.Views;

namespace Glimmer.App.Shell;

/// <summary>One open document: its session and its view.</summary>
public sealed class DocumentTab : INotifyPropertyChanged
{
    public DocumentTab(IEditorSession session)
    {
        Session = session;
        View = new DocumentView { Session = session };
        session.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(IEditorSession.DocumentName)) Raise(nameof(Title)); };
        session.Document.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(IEditorDocument.HasUnsavedChanges)) Raise(nameof(HasUnsavedChanges)); };
    }

    public IEditorSession Session { get; }
    public DocumentView View { get; }
    public string Title => Session.LinkedImage?.FileName ?? Session.ExplicitName ?? Session.DocumentName;
    public bool HasUnsavedChanges => Session.Document.HasUnsavedChanges;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class MainWindow : Window, IDocumentHost
{
    private readonly ObservableCollection<DocumentTab> tabs = [];
    private HwndSource? hwnd;
    private bool maximizeHover;

    public MainWindow()
    {
        InitializeComponent();
        TabStrip.ItemsSource = tabs;
        tabs.CollectionChanged += (_, _) => TabStrip.Visibility = tabs.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        new MenuBuilder(() => SelectedSession, this, this).Build(MainMenu);
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => UpdateForWindowState();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public IEditorSession? SelectedSession => (TabStrip.SelectedItem as DocumentTab)?.Session;

    public void AddDocument(IEditorSession session)
    {
        var tab = new DocumentTab(session);
        tabs.Add(tab);
        Documents.Children.Add(tab.View);
        TabStrip.SelectedItem = tab;
        TabStrip.Visibility = tabs.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTabSelected(object sender, SelectionChangedEventArgs e)
    {
        foreach (var tab in tabs) tab.View.Visibility = tab == TabStrip.SelectedItem ? Visibility.Visible : Visibility.Collapsed;
        if (TabStrip.SelectedItem is DocumentTab selected)
        {
            Title = selected.Title + " – Glimmer";
            selected.View.FocusCanvas();
            // The front document is the one agents and Open Package Folder… act on (DocumentOpener.markFront).
            if (selected.Session is EditorSession session) AppServices.Opener?.MarkFront(session);
        }
        CommandManager.InvalidateRequerySuggested();
    }

    public void SelectTab(int step)
    {
        if (tabs.Count < 2) return;
        TabStrip.SelectedIndex = (TabStrip.SelectedIndex + step + tabs.Count) % tabs.Count;
    }

    public void CloseSelectedTab()
    {
        if (TabStrip.SelectedItem is DocumentTab tab) CloseTab(tab);
    }

    /// <summary>The tab's close button and Ctrl+W: the session asks its questions first (unsaved changes, linked edits).</summary>
    private void CloseTab(DocumentTab tab)
    {
        if (tab.Session.TryClose()) RemoveTab(tab);
    }

    private void RemoveTab(DocumentTab tab, bool closingWindow = false)
    {
        int index = tabs.IndexOf(tab);
        if (index < 0) return;
        tabs.Remove(tab);
        Documents.Children.Remove(tab.View);
        tab.View.Session = null;
        if (closingWindow) return;
        if (tabs.Count == 0) { Close(); return; }
        TabStrip.SelectedIndex = Math.Min(index, tabs.Count - 1);
    }

    private void OnCloseTabClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DocumentTab tab) CloseTab(tab);
        e.Handled = true;
    }

    // ------------------------------------------------------------------------------------------------- keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.Tab && mods.HasFlag(ModifierKeys.Control))
        {
            SelectTab(mods.HasFlag(ModifierKeys.Shift) ? -1 : +1);
            e.Handled = true;
            return;
        }
        if (key == Key.F6 && TabStrip.SelectedItem is DocumentTab current)
        {
            // F6 moves between the canvas, the tools, the options bar, the side panel and the omnibar (Windows' pane
            // key), so Tab can hide the interface on the canvas as it does on the Mac without trapping the keyboard.
            current.View.FocusNextRegion(backwards: mods.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return;
        }
        if (key == Key.W && mods == ModifierKeys.Control) { CloseSelectedTab(); e.Handled = true; return; }
        if (key == Key.N && mods == ModifierKeys.Control) { ShowNewDocumentWindow(); e.Handled = true; return; }
        if (key == Key.O && mods == ModifierKeys.Control) { AppServices.Opener?.ShowOpenPanel(); e.Handled = true; return; }
        if (key == Key.OemComma && mods == ModifierKeys.Control) { OpenSettings("general"); e.Handled = true; return; }
        if (SelectedSession is not { } session) return;
        bool inText = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox;
        if (inText && Shortcuts.IsTextEditingShortcut(key, mods)) return;
        if (key == Key.Z && mods == ModifierKeys.Control) { session.Document.Undo(); e.Handled = true; return; }
        if (key == Key.Z && mods == (ModifierKeys.Control | ModifierKeys.Shift)) { session.Document.Redo(); e.Handled = true; return; }
        if (Shortcuts.Match(key, mods, WindowsCommands.Registry) is { } id)
        {
            if (session.CanPerform(id)) session.Perform(id);
            else System.Media.SystemSounds.Beep.Play();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------------------------------------- caption buttons

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateForWindowState()
    {
        bool max = WindowState == WindowState.Maximized;
        MaximizeGlyph.Symbol = max ? "window.restore" : "window.maximize";
        var name = Loc.T(max ? "Restore Down" : "Maximize");
        AutomationProperties.SetName(MaximizeButton, name);
        MaximizeButton.ToolTip = name;
        // A maximized window reaches past the screen by its resize frame: keep the content on screen.
        Root.Padding = max ? MaximizedInset() : new Thickness(0);
    }

    private Thickness MaximizedInset()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int frameX = GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
        int frameY = GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
        return new Thickness(frameX / dpi.DpiScaleX, frameY / dpi.DpiScaleY, frameX / dpi.DpiScaleX, frameY / dpi.DpiScaleY);
    }

    // ------------------------------------------------------------------------------------------------- Win32

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        hwnd = (HwndSource)PresentationSource.FromVisual(this)!;
        hwnd.AddHook(WndProc);
        var handle = hwnd.Handle;
        // Dark window frame and rounded corners on Windows 11; ignored where not supported.
        int on = 1, round = DWMWCP_ROUND;
        DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int border = (Theme.Edge.B << 16) | (Theme.Edge.G << 8) | Theme.Edge.R;   // COLORREF is 0x00BBGGRR
        DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    /// <summary>Snap Layouts: Windows 11 shows its layout flyout when the pointer rests on a window's maximize button,
    /// which it finds by asking WM_NCHITTEST. Answering HTMAXBUTTON there means the button's clicks arrive as
    /// non-client messages, so the hook also presses the button and shows its hover state.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST when OverMaximizeButton(lParam):
                SetMaximizeHover(true);
                handled = true;
                return new IntPtr(HTMAXBUTTON);
            case WM_NCMOUSELEAVE:
            case WM_NCHITTEST:
                SetMaximizeHover(false);
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt32() == HTMAXBUTTON:
                handled = true;
                return IntPtr.Zero;
            case WM_NCLBUTTONUP when wParam.ToInt32() == HTMAXBUTTON:
                ToggleMaximize();
                handled = true;
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private bool OverMaximizeButton(IntPtr lParam)
    {
        if (!MaximizeButton.IsVisible) return false;
        int x = (short)(lParam.ToInt64() & 0xffff), y = (short)((lParam.ToInt64() >> 16) & 0xffff);
        try
        {
            var p = MaximizeButton.PointFromScreen(new Point(x, y));
            return p.X >= 0 && p.Y >= 0 && p.X < MaximizeButton.ActualWidth && p.Y < MaximizeButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;   // not connected to a presentation source yet
        }
    }

    private void SetMaximizeHover(bool on)
    {
        if (maximizeHover == on) return;
        maximizeHover = on;
        MaximizeButton.Background = on ? (Brush)FindResource("Glimmer.RaisedBrush") : Brushes.Transparent;
    }

    private const int WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCMOUSELEAVE = 0x02A2, HTMAXBUTTON = 9;
    private const int SM_CXSIZEFRAME = 32, SM_CYSIZEFRAME = 33, SM_CXPADDEDBORDER = 92;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ------------------------------------------------------------------------------------------------- IDocumentHost

    public void ShowNewDocumentWindow()
    {
        var sheet = new Views.Sheets.NewDocumentView { Owner = this };
        sheet.ShowDialog();
    }

    public int DocumentCount => tabs.Count;

    public void ShowDocument(IEditorSession session)
    {
        if (tabs.FirstOrDefault(t => t.Session == session) is { } existing) { TabStrip.SelectedItem = existing; return; }
        AddDocument(session);
    }

    public void BringForward(IEditorSession session)
    {
        if (tabs.FirstOrDefault(t => t.Session == session) is { } tab) TabStrip.SelectedItem = tab;
        BringToFront();
    }

    /// <summary>The session closed itself (File › Close after its questions, a linked tab saved back): its tab goes.</summary>
    public void CloseDocument(IEditorSession session)
    {
        if (tabs.FirstOrDefault(t => t.Session == session) is { } tab) RemoveTab(tab);
    }

    public SaveChangesChoice AskToSaveChanges(IEditorSession session)
    {
        if (tabs.FirstOrDefault(t => t.Session == session) is { } tab) TabStrip.SelectedItem = tab;
        var name = session.ExplicitName ?? session.DocumentName;
        int answer = Views.Sheets.AlertWindow.Show(this, Loc.F("Do you want to save the changes made to “{0}”?", name),
            Loc.T("Your changes will be lost if you don’t save them."),
            [("Save", Views.Sheets.AlertRole.Default), ("Don’t Save", Views.Sheets.AlertRole.Normal), ("Cancel", Views.Sheets.AlertRole.Cancel)]);
        return answer switch { 0 => SaveChangesChoice.Save, 1 => SaveChangesChoice.DontSave, _ => SaveChangesChoice.Cancel };
    }

    public LinkedCloseChoice AskToCloseLinked(IEditorSession session, UiText message, UiText detail, bool canSaveBack)
    {
        if (tabs.FirstOrDefault(t => t.Session == session) is { } tab) TabStrip.SelectedItem = tab;
        int answer = Views.Sheets.AlertWindow.Show(this, Loc.T(message), Loc.T(detail),
            [(canSaveBack ? "Save Back" : "Save a Glimmer Copy…", Views.Sheets.AlertRole.Default), ("Keep for Later", Views.Sheets.AlertRole.Normal),
             ("Discard", Views.Sheets.AlertRole.Destructive), ("Cancel", Views.Sheets.AlertRole.Cancel)]);
        return answer switch { 0 => LinkedCloseChoice.SaveBack, 1 => LinkedCloseChoice.KeepForLater, 2 => LinkedCloseChoice.Discard, _ => LinkedCloseChoice.Cancel };
    }

    public void PresentError(UiText message) =>
        Views.Sheets.AlertWindow.Show(IsVisible ? this : null, "Glimmer", Loc.T(message), [("OK", Views.Sheets.AlertRole.Default)]);

    public void NoteRecentDocument(string path) => Platform.RecentDocuments.Note(path);

    /// <summary>A small window while something slow runs (developing a RAW file); disposing it closes the window.</summary>
    public IDisposable ShowProgress(UiText title, UiText message)
    {
        var window = new Views.Sheets.ProgressWindow(Loc.T(title), Loc.T(message)) { Owner = IsVisible ? this : null };
        window.Show();
        return window;
    }

    /// <summary>A document a stop left unsaved: open it again, or let it go (the Mac offers the same at launch).</summary>
    public void OfferRecovered(DocumentOpener opener, RecoveryStore.Entry entry)
    {
        int answer = Views.Sheets.AlertWindow.Show(IsVisible ? this : null, Loc.F("“{0}” has changes that were not saved.", entry.Info.Name),
            Loc.T("Glimmer stopped before they were saved. Open the document to keep them, or discard them."),
            [("Open", Views.Sheets.AlertRole.Default), ("Discard", Views.Sheets.AlertRole.Destructive)]);
        if (answer == 0) opener.OpenRecovered(entry);
        else if (answer == 1) opener.DiscardRecovered(entry);
        // Closed without an answer: the copy stays and is offered again next time.
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // Every tab answers its own question (unsaved changes, linked edits); one Cancel keeps the window.
        foreach (var tab in tabs.ToList())
        {
            TabStrip.SelectedItem = tab;
            if (!tab.Session.TryClose()) { e.Cancel = true; return; }
            RemoveTab(tab, closingWindow: true);
        }
        if (AppServices.Opener is { } opener && !opener.PrepareToQuit()) e.Cancel = true;
    }

    /// <summary>Another copy of Glimmer handed files over: come forward, restored if minimised.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;      // the classic way past Windows' focus-stealing guard, allowed by the other copy
        Topmost = false;
        Focus();
    }

    public void ShowWelcomeWindow() => new Views.Sheets.WelcomeView { Owner = this }.ShowDialog();

    public void ShowAgentSplash()
    {
        if (AppServices.AgentServer is { } server) new Views.Sheets.AgentSplashWindow(server) { Owner = this }.ShowDialog();
    }

    public void ShowLinkedWaiting()
    {
        if (AppServices.LinkedFiles is { } journal) new Views.Sheets.LinkedWaitingSheet(journal) { Owner = this }.ShowDialog();
    }
    public void OpenSettings(string pane) => Views.Settings.SettingsWindow.Show(this, pane);
}
