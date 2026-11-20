// Ported from App/EditorSession+Helpers.swift (and the small helpers other App files share).
using System.Globalization;
using Glimmer.Core;

namespace Glimmer.Editor;

/// <summary>RegionOps is internal to GlimmerRender; tools only need expand.</summary>
internal static class RegionOpsPublic
{
    public static PixelRect Expand(PixelRect r, int n) => new(r.X - n, r.Y - n, r.Width + 2 * n, r.Height + 2 * n);
}

internal static class EditorHelpers
{
    public static double Clamped(this double v, double lo, double hi) => Math.Min(Math.Max(v, lo), hi);
    public static int Clamped(this int v, int lo, int hi) => Math.Min(Math.Max(v, lo), hi);

    /// <summary>Swift's rounded(): to nearest, halves away from zero.</summary>
    public static double Rounded(this double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    public static (int X, int Y) RoundedInt(Vec2 v) => ((int)Rounded(v.X), (int)Rounded(v.Y));

    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;

    public static Vec2 Normalized(Vec2 v)
    {
        double l = v.Length;
        return l > 0 ? v / l : v;
    }

    /// <summary>Byte counts in words, as macOS's ByteCountFormatter with .file writes them (decimal units).</summary>
    public static string FileSize(long bytes)
    {
        if (bytes == 0) return "Zero KB";
        if (Math.Abs(bytes) < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        int unit = -1;
        while (Math.Abs(value) >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        int decimals = unit == 0 ? 0 : unit == 1 ? 1 : 2;
        string text = decimals == 0
            ? Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
            : Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture);
        return $"{text} {units[unit]}";
    }
}
