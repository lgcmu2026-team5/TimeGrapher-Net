using System.Globalization;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Pure formatting/derivation logic behind the Vario readout grid, kept out of
/// the renderer so it is unit-testable without a live plot control.
/// </summary>
internal static class VarioReadout
{
    /// <summary>Shown while a reading does not exist yet.</summary>
    public const string Missing = "—";

    /// <summary>Formats a reading with its unit, or an em dash when absent.</summary>
    public static string Format(double? value, string numericFormat, string unit) =>
        value is double v
            ? v.ToString(numericFormat, CultureInfo.InvariantCulture) + unit
            : Missing;

    /// <summary>Elapsed measurement time as mm:ss, switching to h:mm:ss from one hour.</summary>
    public static string FormatElapsed(double seconds)
    {
        long total = (long)Math.Max(0.0, seconds);
        long h = total / 3600;
        long m = total % 3600 / 60;
        long s = total % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", m, s);
    }

    /// <summary>
    /// Review-cursor reading: the value the series had captured at
    /// <paramref name="timeS"/> (newest point at or before it). Null when the
    /// series is empty or the time precedes the first captured point.
    /// </summary>
    public static double? ValueAt(MetricsHistorySeries series, double timeS)
    {
        for (int i = series.X.Count - 1; i >= 0; i--)
        {
            if (series.X[i] <= timeS)
            {
                return series.Y[i];
            }
        }

        return null;
    }
}
