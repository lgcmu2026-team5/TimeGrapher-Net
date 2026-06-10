using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Pure display policy for the Vario value gauges: the acceptable (green)
/// ranges and the gauge X-window derivation, kept free of UI types so both are
/// unit-testable. Rate ±10 s/d is this project's acceptance band for a healthy
/// mechanical watch; amplitude reuses the project plan's 270–300° normal
/// operating range — the same band <see cref="TraceAlertEvaluator"/> alerts
/// on, so the two displays agree by construction.
/// </summary>
internal static class VarioGaugePolicy
{
    public const double RateAcceptMinSPerDay = -10.0;
    public const double RateAcceptMaxSPerDay = 10.0;

    public const double AmplitudeAcceptMinDeg = TraceAlertEvaluator.AmplitudeMinDeg;
    public const double AmplitudeAcceptMaxDeg = TraceAlertEvaluator.AmplitudeMaxDeg;

    /// <summary>Fraction of the spanned width added on each side so edge markers stay visible.</summary>
    public const double GaugePaddingFraction = 0.05;

    /// <summary>
    /// Gauge X window: the acceptable range, widened to include the measured
    /// min/max and the current reading, then padded on both sides.
    /// </summary>
    public static (double Lo, double Hi) GaugeRange(
        double acceptMin, double acceptMax, StatsSummary stats, double? current)
    {
        double lo = acceptMin;
        double hi = acceptMax;

        if (stats.Valid)
        {
            lo = Math.Min(lo, stats.Min);
            hi = Math.Max(hi, stats.Max);
        }

        if (current is double value)
        {
            lo = Math.Min(lo, value);
            hi = Math.Max(hi, value);
        }

        double pad = (hi - lo) * GaugePaddingFraction;
        return (lo - pad, hi + pad);
    }
}
