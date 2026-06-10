namespace TimeGrapher.Core.Shared;

/// <summary>
/// One decimated history series (X ascending, Y bucket averages, YMin/YMax bucket
/// extremes). Lists are immutable once published; the same instance may be shared
/// across many frames.
/// </summary>
public sealed class MetricsHistorySeries
{
    public static readonly MetricsHistorySeries Empty = new();

    public IReadOnlyList<double> X { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> Y { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> YMin { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> YMax { get; init; } = Array.Empty<double>();
}

/// <summary>
/// Cumulative beat-metrics history snapshot carried by every frame. Because the
/// render scheduler coalesces frames latest-wins, per-beat data must accumulate in
/// Core and travel as a cumulative snapshot: dropping intermediate frames then
/// loses nothing. Rebuilt at most every <see cref="TimeGrapher.Core.Metrics.BeatMetricsHistory"/>
/// snapshot interval; in between, frames share the same immutable instance
/// (the rate-series sharing pattern).
/// </summary>
public sealed class BeatMetricsHistorySnapshot
{
    /// <summary>Increments whenever snapshot content changed; consumers can skip re-rendering on equal versions.</summary>
    public ulong Version { get; init; }

    /// <summary>Rate (s/d) over elapsed time (s).</summary>
    public MetricsHistorySeries Rate { get; init; } = MetricsHistorySeries.Empty;

    /// <summary>Amplitude tic/toc pair averages (deg) over elapsed time (s).</summary>
    public MetricsHistorySeries Amplitude { get; init; } = MetricsHistorySeries.Empty;

    /// <summary>Signed beat error (ms) over elapsed time (s).</summary>
    public MetricsHistorySeries BeatError { get; init; } = MetricsHistorySeries.Empty;

    public DerivedTimingMeasures Derived { get; init; }

    /// <summary>Latest instantaneous readings (the "current" column of stability views).</summary>
    public bool RateValid { get; init; }
    public double RateSPerDay { get; init; }
    public bool AmplitudeValid { get; init; }
    public double AmplitudeDeg { get; init; }
    public bool BeatErrorValid { get; init; }
    public double BeatErrorSignedMs { get; init; }

    /// <summary>Stream time (s) of the newest recorded beat.</summary>
    public double LatestTimeS { get; init; }
}
