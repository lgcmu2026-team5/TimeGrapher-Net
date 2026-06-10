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
/// Running min/max/mean/population-sigma summary of one measure since
/// measurement start (built by <see cref="TimeGrapher.Core.Metrics.RunningStats"/>).
/// Valid is false until the first sample; the numeric fields are 0 while invalid.
/// Fed per beat in Core, so the statistics stay exact even though the plotted
/// series decimate, and survive latest-wins frame coalescing.
/// </summary>
public readonly record struct StatsSummary(
    bool Valid,
    double Min,
    double Max,
    double Mean,
    double Sigma,
    long Count);

/// <summary>
/// Running aggregates of one measured watch test position: rate (s/d),
/// amplitude (deg, tic/toc pair averages) and signed beat error (ms) of every
/// beat tagged with that position. Only positions with at least one recorded
/// measurement appear in <see cref="BeatMetricsHistorySnapshot.Positions"/>,
/// so the list is bounded by the six standard positions.
/// </summary>
public sealed record PositionSummary(
    WatchPosition Position,
    StatsSummary Rate,
    StatsSummary Amplitude,
    StatsSummary BeatError);

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
    /// <summary>Beats-per-hour of the newest recorded beat (0 until the first synced beat).</summary>
    public int Bph { get; init; }
    public bool AmplitudeValid { get; init; }
    public double AmplitudeDeg { get; init; }
    public bool BeatErrorValid { get; init; }
    public double BeatErrorSignedMs { get; init; }

    /// <summary>Stream time (s) of the newest recorded beat.</summary>
    public double LatestTimeS { get; init; }

    /// <summary>Running rate (s/d) stability statistics since start (Vario display).</summary>
    public StatsSummary RateStats { get; init; }

    /// <summary>Running amplitude (deg, tic/toc pair averages) stability statistics since start.</summary>
    public StatsSummary AmplitudeStats { get; init; }

    /// <summary>Watch test position new measurements are currently tagged with.</summary>
    public WatchPosition ActivePosition { get; init; }

    /// <summary>
    /// Per-position aggregates of every position measured so far, in
    /// <see cref="WatchPositions.All"/> order (bounded by <see cref="WatchPositions.Count"/> entries).
    /// Rebuilt together with the snapshot.
    /// </summary>
    public IReadOnlyList<PositionSummary> Positions { get; init; } = Array.Empty<PositionSummary>();
}
