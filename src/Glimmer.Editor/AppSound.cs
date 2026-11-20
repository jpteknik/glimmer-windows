// Ported from App/AppSound.swift

namespace Glimmer.Editor;

/// <summary>The app's own sounds, in one place, so a test copy can be silent by construction (the series' rule,
/// 2026-11-16): a test run sets "quiet", and then nothing here sounds. The system volume is never touched.
/// The sound itself is the app's (<see cref="IAppSound"/>).</summary>
public static class AppSound
{
    public static bool IsQuiet(IAppSettings settings) => settings.Get(Preferences.QuietKey, false);

    public static void Beep(EditorEnvironment environment)
    {
        if (IsQuiet(environment.Settings)) return;
        environment.Sound?.Beep();
    }
}
