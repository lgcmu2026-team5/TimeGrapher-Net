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
}

public sealed class GraphSeriesFrame
{
    public string Id = "";
    public List<double> X = new();
    public List<double> Y = new();
    /// <summary>false = append to rolling plot (scope), true = replace plot data (rate).</summary>
    public bool Replace;
}

/// <summary>Port of WatchMetricsUpdate (WatchMetrics.h).</summary>
public sealed class WatchMetricsUpdate
{
    public bool TicRateUpdated;
    public bool TocRateUpdated;
    public List<double> XTic = new();
    public List<double> YTic = new();
    public List<double> XToc = new();
    public List<double> YToc = new();
    public bool ResultsUpdated;
    public string ResultsText = "";
    public bool CMarkerTextUpdated;
    public string CMarkerText = "";
}

public sealed class AnalysisFrame
{
    public ulong SessionId;
    public ulong SourceId;
    public ulong SourceSampleEnd;

    public List<GraphSeriesFrame> ScopeSeries = new();
    public List<GraphSeriesFrame> RateSeries = new();

    public List<double> ScopeX = new();
    public List<double> ScopePcm = new();
    public List<double> ScopeThreshold = new();
    public List<ScopeVerticalMarker> VerticalMarkers = new();
    public List<ScopeHorizontalMarker> HorizontalMarkers = new();
    public List<ScopeTextMarker> TextMarkers = new();

    public WatchMetricsUpdate MetricsUpdate = new();
    public PixelBuffer? SoundImage;
    public bool SoundImageUpdated;

    public ulong GraphTickEnd;

    public double BackgroundFps;
    public double BackgroundSps;
    public double BackgroundSpf;
    public double ForegroundFps;
    public double ForegroundSps;
    public double ForegroundSpf;
    public bool ForegroundStatsUpdated;
}
