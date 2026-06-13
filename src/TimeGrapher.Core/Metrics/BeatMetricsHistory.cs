using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Metrics;

/// <summary>
/// Accumulates the numeric per-beat samples emitted by <see cref="WatchMetrics"/>
/// into bounded decimating series (rate, amplitude, beat error) plus the latest
/// derived measures and instantaneous readings, and publishes them as immutable
/// <see cref="BeatMetricsHistorySnapshot"/>s. Beat-data rebuilds happen at most
/// once per <see cref="SnapshotMinIntervalS"/> of stream time; a user state
/// change (active position, sequence reset) bypasses that throttle once, since
/// stream time stands still while no synced beats arrive. Unchanged or
/// in-between requests return the same shared instance, so per-frame cost
/// stays flat.
/// </summary>
public sealed class BeatMetricsHistory
{
    public const int DefaultSeriesCapacity = 4096;
    public const double SnapshotMinIntervalS = 0.5;

    private readonly DecimatingSeries _rate;
    private readonly DecimatingSeries _amplitude;
    private readonly DecimatingSeries _beatError;

    // Vario stability statistics: fed per beat (before decimation), so min/max/
    // mean/sigma stay exact however coarse the plotted series become. They cover a
    // single watch position and restart on a position change (see SetActivePosition),
    // because mixing positions would inflate the spread and misreport stability.
    private readonly RunningStats _rateStats = new();
    private readonly RunningStats _amplitudeStats = new();

    // Per-position aggregates, indexed by WatchPosition ordinal. A slot is
    // created on the first measurement tagged with that position, so storage is
    // bounded by the WatchPositions.Count-entry catalog (10) regardless of run length.
    private readonly PositionAggregate?[] _positionAggregates =
        new PositionAggregate?[WatchPositions.Count];
    private WatchPosition _activePosition = WatchPosition.CH;

    private DerivedTimingMeasures _derived;
    private bool _rateValid;
    private double _rateSPerDay;
    private int _bph;
    private bool _amplitudeValid;
    private double _amplitudeDeg;
    private bool _beatErrorValid;
    private double _beatErrorSignedMs;
    private double _latestTimeS;

    // Baseline for the Vario stats' elapsed clock, re-anchored on a position change
    // so the displayed elapsed time counts from when the current position started.
    private double _statsStartTimeS;

    private bool _dirty;

    // State changes (active position, sequence reset) bypass the stream-time
    // throttle: it is keyed to _latestTimeS, which only advances with synced
    // beats, so a position picked while no beats arrive (watch off the mic)
    // would otherwise never publish and the position UI would stay stale.
    private bool _publishImmediately;
    private ulong _version;
    private BeatMetricsHistorySnapshot? _snapshot;
    private double _lastSnapshotTimeS;

    public BeatMetricsHistory(int seriesCapacity = DefaultSeriesCapacity)
    {
        _rate = new DecimatingSeries(seriesCapacity);
        _amplitude = new DecimatingSeries(seriesCapacity);
        _beatError = new DecimatingSeries(seriesCapacity);
    }

    /// <summary>
    /// Tags subsequent measurements with the given test position. Analysis-thread
    /// only (the UI request travels through the projector's volatile knob); a
    /// change re-stamps the next snapshot.
    /// </summary>
    public void SetActivePosition(WatchPosition position)
    {
        if (_activePosition == position)
        {
            return;
        }

        _activePosition = position;
        // Vario reports stability for the current position only, so its running
        // statistics and elapsed clock restart when the watch turns to a new
        // position. The live series and per-position aggregates are untouched.
        _rateStats.Reset();
        _amplitudeStats.Reset();
        _statsStartTimeS = _latestTimeS;
        _dirty = true;
        _publishImmediately = true;
    }

    public void Record(WatchMetricsUpdate update)
    {
        if (update.BeatTimingSampleUpdated)
        {
            BeatTimingSample sample = update.BeatTimingSample;
            _latestTimeS = sample.TimeS;
            _bph = sample.Bph;

            if (sample.RateValid)
            {
                _rate.Add(sample.TimeS, sample.RateSPerDay);
                _rateStats.Add(sample.RateSPerDay);
                ActiveAggregate().Rate.Add(sample.RateSPerDay);
                _rateValid = true;
                _rateSPerDay = sample.RateSPerDay;
            }

            if (sample.BeatErrorValid)
            {
                _beatError.Add(sample.TimeS, sample.BeatErrorSignedMs);
                ActiveAggregate().BeatError.Add(sample.BeatErrorSignedMs);
                _beatErrorValid = true;
                _beatErrorSignedMs = sample.BeatErrorSignedMs;
            }

            _dirty = true;
        }

        if (update.AmplitudeSampleUpdated && update.AmplitudeSample.PairAverageUpdated)
        {
            AmplitudeSample sample = update.AmplitudeSample;
            _amplitude.Add(sample.TimeS, sample.PairAverageDeg);
            _amplitudeStats.Add(sample.PairAverageDeg);
            ActiveAggregate().Amplitude.Add(sample.PairAverageDeg);
            _amplitudeValid = true;
            _amplitudeDeg = sample.PairAverageDeg;
            _latestTimeS = Math.Max(_latestTimeS, sample.TimeS);
            _dirty = true;
        }

        if (update.DerivedMeasuresUpdated)
        {
            _derived = update.DerivedMeasures;
            _dirty = true;
        }
    }

    public void Reset()
    {
        _rate.Reset();
        _amplitude.Reset();
        _beatError.Reset();
        _rateStats.Reset();
        _amplitudeStats.Reset();
        // The active position is the watch's physical orientation, not run
        // data, so it survives the reset; only its accumulated stats clear.
        Array.Clear(_positionAggregates);
        _derived = default;
        _rateValid = false;
        _bph = 0;
        _amplitudeValid = false;
        _beatErrorValid = false;
        _latestTimeS = 0.0;
        _statsStartTimeS = 0.0;
        _dirty = false;
        _publishImmediately = false;
        _snapshot = null;
        _lastSnapshotTimeS = 0.0;
    }

    /// <summary>
    /// Clears only the per-position aggregates (live series and overall stats
    /// keep accumulating). The multi-position sequence flow restarts position
    /// statistics mid-run through this; analysis-thread only (the UI request
    /// travels through the projector's volatile knob, the SetActivePosition flow).
    /// </summary>
    public void ResetPositionAggregates()
    {
        Array.Clear(_positionAggregates);
        _dirty = true;
        _publishImmediately = true;
    }

    /// <summary>
    /// Latest snapshot, rebuilt only when content changed and either the
    /// stream-time throttle elapsed or a state change requested an immediate
    /// publish (the first build is immediate). Null until the first beat -
    /// unless a state change precedes it, which publishes a position-only
    /// snapshot with empty series.
    /// </summary>
    public BeatMetricsHistorySnapshot? CurrentSnapshot()
    {
        if (!_dirty && _snapshot != null)
        {
            return _snapshot;
        }

        if (_snapshot != null &&
            !_publishImmediately &&
            _latestTimeS - _lastSnapshotTimeS < SnapshotMinIntervalS)
        {
            return _snapshot;
        }

        if (!_dirty)
        {
            return _snapshot;
        }

        _version++;
        _snapshot = new BeatMetricsHistorySnapshot
        {
            Version = _version,
            Rate = BuildSeries(_rate),
            Amplitude = BuildSeries(_amplitude),
            BeatError = BuildSeries(_beatError),
            Derived = _derived,
            RateValid = _rateValid,
            RateSPerDay = _rateSPerDay,
            Bph = _bph,
            AmplitudeValid = _amplitudeValid,
            AmplitudeDeg = _amplitudeDeg,
            BeatErrorValid = _beatErrorValid,
            BeatErrorSignedMs = _beatErrorSignedMs,
            LatestTimeS = _latestTimeS,
            StatsElapsedS = Math.Max(0.0, _latestTimeS - _statsStartTimeS),
            RateStats = Summarize(_rateStats),
            AmplitudeStats = Summarize(_amplitudeStats),
            ActivePosition = _activePosition,
            Positions = BuildPositionSummaries(),
        };
        _lastSnapshotTimeS = _latestTimeS;
        _dirty = false;
        _publishImmediately = false;
        return _snapshot;
    }

    private sealed class PositionAggregate
    {
        public readonly RunningStats Rate = new();
        public readonly RunningStats Amplitude = new();
        public readonly RunningStats BeatError = new();
    }

    private PositionAggregate ActiveAggregate()
    {
        return _positionAggregates[(int)_activePosition] ??= new PositionAggregate();
    }

    private IReadOnlyList<PositionSummary> BuildPositionSummaries()
    {
        int measured = 0;
        foreach (PositionAggregate? aggregate in _positionAggregates)
        {
            if (aggregate != null)
            {
                measured++;
            }
        }

        if (measured == 0)
        {
            return Array.Empty<PositionSummary>();
        }

        // Rebuilt with the snapshot (at most every SnapshotMinIntervalS), so
        // the allocation stays off the per-beat path and is bounded by WatchPositions.Count rows.
        var summaries = new List<PositionSummary>(measured);
        foreach (WatchPosition position in WatchPositions.All)
        {
            if (_positionAggregates[(int)position] is { } aggregate)
            {
                summaries.Add(new PositionSummary(
                    position,
                    Summarize(aggregate.Rate),
                    Summarize(aggregate.Amplitude),
                    Summarize(aggregate.BeatError)));
            }
        }

        return summaries;
    }

    private static StatsSummary Summarize(RunningStats stats) => new(
        stats.Count > 0, stats.Min, stats.Max, stats.Mean, stats.Sigma, stats.Count);

    private static MetricsHistorySeries BuildSeries(DecimatingSeries source)
    {
        if (source.Count == 0)
        {
            return MetricsHistorySeries.Empty;
        }

        var x = new List<double>(source.Count);
        var y = new List<double>(source.Count);
        var yMin = new List<double>(source.Count);
        var yMax = new List<double>(source.Count);
        source.SnapshotTo(x, y, yMin, yMax);
        return new MetricsHistorySeries { X = x, Y = y, YMin = yMin, YMax = yMax };
    }
}
