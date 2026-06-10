namespace TimeGrapher.Core.Shared;

/// <summary>
/// Port of the AnalysisWorker.h presentation DTOs. One AnalysisFrame is one UI update unit:
/// scope series, event markers, rate plot data, result text, sound image and thread stats
/// produced by the same analysis pass (QAS-4 single-frame consistency).
/// </summary>

public struct ScopeVerticalMarker
{
    public double X;
    public double Height;
    public uint Color; // ARGB32
}

public enum HorizontalMarkerDirection
{
    Outward,
    Inward
}

public struct ScopeHorizontalMarker
{
    public HorizontalMarkerDirection Direction;
    public double XLeft;
    public double XRight;
    public double Length; // used by Inward markers only
    public double Height;
    public uint Color; // ARGB32
}

/// <summary>Subset of Qt::Alignment actually used by the original code.</summary>
public enum MarkerTextAlignment
{
    LeftTop,   // Qt::AlignLeft | Qt::AlignTop
    CenterTop  // Qt::AlignHCenter | Qt::AlignTop
}

public struct ScopeTextMarker
{
    public double X;
    public double Height;
    public string Text;
    public uint Color; // ARGB32
    public MarkerTextAlignment Alignment;
}

/// <summary>Graph series ids (AnalysisGraphSeries namespace in C++).</summary>
public static class AnalysisGraphSeries
{
    public const string ScopePcm = "scope.pcm";
    public const string ScopeThreshold = "scope.threshold";
    public const string RateTic = "rate.tic";
    public const string RateToc = "rate.toc";
    /// <summary>Scope Mode sweep window envelope (x = ms within the sweep window).</summary>
    public const string SweepTrace = "sweep.trace";
    /// <summary>
    /// Multi-Filter Scope views F0..F3 (x = absolute raw-sample ticks on the
    /// MultiFilterFrameProjector's own counter, not GraphTickEnd).
    /// </summary>
    public const string FilterF0 = "filter.f0";
    public const string FilterF1 = "filter.f1";
    public const string FilterF2 = "filter.f2";
    public const string FilterF3 = "filter.f3";
}

public sealed class GraphSeriesFrame
{
    public string Id { get; init; } = "";
    public IReadOnlyList<double> X { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> Y { get; init; } = Array.Empty<double>();
    /// <summary>true = replace the previous UI graph payload for this series.</summary>
    public bool Replace { get; init; }
}

/// <summary>Port of WatchMetricsUpdate (WatchMetrics.h).</summary>
public sealed class WatchMetricsUpdate
{
    private readonly List<double> _xTic = new();
    private readonly List<double> _yTic = new();
    private readonly List<double> _xToc = new();
    private readonly List<double> _yToc = new();

    public bool TicRateUpdated { get; private set; }
    public bool TocRateUpdated { get; private set; }
    public IReadOnlyList<double> XTic => _xTic;
    public IReadOnlyList<double> YTic => _yTic;
    public IReadOnlyList<double> XToc => _xToc;
    public IReadOnlyList<double> YToc => _yToc;
    public bool ResultsUpdated { get; private set; }
    public string ResultsText { get; private set; } = "";
    public bool CMarkerTextUpdated { get; private set; }
    public string CMarkerText { get; private set; } = "";
    public bool BeatTimingSampleUpdated { get; private set; }
    public BeatTimingSample BeatTimingSample { get; private set; }
    public bool AmplitudeSampleUpdated { get; private set; }
    public AmplitudeSample AmplitudeSample { get; private set; }
    public bool DerivedMeasuresUpdated { get; private set; }
    public DerivedTimingMeasures DerivedMeasures { get; private set; }

    internal void SetTicRate(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        Replace(_xTic, x);
        Replace(_yTic, y);
        TicRateUpdated = true;
    }

    internal void SetTocRate(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        Replace(_xToc, x);
        Replace(_yToc, y);
        TocRateUpdated = true;
    }

    internal void SetResults(string text)
    {
        ResultsText = text;
        ResultsUpdated = true;
    }

    internal void SetCMarkerText(string text)
    {
        CMarkerText = text;
        CMarkerTextUpdated = true;
    }

    internal void SetBeatTimingSample(BeatTimingSample sample)
    {
        BeatTimingSample = sample;
        BeatTimingSampleUpdated = true;
    }

    internal void SetAmplitudeSample(AmplitudeSample sample)
    {
        AmplitudeSample = sample;
        AmplitudeSampleUpdated = true;
    }

    internal void SetDerivedMeasures(DerivedTimingMeasures measures)
    {
        DerivedMeasures = measures;
        DerivedMeasuresUpdated = true;
    }

    private static void Replace(List<double> target, IReadOnlyList<double> source)
    {
        target.Clear();
        target.AddRange(source);
    }
}

public sealed class AnalysisFrame
{
    private readonly List<GraphSeriesFrame> _scopeSeries = new();
    private readonly List<GraphSeriesFrame> _rateSeries = new();
    private readonly List<ScopeVerticalMarker> _verticalMarkers = new();
    private readonly List<ScopeHorizontalMarker> _horizontalMarkers = new();
    private readonly List<ScopeTextMarker> _textMarkers = new();

    public ulong SessionId;
    public ulong SourceId;
    public ulong SourceSampleEnd;
    /// <summary>Sample rate the producing analysis run was configured with (0 = unknown).</summary>
    public int SampleRate;
    public bool InputOverrun;
    public ulong InputSamplesDropped;
    public ulong PendingSamples;
    public ulong AnalysisLagSamples;
    public double ProcessingElapsedMs;
    /// <summary>Current AnalysisDeadlineMonitor ladder level (0 = full quality).</summary>
    public int DeadlineDegradationLevel;

    /// <summary>
    /// Latency instrumentation (QA: capture-to-display latency reporting).
    /// Stopwatch timestamps so the UI can compute capture-to-processing,
    /// processing-to-display, and end-to-end legs on one clock. 0 = unknown.
    /// </summary>
    public long CaptureTimestamp;
    public long ProcessingCompletedTimestamp;

    /// <summary>Session-cumulative missed-beat / sync-loss counters (coalescing-safe).</summary>
    public ulong MissedBeats;
    public uint SyncLossCount;

    public IReadOnlyList<GraphSeriesFrame> ScopeSeries => _scopeSeries;
    public IReadOnlyList<GraphSeriesFrame> RateSeries => _rateSeries;

    public IReadOnlyList<ScopeVerticalMarker> VerticalMarkers => _verticalMarkers;
    public IReadOnlyList<ScopeHorizontalMarker> HorizontalMarkers => _horizontalMarkers;
    public IReadOnlyList<ScopeTextMarker> TextMarkers => _textMarkers;

    public WatchMetricsUpdate MetricsUpdate = new();

    /// <summary>
    /// Cumulative per-beat metrics history (rate / amplitude / beat error series +
    /// derived measures). Cumulative by design: the render scheduler coalesces
    /// frames latest-wins, so dropped intermediate frames lose nothing.
    /// </summary>
    public BeatMetricsHistorySnapshot? MetricsHistory;

    public PixelBuffer? SoundImage;
    public bool SoundImageUpdated;

    /// <summary>True once the detector has locked onto the tick/tock beat (BPH synced).</summary>
    public bool BeatSynced;

    public ulong GraphTickEnd;

    public double BackgroundFps;
    public double BackgroundSps;
    public double BackgroundSpf;
    public double ForegroundFps;
    public double ForegroundSps;
    public double ForegroundSpf;
    public bool ForegroundStatsUpdated;

    internal void AddScopeSeries(GraphSeriesFrame series)
    {
        _scopeSeries.Add(series);
    }

    internal void AddRateSeries(GraphSeriesFrame series)
    {
        _rateSeries.Add(series);
    }

    internal void SetScopeMarkers(
        IReadOnlyList<ScopeVerticalMarker> verticalMarkers,
        IReadOnlyList<ScopeHorizontalMarker> horizontalMarkers,
        IReadOnlyList<ScopeTextMarker> textMarkers)
    {
        Replace(_verticalMarkers, verticalMarkers);
        Replace(_horizontalMarkers, horizontalMarkers);
        Replace(_textMarkers, textMarkers);
    }

    private static void Replace<T>(List<T> target, IReadOnlyList<T> source)
    {
        target.Clear();
        target.AddRange(source);
    }
}
