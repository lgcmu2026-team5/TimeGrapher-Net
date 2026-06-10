using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Analysis;

/// <summary>
/// Multi-Filter Scope projector: runs every raw input sample through the
/// <see cref="ScopeFilters"/> bank (F0..F3) and keeps a rolling
/// <see cref="WindowSeconds"/>-second window per view, decimated at the
/// producer to <see cref="FilterPointBudget"/> points per series (the
/// <see cref="ScopeRateFrameProjector"/> stride pattern), so per-frame
/// snapshot cost and memory stay bounded regardless of sample rate or run
/// length (SAP performance tactic: bound resource usage). Snapshots ride the
/// frame as four replace series sharing one X list. X values are absolute
/// raw-sample ticks on this projector's own counter — frame.GraphTickEnd
/// stays owned by ScopeRateFrameProjector; the renderer windows each plot to
/// its own series' max x. ProcessSamples/AppendSnapshot run on the analysis
/// thread only.
/// </summary>
public sealed class MultiFilterFrameProjector
{
    public const int WindowSeconds = 2;
    public const int FilterPointBudget = 2000;

    private static readonly string[] SeriesIds =
    {
        AnalysisGraphSeries.FilterF0,
        AnalysisGraphSeries.FilterF1,
        AnalysisGraphSeries.FilterF2,
        AnalysisGraphSeries.FilterF3,
    };

    private readonly int _sampleRate;
    private readonly ScopeFilters _filters;
    private readonly List<double> _windowX = new();
    private readonly List<double>[] _windowY;
    private ulong _sampleTicks;

    public MultiFilterFrameProjector(int sampleRate)
    {
        _sampleRate = sampleRate;
        _filters = new ScopeFilters(sampleRate);
        _windowY = new List<double>[SeriesIds.Length];
        for (int i = 0; i < _windowY.Length; i++)
        {
            _windowY[i] = new List<double>();
        }
    }

    /// <summary>
    /// Feeds one raw audio block (the same span AnalysisWorker hands the
    /// detector pipeline). The filter bank consumes every sample to keep its
    /// state exact; only every stride-th output is stored for display.
    /// </summary>
    public void ProcessSamples(ReadOnlySpan<float> block)
    {
        ulong stride = (ulong)SnapshotStride();
        for (int i = 0; i < block.Length; i++)
        {
            ScopeFilterSample sample = _filters.Process(block[i]);
            if ((_sampleTicks % stride) == 0)
            {
                _windowX.Add(_sampleTicks);
                _windowY[0].Add(sample.F0);
                _windowY[1].Add(sample.F1);
                _windowY[2].Add(sample.F2);
                _windowY[3].Add(sample.F3);
            }

            _sampleTicks++;
        }
    }

    public void AppendSnapshot(AnalysisFrame frame)
    {
        TrimWindow();
        if (_windowX.Count == 0)
        {
            return;
        }

        var x = new List<double>(_windowX);
        for (int i = 0; i < SeriesIds.Length; i++)
        {
            frame.AddScopeSeries(new GraphSeriesFrame
            {
                Id = SeriesIds[i],
                X = x,
                Y = new List<double>(_windowY[i]),
                Replace = true,
            });
        }
    }

    private int SnapshotStride()
    {
        int baseStride = Math.Max(1, _sampleRate / 48000);
        int maxWindowSamples = Math.Max(1, WindowSeconds * _sampleRate);
        int budgetStride = (int)Math.Ceiling(maxWindowSamples / (double)FilterPointBudget);
        return Math.Max(baseStride, budgetStride);
    }

    private void TrimWindow()
    {
        double minX = 0.0;
        ulong historySamples = (ulong)(WindowSeconds * _sampleRate);
        if (_sampleTicks > historySamples)
        {
            minX = _sampleTicks - historySamples;
        }

        int removeCount = 0;
        while (removeCount < _windowX.Count && _windowX[removeCount] < minX)
        {
            removeCount++;
        }

        if (removeCount > 0)
        {
            _windowX.RemoveRange(0, removeCount);
            for (int i = 0; i < _windowY.Length; i++)
            {
                _windowY[i].RemoveRange(0, removeCount);
            }
        }
    }
}
