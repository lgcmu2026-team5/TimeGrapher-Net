using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// One stacked lane of the Multi-Filter Scope tab: which Core filter series it
/// plots, its label, the one-line description shown above the plot, and the
/// theme-palette slot its trace recolors from on theme toggles.
/// </summary>
internal sealed record MultiFilterScopeLane(
    string SeriesId,
    string Label,
    string Description,
    Func<PlotThemePalette, uint> Color);

/// <summary>
/// Static lane catalog for the Multi-Filter Scope tab, in top-to-bottom display
/// order F0..F3 (the plan's four PC-RM4 filter views of the same signal).
/// </summary>
internal static class MultiFilterScopeLanes
{
    public static readonly IReadOnlyList<MultiFilterScopeLane> All = new MultiFilterScopeLane[]
    {
        new(AnalysisGraphSeries.FilterF0, "F0",
            "Signal as captured, mirrored around its average — the closest view of the raw watch signal.",
            theme => theme.TraceWave),
        new(AnalysisGraphSeries.FilterF1, "F1",
            "Moving average of F0 — smoother and less noisy, but low-amplitude detail fades.",
            theme => theme.TraceTick),
        new(AnalysisGraphSeries.FilterF2, "F2",
            "F1 with rising slopes emphasized and falls attenuated — reveals T3 and somewhat T2.",
            theme => theme.TraceTock),
        new(AnalysisGraphSeries.FilterF3, "F3",
            "Upper portion above the average with rising-edge emphasis — helps identify T1 and T3.",
            theme => theme.TextPrimary),
    };
}
