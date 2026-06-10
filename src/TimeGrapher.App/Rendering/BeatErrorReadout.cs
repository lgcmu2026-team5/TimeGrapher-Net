using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Pure formatting behind the Beat Error Display numeric panel, kept out of the
/// renderer so it is unit-testable without a live control tree. Covers the four
/// plan readings (rate, amplitude, signed beat error, BPH) plus the derived
/// timing measures (DiffTicTac / DiffPeriod / AvgPeriod) the plan asks the GUI
/// to surface — this panel is their first GUI surface.
/// </summary>
internal static class BeatErrorReadout
{
    private const string SignedMsFormat = "+0.00;-0.00;0.00";

    /// <summary>Panel labels; <see cref="Values"/> returns matching positions.</summary>
    public static readonly string[] Labels =
    {
        "RATE", "AMPLITUDE", "BEAT ERROR", "BPH",
        "DIFF TIC-TAC", "DIFF PERIOD", "AVG PERIOD",
    };

    /// <summary>Formatted readings in <see cref="Labels"/> order (em dash when absent).</summary>
    public static string[] Values(BeatMetricsHistorySnapshot snapshot)
    {
        DerivedTimingMeasures derived = snapshot.Derived;
        return new[]
        {
            VarioReadout.Format(snapshot.RateValid ? snapshot.RateSPerDay : null, "+0.0;-0.0;0.0", " s/d"),
            VarioReadout.Format(snapshot.AmplitudeValid ? snapshot.AmplitudeDeg : null, "0", "°"),
            VarioReadout.Format(snapshot.BeatErrorValid ? snapshot.BeatErrorSignedMs : null, SignedMsFormat, " ms"),
            VarioReadout.Format(snapshot.Bph > 0 ? snapshot.Bph : (double?)null, "0", " bph"),
            VarioReadout.Format(derived.DiffTicTacValid ? derived.DiffTicTacMs : null, SignedMsFormat, " ms"),
            VarioReadout.Format(derived.DiffPeriodValid ? derived.DiffPeriodMs : null, SignedMsFormat, " ms"),
            VarioReadout.Format(derived.AvgPeriodValid ? derived.AvgPeriodMs : null, SignedMsFormat, " ms"),
        };
    }
}
