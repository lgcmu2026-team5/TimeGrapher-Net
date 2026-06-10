using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Tabs;

internal enum InfoTabKind
{
    RateScope,
    SoundPrint,
    TraceDisplay,
    Vario,
    BeatErrorDiag,
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
    public const string VarioTabId = "rate-amp-stability";
    public const string BeatErrorDiagTabId = "beat-error-diag";

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
            // Vario stability gauges render the running stats on the same snapshot.
            new(VarioTabId, "Rate/Amp Stability", InfoTabKind.Vario, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: false, Array.Empty<GraphSeriesDefinition>()),
            // Beat Error Diag plots the per-frame tic/toc rate traces and reads the
            // cumulative snapshot for its numeric panel and diagnostic rules.
            new(BeatErrorDiagTabId, "Beat Error Diag", InfoTabKind.BeatErrorDiag, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: true, BeatErrorDiagSeries),
        };

        // Reserved placeholder tabs for features not yet built.
        // Titles are shortened from the planned feature names to fit the tab width.
        (string Id, string Title)[] placeholders =
        {
            ("test-positions", "Test Positions"),
            ("multi-position-seq", "Multi-Position Seq"),
            ("beat-noise-scope", "Beat-Noise Scope"),
            ("long-term-perf", "Long-Term Perf"),
            ("escapement-analyzer", "Escapement Analyzer"),
            ("spectrogram", "Spectrogram"),
            ("waveform-compare", "Waveform Compare"),
            ("multi-filter-scope", "Multi-Filter Scope"),
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
