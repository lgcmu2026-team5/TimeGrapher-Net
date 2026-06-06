using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Tabs;

internal enum InfoTabKind
{
    RateScope,
    SoundPrint,
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

    private static readonly InfoTabDefinition[] Definitions =
    {
        new(RateScopeTabId, "Rate/Scope", InfoTabKind.RateScope, DefaultUiRefreshIntervalMs, UsesGraphSnapshots: true, RateScopeSeries),
        new(SoundPrintTabId, "Sound Print", InfoTabKind.SoundPrint, SoundPrintRefreshIntervalMs, UsesGraphSnapshots: false, Array.Empty<GraphSeriesDefinition>()),
    };

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
