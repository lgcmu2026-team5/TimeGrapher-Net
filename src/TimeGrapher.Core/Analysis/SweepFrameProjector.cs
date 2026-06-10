using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Analysis;

/// <summary>
/// Scope Mode sweep projector: folds the rectified detector output into a fixed
/// sweep window of <see cref="SetSweepMultiple"/> beat periods, oscilloscope
/// style. Samples land in a fixed bin array by their absolute stream position
/// modulo the window, and each sweep pass overwrites the previous one bin by bin
/// (within one pass a bin keeps its envelope maximum). A watch on its nominal
/// rate therefore paints a visually stable pattern, while a fast/slow watch
/// drifts across the display — the plan's synchronized-sweep behavior. The fixed
/// bin budget bounds memory and per-frame snapshot cost regardless of run length
/// (SAP performance tactic: bound resource usage). Sibling of
/// <see cref="ScopeRateFrameProjector"/>; Project/AppendSnapshot run on the
/// analysis thread only.
/// </summary>
public sealed class SweepFrameProjector
{
    public const int DefaultSweepMultiple = 2;
    public const int SweepBinBudget = 4000;

    /// <summary>Nominal 28800-BPH beat period used before any lock.</summary>
    public const double FallbackBeatPeriodS = 0.125;

    // The PLL-tracked MeasuredPeriodS jitters slightly every block; re-tuning
    // (and clearing) the window on every wiggle would never let a pattern
    // accumulate. The window is latched and only re-tuned when the candidate
    // moves past this fraction (a re-lock at a different BPH), so genuine rate
    // error shows up as drift against the latched window instead of silently
    // re-centering.
    private const double WindowRetuneFraction = 0.01;

    private readonly int _sampleRate;
    private readonly double[] _binValues = new double[SweepBinBudget];
    private readonly long[] _binPass = new long[SweepBinBudget];

    // Written from any thread (UI sweep buttons), applied analysis-side at the
    // start of the next Project pass.
    private volatile int _requestedSweepMultiple = DefaultSweepMultiple;
    private int _activeSweepMultiple = DefaultSweepMultiple;
    private double _windowS;

    // Last locked beat period, latched across sync dropouts: the detector
    // forces MeasuredPeriodS to 0 on every sync loss, and falling back to the
    // nominal 28800-bph period there would re-tune (and clear) the window twice
    // per dropout for any watch whose period differs >1% from 125 ms. Same
    // discipline as AnalysisWorker._latestBeatPeriodS.
    private double _lastKnownBeatPeriodS;

    /// <summary>
    /// Stream-time floor between series rebuilds (the MultiFilterFrameProjector
    /// throttle): the X list is bit-identical between retunes and Y changes
    /// continuously, so frames in between re-attach the same immutable series.
    /// </summary>
    public const double PublishIntervalS = 0.05;

    private volatile int _publishIntervalScale = 1;
    private List<double>? _cachedX;
    private GraphSeriesFrame? _lastSeries;
    private ulong _lastPublishedSample;
    private ulong _streamEndSample;

    public SweepFrameProjector(int sampleRate)
    {
        _sampleRate = sampleRate;
        Array.Fill(_binPass, -1L);
    }

    /// <summary>
    /// Sweep window length as a multiple of the beat period (the plan offers
    /// 1x / 2x / 4x). Thread-safe; a change re-tunes the window and clears the
    /// bins on the next analysis pass.
    /// </summary>
    public void SetSweepMultiple(int sweepMultiple)
    {
        _requestedSweepMultiple = Math.Max(1, sweepMultiple);
    }

    /// <summary>
    /// Deadline-degradation knob: multiplies the publish floor (1 = normal).
    /// Thread-safe; applied on the next AppendSnapshot.
    /// </summary>
    public void SetPublishIntervalScale(int scale)
    {
        _publishIntervalScale = Math.Max(1, scale);
    }

    public void Project(DetectorMetricsBlockUpdate update)
    {
        DetectorResultSnapshot result = update.Result;
        RetuneWindow(result);
        if (_windowS <= 0.0 || result.ProcessedPcmLen == 0)
        {
            return;
        }

        _streamEndSample = result.ProcessedPcmStartSample + (ulong)result.ProcessedPcmLen;
        double windowSamples = _windowS * _sampleRate;
        double binsPerSample = SweepBinBudget / windowSamples;
        for (int i = 0; i < result.ProcessedPcmLen; i++)
        {
            double absoluteSample = (double)result.ProcessedPcmStartSample + i;
            long pass = (long)(absoluteSample / windowSamples);
            double positionInWindow = absoluteSample - pass * windowSamples;
            int bin = (int)(positionInWindow * binsPerSample);
            if ((uint)bin >= SweepBinBudget)
            {
                bin = SweepBinBudget - 1;
            }

            double value = result.ProcessedPcm[i];
            if (_binPass[bin] == pass)
            {
                // Same sweep pass: keep the bin's envelope maximum.
                if (value > _binValues[bin])
                {
                    _binValues[bin] = value;
                }
            }
            else
            {
                // Newer pass: overwrite, so rate drift moves the pattern.
                _binValues[bin] = value;
                _binPass[bin] = pass;
            }
        }
    }

    public void AppendSnapshot(AnalysisFrame frame)
    {
        if (_windowS <= 0.0)
        {
            return;
        }

        ulong intervalSamples = (ulong)(PublishIntervalS * _sampleRate) * (ulong)_publishIntervalScale;
        if (_lastSeries == null || _streamEndSample - _lastPublishedSample >= intervalSamples)
        {
            if (_cachedX == null)
            {
                double binWidthMs = _windowS * 1000.0 / SweepBinBudget;
                _cachedX = new List<double>(SweepBinBudget);
                for (int i = 0; i < SweepBinBudget; i++)
                {
                    _cachedX.Add((i + 0.5) * binWidthMs);
                }
            }

            var y = new List<double>(SweepBinBudget);
            for (int i = 0; i < SweepBinBudget; i++)
            {
                y.Add(_binValues[i]);
            }

            _lastSeries = new GraphSeriesFrame
            {
                Id = AnalysisGraphSeries.SweepTrace,
                X = _cachedX,
                Y = y,
                Replace = true,
            };
            _lastPublishedSample = _streamEndSample;
        }

        frame.AddScopeSeries(_lastSeries);
    }

    private void RetuneWindow(DetectorResultSnapshot result)
    {
        int multiple = _requestedSweepMultiple;
        if (result.MeasuredPeriodS > 0.0)
        {
            _lastKnownBeatPeriodS = result.MeasuredPeriodS;
        }
        else if (result.SyncStatus == TgSyncStatus.Synced && result.DetectedBph > 0)
        {
            _lastKnownBeatPeriodS = 3600.0 / result.DetectedBph;
        }

        double beatPeriodS = _lastKnownBeatPeriodS > 0.0 ? _lastKnownBeatPeriodS : FallbackBeatPeriodS;
        double candidate = multiple * beatPeriodS;
        if (multiple == _activeSweepMultiple &&
            _windowS > 0.0 &&
            Math.Abs(candidate - _windowS) <= _windowS * WindowRetuneFraction)
        {
            return;
        }

        _activeSweepMultiple = multiple;
        _windowS = candidate;
        Array.Clear(_binValues);
        Array.Fill(_binPass, -1L);
        // The window length changed: the cached X axis and the shared series
        // are stale, and the cleared pattern should publish immediately.
        _cachedX = null;
        _lastSeries = null;
    }
}
