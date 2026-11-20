// Ported from App/AppCommands.swift (GlimmerCommands) and the menus App/GlimmerApp.swift adds.
// The menus are the editor's AppMenus (GlimmerCommands.body as data) over WindowsCommands.Registry, never written by
// hand here, and every item goes to the selected tab's session. The Mac's system menus become their Windows
// counterparts: Settings… and Exit at the end of File, then Window and Help.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Shell;

public sealed class MenuBuilder(Func<IEditorSession?> session, IDocumentHost host, Window window)
{
    private static CommandRegistry Registry => WindowsCommands.Registry;

    /// <summary>In a text field these act on the text, as the Mac's system menu items do there.</summary>
    private static readonly Dictionary<CommandID, RoutedUICommand> TextCommands = new()
    {
        [CommandID.Undo] = ApplicationCommands.Undo,
        [CommandID.Redo] = ApplicationCommands.Redo,
        [CommandID.Cut] = ApplicationCommands.Cut,
        [CommandID.Copy] = ApplicationCommands.Copy,
        [WindowsCommands.Paste] = ApplicationCommands.Paste,
        [CommandID.ClearPixels] = ApplicationCommands.Delete,
        [CommandID.SelectAll] = ApplicationCommands.SelectAll,
    };

    /// <summary>A command's place in a menu: its id, and whether it shows a checkmark.</summary>
    private sealed record Entry(CommandID Id, bool Toggle);

    /// <summary>File, Edit, Image, Layer, Select, Filter, AI, View (the Mac's menu bar order), then Window and Help.</summary>
    public void Build(Menu menu)
    {
        menu.Items.Clear();
        foreach (var (title, entries) in AppMenus.Menus)
        {
            var items = new List<object>(entries.Select(Item));
            // The Mac keeps these in the app menu, which Windows doesn't have.
            if (title == "File")
                items.AddRange([new Separator(), Action("Settings…", "Ctrl+,", () => host.OpenSettings("general")), new Separator(),
                                Action("Exit", "Alt+F4", window.Close)]);
            menu.Items.Add(Top(title, items));
        }
        menu.Items.Add(WindowMenu());
        menu.Items.Add(HelpMenu());
    }

    private MenuItem WindowMenu()
    {
        var main = window as MainWindow;
        return Top("Window", [
            Action("Minimize", null, () => window.WindowState = WindowState.Minimized),
            Action("Maximize", null, () => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized),
            new Separator(),
            Action("Next Tab", "Ctrl+Tab", () => main?.SelectTab(+1)),
            Action("Previous Tab", "Ctrl+Shift+Tab", () => main?.SelectTab(-1)),
        ]);
    }

    private MenuItem HelpMenu() => Top("Help", [
        Action("Welcome to Glimmer", null, host.ShowWelcomeWindow),
        Action("Agent Access…", null, host.ShowAgentSplash),
        new Separator(),
        Action("Glimmer Manual", null, () => Open("https://glimmer.brattoo.com/manual/")),
        Action("Frequently Asked Questions", null, () => Open("https://glimmer.brattoo.com/faq.html")),
    ]);

    // ------------------------------------------------------------------------------------------------- items

    private object Item(AppMenus.Entry entry) => entry switch
    {
        AppMenus.Entry.Command c => CommandItem(c.Id, toggle: false),
        AppMenus.Entry.Toggle t => CommandItem(t.Id, toggle: true),
        AppMenus.Entry.Submenu sub => Top(sub.Title, sub.Entries.Select(Item)),
        _ => new Separator(),
    };

    private MenuItem CommandItem(CommandID id, bool toggle)
    {
        var d = Registry.Descriptor(id);
        return new MenuItem
        {
            Header = Loc.T(d?.Title ?? id.RawValue),
            InputGestureText = d?.Shortcut is { } s ? Shortcuts.Display(s) : "",
            Command = new SessionCommand(session, id, TextCommands.GetValueOrDefault(id)),
            Tag = new Entry(id, toggle),
        };
    }

    private MenuItem Top(string title, IEnumerable<object> items)
    {
        var m = new MenuItem { Header = Loc.T(title) };
        // Fits the 40 px title bar (Themes/Glimmer.xaml says why); a local value, so Fluent's role trigger can't replace it.
        if (Application.Current?.TryFindResource("Glimmer.TitleBarMenuItemTemplate") is ControlTemplate compact) m.Template = compact;
        else Platform.CrashLog.Note("Glimmer.TitleBarMenuItemTemplate not found: the menu bar keeps Fluent's taller items.");
        foreach (var i in items) m.Items.Add(i);
        // Titles and checkmarks are read when the menu opens (Swift: Toggle(isOn: session?.isChecked(id)), and the
        // titles that name the document: "Undo {0}", "Save Back to {0}").
        m.SubmenuOpened += (_, e) =>
        {
            if (e.OriginalSource != m) return;
            var s = session();
            bool inText = SessionCommand.TextField() is not null;
            foreach (var item in m.Items.OfType<MenuItem>())
            {
                if (item.Tag is not Entry entry) continue;
                var title = inText && TextCommands.ContainsKey(entry.Id) ? null : s?.Title(entry.Id);
                item.Header = title is not null ? Loc.T(title) : Loc.T(Registry.Descriptor(entry.Id)?.Title ?? entry.Id.RawValue);
                item.IsChecked = entry.Toggle && s?.IsChecked(entry.Id) == true;
            }
        };
        return m;
    }

    private static MenuItem Action(string title, string? gesture, Action action)
    {
        var m = new MenuItem { Header = Loc.T(title), InputGestureText = gesture ?? "" };
        m.Click += (_, _) => action();
        return m;
    }

    private static void Open(string url) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}

/// <summary>A registry command as a WPF command for the selected tab's session (Swift: session?.perform(command.id),
/// disabled unless session?.canPerform). With a text command, a focused text field gets that instead.</summary>
public sealed class SessionCommand(Func<IEditorSession?> session, CommandID id, RoutedUICommand? textCommand = null) : ICommand
{
    public CommandID Id => id;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
    {
        if (textCommand is not null && TextField() is { } field) return textCommand.CanExecute(null, field);
        return session() is { } s ? s.CanPerform(id) : WithoutDocument(id) is not null;
    }

    public void Execute(object? parameter)
    {
        if (textCommand is not null && TextField() is { } field)
        {
            textCommand.Execute(null, field);
            return;
        }
        if (session() is { } s) s.Perform(id);
        else WithoutDocument(id)?.Invoke();
    }

    /// <summary>What File's items that need no document do while none is open.</summary>
    private static Action? WithoutDocument(CommandID id) =>
        id == CommandID.NewDocument ? () => AppServices.Documents?.ShowNewDocumentWindow()
        : id == CommandID.OpenDocument ? () => AppServices.Opener?.ShowOpenPanel()
        : id == WindowsCommands.OpenPackageFolder ? () => AppServices.Opener?.OpenPackageFolder()
        : id == WindowsCommands.LinkedFilesWaiting ? () => AppServices.Documents?.ShowLinkedWaiting()
        : null;

    /// <summary>The focused text field, if any. A menu takes keyboard focus while it is open, so the window's own focused
    /// element (its focus scope) is asked as well.</summary>
    internal static IInputElement? TextField()
    {
        var focused = Keyboard.FocusedElement;
        if (focused is not (TextBoxBase or PasswordBox) && Application.Current?.MainWindow is { } w) focused = FocusManager.GetFocusedElement(w);
        return focused is TextBoxBase or PasswordBox ? focused : null;
    }
}
