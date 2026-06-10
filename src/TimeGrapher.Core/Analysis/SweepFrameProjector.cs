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

    public void Project(DetectorMetricsBlockUpdate update)
    {
        DetectorResultSnapshot result = update.Result;
        RetuneWindow(result);
        if (_windowS <= 0.0 || result.ProcessedPcmLen == 0)
        {
            return;
        }

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

        double binWidthMs = _windowS * 1000.0 / SweepBinBudget;
        var x = new List<double>(SweepBinBudget);
        var y = new List<double>(SweepBinBudget);
        for (int i = 0; i < SweepBinBudget; i++)
        {
            x.Add((i + 0.5) * binWidthMs);
            y.Add(_binValues[i]);
        }

        frame.AddScopeSeries(new GraphSeriesFrame
        {
            Id = AnalysisGraphSeries.SweepTrace,
            X = x,
            Y = y,
            Replace = true,
        });
    }

    private void RetuneWindow(DetectorResultSnapshot result)
    {
        int multiple = _requestedSweepMultiple;
        double beatPeriodS = FallbackBeatPeriodS;
        if (result.MeasuredPeriodS > 0.0)
        {
            beatPeriodS = result.MeasuredPeriodS;
        }
        else if (result.SyncStatus == TgSyncStatus.Synced && result.DetectedBph > 0)
        {
            beatPeriodS = 3600.0 / result.DetectedBph;
        }

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
    }
}
