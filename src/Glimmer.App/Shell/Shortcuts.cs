// The registry's key equivalents as Windows writes and reads them. The registry already holds them in Windows terms
// (KeyShortcut: Ctrl, Alt, Shift, mapped from the Mac's the way Photoshop maps its own; see CommandRegistry.Standard).
using System.Windows.Input;
using Glimmer.App.Controls.Canvas;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Shell;

public static class Shortcuts
{
    /// <summary>"Ctrl+Shift+N", for a menu item's InputGestureText and the palette (the key named in the person's language).</summary>
    public static string Display(KeyShortcut s)
    {
        var parts = new List<string>(4);
        if (s.Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (s.Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (s.Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(Localization.Loc.KeyName(s.Key));
        return string.Join("+", parts);
    }

    /// <summary>The modifiers of a key event in the registry's terms.</summary>
    public static KeyModifiers ModifiersOf(ModifierKeys keys)
    {
        var m = KeyModifiers.None;
        if (keys.HasFlag(ModifierKeys.Control)) m |= KeyModifiers.Control;
        if (keys.HasFlag(ModifierKeys.Alt)) m |= KeyModifiers.Alt;
        if (keys.HasFlag(ModifierKeys.Shift)) m |= KeyModifiers.Shift;
        return m;
    }

    /// <summary>The command whose key equivalent this key press is, if any. The key is matched by the character it
    /// types on the current layout, as the Mac matches key equivalents, so "[" works wherever the keyboard has it.</summary>
    public static CommandID? Match(Key key, ModifierKeys modifiers, CommandRegistry registry)
    {
        char ch = KeyboardChars.CharFor(key);
        if (ch == '\0') return null;
        var mods = ModifiersOf(modifiers);
        if (!mods.HasFlag(KeyModifiers.Control)) return null;   // every registry shortcut has Ctrl
        foreach (var d in registry.Descriptors)
        {
            if (d.Shortcut is not { } s) continue;
            if (char.ToLowerInvariant(s.Key) != char.ToLowerInvariant(ch)) continue;
            if (s.Modifiers == mods) return d.Id;
        }
        // Ctrl+Plus on the key that types "=" unshifted ("+" needs Shift on most layouts): Zoom In.
        if (ch == '=' && (mods == KeyModifiers.Control || mods == (KeyModifiers.Control | KeyModifiers.Shift))) return CommandID.ZoomIn;
        if (key == Key.Add && mods == KeyModifiers.Control) return CommandID.ZoomIn;
        if (key == Key.Subtract && mods == KeyModifiers.Control) return CommandID.ZoomOut;
        return null;
    }

    /// <summary>Shortcuts a text field keeps for itself (Swift: "⌘A stays with Edit › Select All so text fields keep it").</summary>
    public static bool IsTextEditingShortcut(Key key, ModifierKeys modifiers) =>
        modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift)
        && key is Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.Left or Key.Right or Key.Back or Key.Delete or Key.Home or Key.End;
}
