using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Tabs;

internal enum InfoTabKind
{
    RateScope,
    SoundPrint,
    TraceDisplay,
    ScopeSweep,
    Vario,
    BeatErrorDiag,
    MultiFilterScope,
    LongTermPerformance,
    TestPositions,
    MultiPositionSequence,
    BeatNoiseScope,
    EscapementAnalyzer,
    WaveformCompare,
    Placeholder,
}

internal enum GraphSeriesRenderMode
{
    Line,
    Points,
}

internal sealed record GraphSeriesDefinition(
    string Id,
    string Name,
    uint Color,
    GraphSeriesRenderMode RenderMode,
    int TargetPointBudget,
    int FillAlpha = 0);

internal sealed record InfoTabDefinition(
    string Id,
    string Title,
    InfoTabKind Kind,
    int RefreshIntervalMs,
    bool UsesGraphSnapshots,
    IReadOnlyList<GraphSeriesDefinition> GraphSeries);

internal static class InfoTabCatalog
{
    public const string RateScopeTabId = "rate-scope";
    public const string SoundPrintTabId = "sound-print";
    public const string TraceDisplayTabId = "trace-display";
    public const string ScopeSweepTabId = "scope-sweep";
    public const string VarioTabId = "rate-amp-stability";
    public const string BeatErrorDiagTabId = "beat-error-diag";
    public const string MultiFilterScopeTabId = "multi-filter-scope";
    public const string LongTermPerfTabId = "long-term-perf";
    public const string TestPositionsTabId = "test-positions";
    public const string MultiPositionSeqTabId = "multi-position-seq";
    public const string BeatNoiseScopeTabId = "beat-noise-scope";
    public const string EscapementAnalyzerTabId = "escapement-analyzer";
    public const string WaveformCompareTabId = "waveform-compare";

    public const int DefaultUiRefreshIntervalMs = 33;
    public const int SoundPrintRefreshIntervalMs = 100;
    public const int ScopeTargetPointBudget = 8000;
    public const int RateTargetPointBudget = 250;

    private static readonly GraphSeriesDefinition[] RateScopeSeries =
    {
        new(AnalysisGraphSeries.ScopePcm, "Rectified", Argb.Blue, GraphSeriesRenderMode.Line, ScopeTargetPointBudget, FillAlpha: 20),
        new(AnalysisGraphSeries.ScopeThreshold, "Trigger", Argb.Red, GraphSeriesRenderMode.Line, ScopeTargetPointBudget),
        new(AnalysisGraphSeries.RateTic, "Tic Rate", Argb.Red, GraphSeriesRenderMode.Points, RateTargetPointBudget),
        new(AnalysisGraphSeries.RateToc, "Toc Rate", Argb.Blue, GraphSeriesRenderMode.Points, RateTargetPointBudget),
    };

    // Same tic/toc rate-error traces the Rate/Scope tab consumes; declared
    // separately so each tab states its own graph-series contract.
    private static readonly GraphSeriesDefinition[] BeatErrorDiagSeries =
    {
        new(AnalysisGraphSeries.RateTic, "Tic Rate", Argb.Red, GraphSeriesRenderMode.Points, RateTargetPointBudget),
        new(AnalysisGraphSeries.RateToc, "Toc Rate", Argb.Blue, GraphSeriesRenderMode.Points, RateTargetPointBudget),
    };

    private static readonly InfoTabDefinition[] Definitions = BuildDefinitions();

    private static InfoTabDefinition[] BuildDefinitions()
    {
        var definitions = new List<InfoTabDefinition>
        {
            new(RateScopeTabId, "Rate/Scope", InfoTabKind.RateScope, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: true, RateScopeSeries),
            new(SoundPrintTabId, "Sound Print", InfoTabKind.SoundPrint, SoundPrintRefreshIntervalMs, UsesGraphSnapshots: false, Array.Empty<GraphSeriesDefinition>()),
            // Trace Display renders the cumulative BeatMetricsHistorySnapshot the
            // frame carries; it declares no per-frame graph-series contract.
            new(TraceDisplayTabId, "Trace", InfoTabKind.TraceDisplay, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: false, Array.Empty<GraphSeriesDefinition>()),
            // Scope Sweep refills its single plot from the Core-folded sweep.trace
            // replace series; the fixed bin budget lives Core-side, so no per-frame
            // graph-series reduction contract is declared here.
            new(ScopeSweepTabId, "Scope Sweep", InfoTabKind.ScopeSweep, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: false, Array.Empty<GraphSeriesDefinition>()),
            // Vario stability gauges render the running stats on the same snapshot.
            new(VarioTabId, "Rate/Amp Stability", InfoTabKind.Vario, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: false, Array.Empty<GraphSeriesDefinition>()),
            // Beat Error Diag plots the per-frame tic/toc rate traces and reads the
            // cumulative snapshot for its numeric panel and diagnostic rules.
            new(BeatErrorDiagTabId, "Beat Error Diag", InfoTabKind.BeatErrorDiag, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: true, BeatErrorDiagSeries),
            // Multi-Filter Scope refills its four stacked plots from the
            // Core-decimated filter.f0..f3 replace series; the per-series point
            // budget lives Core-side (MultiFilterFrameProjector), so no per-frame
            // graph-series reduction contract is declared here.
            new(MultiFilterScopeTabId, "Multi-Filter Scope", InfoTabKind.MultiFilterScope, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: false, Array.Empty<GraphSeriesDefinition>()),
            // Long-Term Performance renders the cumulative BeatMetricsHistorySnapshot
            // (bucket averages plus YMin/YMax variation bands); it declares no
            // per-frame graph-series contract.
            new(LongTermPerfTabId, "Long-Term Perf", InfoTabKind.LongTermPerformance, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: false, Array.Empty<GraphSeriesDefinition>()),
            // Test Positions reads only the cumulative snapshot's ActivePosition
            // stamp (the highlight follows what Core actually tags); it declares
            // no per-frame graph-series contract.
            new(TestPositionsTabId, "Test Positions", InfoTabKind.TestPositions, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: false, Array.Empty<GraphSeriesDefinition>()),
            // Multi-Position Sequence reads only the cumulative snapshot's
            // per-position aggregates (PositionSummary list) and active-position
            // stamp; it declares no per-frame graph-series contract.
            new(MultiPositionSeqTabId, "Multi-Position Seq", InfoTabKind.MultiPositionSequence, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: false, Array.Empty<GraphSeriesDefinition>()),
            // Beat-Noise Scope renders the cumulative BeatSegmentsSnapshot the
            // frame carries (Scope 1 segments + Scope 2 lane averages); it
            // declares no per-frame graph-series contract.
            new(BeatNoiseScopeTabId, "Beat-Noise Scope", InfoTabKind.BeatNoiseScope, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: false, Array.Empty<GraphSeriesDefinition>()),
            // Escapement Analyzer renders the latest segment of the same
            // cumulative BeatSegmentsSnapshot (A / C marker lines with ms
            // labels and the onset-vs-peak repeatability panel); it declares
            // no per-frame graph-series contract.
            new(EscapementAnalyzerTabId, "Escapement Analyzer", InfoTabKind.EscapementAnalyzer, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: false, Array.Empty<GraphSeriesDefinition>()),
            // Waveform Compare stacks the recent beats of the same cumulative
            // BeatSegmentsSnapshot in A-aligned, peak-normalized lanes with the
            // A / mean-C timing guides; it declares no per-frame graph-series
            // contract.
            new(WaveformCompareTabId, "Waveform Compare", InfoTabKind.WaveformCompare, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: false, Array.Empty<GraphSeriesDefinition>()),
        };

        // Reserved placeholder tabs for features not yet built.
        // Titles are shortened from the planned feature names to fit the tab width.
        (string Id, string Title)[] placeholders =
        {
            ("spectrogram", "Spectrogram"),
        };

        foreach ((string id, string title) in placeholders)
        {
            definitions.Add(new(
                id,
                title,
                InfoTabKind.Placeholder,
                DefaultUiRefreshIntervalMs,
                UsesGraphSnapshots: false,
                Array.Empty<GraphSeriesDefinition>()));
        }

        return definitions.ToArray();
    }

    public static IReadOnlyList<InfoTabDefinition> All => Definitions;

    public static InfoTabDefinition RateScope => Definitions[0];
    public static InfoTabDefinition SoundPrint => Definitions[1];

    public static InfoTabDefinition Get(string id)
    {
        foreach (InfoTabDefinition definition in Definitions)
        {
            if (definition.Id == id)
            {
                return definition;
            }
        }

        throw new ArgumentException($"Unknown info tab '{id}'.", nameof(id));
    }

    public static bool TryGet(string id, out InfoTabDefinition? definition)
    {
        foreach (InfoTabDefinition candidate in Definitions)
        {
            if (candidate.Id == id)
            {
                definition = candidate;
                return true;
            }
        }

        definition = null;
        return false;
    }
}
