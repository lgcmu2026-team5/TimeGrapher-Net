using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Metrics;

/// <summary>
/// Accumulates the numeric per-beat samples emitted by <see cref="WatchMetrics"/>
/// into bounded decimating series (rate, amplitude, beat error) plus the latest
/// derived measures and instantaneous readings, and publishes them as immutable
/// <see cref="BeatMetricsHistorySnapshot"/>s. Snapshots are rebuilt at most once
/// per <see cref="SnapshotMinIntervalS"/> of stream time; unchanged or in-between
/// requests return the same shared instance, so per-frame cost stays flat.
/// </summary>
public sealed class BeatMetricsHistory
{
    public const int DefaultSeriesCapacity = 4096;
    public const double SnapshotMinIntervalS = 0.5;

    private readonly DecimatingSeries _rate;
    private readonly DecimatingSeries _amplitude;
    private readonly DecimatingSeries _beatError;

    private DerivedTimingMeasures _derived;
    private bool _rateValid;
    private double _rateSPerDay;
    private bool _amplitudeValid;
    private double _amplitudeDeg;
    private bool _beatErrorValid;
    private double _beatErrorSignedMs;
    private double _latestTimeS;

    private bool _dirty;
    private ulong _version;
    private BeatMetricsHistorySnapshot? _snapshot;
    private double _lastSnapshotTimeS;

    public BeatMetricsHistory(int seriesCapacity = DefaultSeriesCapacity)
    {
        _rate = new DecimatingSeries(seriesCapacity);
        _amplitude = new DecimatingSeries(seriesCapacity);
        _beatError = new DecimatingSeries(seriesCapacity);
    }

    public void Record(WatchMetricsUpdate update)
    {
        if (update.BeatTimingSampleUpdated)
        {
            BeatTimingSample sample = update.BeatTimingSample;
            _latestTimeS = sample.TimeS;

            if (sample.RateValid)
            {
                _rate.Add(sample.TimeS, sample.RateSPerDay);
                _rateValid = true;
                _rateSPerDay = sample.RateSPerDay;
            }

            if (sample.BeatErrorValid)
            {
                _beatError.Add(sample.TimeS, sample.BeatErrorSignedMs);
                _beatErrorValid = true;
                _beatErrorSignedMs = sample.BeatErrorSignedMs;
            }

            _dirty = true;
        }

        if (update.AmplitudeSampleUpdated && update.AmplitudeSample.PairAverageUpdated)
        {
            AmplitudeSample sample = update.AmplitudeSample;
            _amplitude.Add(sample.TimeS, sample.PairAverageDeg);
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
        _derived = default;
        _rateValid = false;
        _amplitudeValid = false;
        _beatErrorValid = false;
        _latestTimeS = 0.0;
        _dirty = false;
        _snapshot = null;
        _lastSnapshotTimeS = 0.0;
    }

    /// <summary>
    /// Latest snapshot, rebuilt only when content changed and the stream-time
    /// throttle elapsed (the first build is immediate). Null until the first beat.
    /// </summary>
    public BeatMetricsHistorySnapshot? CurrentSnapshot()
    {
        if (!_dirty && _snapshot != null)
        {
            return _snapshot;
        }

        if (_snapshot != null && _latestTimeS - _lastSnapshotTimeS < SnapshotMinIntervalS)
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
            AmplitudeValid = _amplitudeValid,
            AmplitudeDeg = _amplitudeDeg,
            BeatErrorValid = _beatErrorValid,
            BeatErrorSignedMs = _beatErrorSignedMs,
            LatestTimeS = _latestTimeS,
        };
        _lastSnapshotTimeS = _latestTimeS;
        _dirty = false;
        return _snapshot;
    }

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
