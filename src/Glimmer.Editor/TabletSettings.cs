// Ported from App/TabletSettings.swift

namespace Glimmer.Editor;

/// <summary>Pen-tablet preferences (Settings › Tablet).</summary>
public static class TabletSettings
{
    public const string PressureCurveKey = "tablet.pressureCurve";
    public const string UseTiltKey = "tablet.useTilt";

    /// <summary>Exponent applied to raw pressure: below 1 is softer (less force for the same effect), above 1 firmer.</summary>
    public static double PressureExponent(IAppSettings settings)
    {
        double v = settings.Get(PressureCurveKey, 0.0);
        return v > 0 ? v : 1;
    }

    public static bool UseTilt(IAppSettings settings) => settings.Get(UseTiltKey, true);

    public static double Curve(double pressure, double exponent) => Math.Pow(Math.Min(Math.Max(pressure, 0), 1), exponent);

    public static double Curve(double pressure, IAppSettings settings) => Curve(pressure, PressureExponent(settings));
}
