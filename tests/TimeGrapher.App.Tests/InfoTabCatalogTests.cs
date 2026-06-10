using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class InfoTabCatalogTests
{
    [Fact]
    public void TabIdsAreUniqueAndHaveRefreshPolicies()
    {
        string[] tabIds = InfoTabCatalog.All.Select(tab => tab.Id).ToArray();

        Assert.Equal(tabIds.Length, tabIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(InfoTabCatalog.All, tab => Assert.True(tab.RefreshIntervalMs > 0));
    }

    [Fact]
    public void RateScopeTabDeclaresSnapshotGraphContract()
    {
        InfoTabDefinition tab = InfoTabCatalog.Get(InfoTabCatalog.RateScopeTabId);
        string[] requiredIds =
        {
            AnalysisGraphSeries.ScopePcm,
            AnalysisGraphSeries.ScopeThreshold,
            AnalysisGraphSeries.RateTic,
            AnalysisGraphSeries.RateToc,
        };

        HashSet<string> seriesIds = tab.GraphSeries.Select(series => series.Id).ToHashSet(StringComparer.Ordinal);

        Assert.True(tab.UsesGraphSnapshots);
        Assert.All(requiredIds, id => Assert.Contains(id, seriesIds));
        Assert.All(tab.GraphSeries, series => Assert.True(series.TargetPointBudget > 0));
    }

    [Fact]
    public void SoundPrintTabIsIndependentFromGraphSeries()
    {
        InfoTabDefinition tab = InfoTabCatalog.Get(InfoTabCatalog.SoundPrintTabId);

        Assert.False(tab.UsesGraphSnapshots);
        Assert.Empty(tab.GraphSeries);
        Assert.Equal(InfoTabKind.SoundPrint, tab.Kind);
    }

    [Fact]
    public void TryGetReturnsFalseForUnknownTab()
    {
        Assert.False(InfoTabCatalog.TryGet("missing", out InfoTabDefinition? tab));
        Assert.Null(tab);
    }

    [Fact]
    public void TraceDisplayTabRendersFromCumulativeHistoryNotGraphSeries()
    {
        InfoTabDefinition tab = InfoTabCatalog.Get(InfoTabCatalog.TraceDisplayTabId);

        Assert.Equal(InfoTabKind.TraceDisplay, tab.Kind);
        Assert.False(tab.UsesGraphSnapshots);
        Assert.Empty(tab.GraphSeries);
    }

    [Fact]
    public void ScopeSweepTabRendersFromCoreFoldedSweepSeries()
    {
        InfoTabDefinition tab = InfoTabCatalog.Get(InfoTabCatalog.ScopeSweepTabId);

        Assert.Equal(InfoTabKind.ScopeSweep, tab.Kind);
        Assert.False(tab.UsesGraphSnapshots);
        Assert.Empty(tab.GraphSeries);
    }

    [Fact]
    public void BeatErrorDiagTabDeclaresRateTraceContract()
    {
        InfoTabDefinition tab = InfoTabCatalog.Get(InfoTabCatalog.BeatErrorDiagTabId);

        Assert.Equal(InfoTabKind.BeatErrorDiag, tab.Kind);
        Assert.True(tab.UsesGraphSnapshots);
        Assert.Equal(
            new[] { AnalysisGraphSeries.RateTic, AnalysisGraphSeries.RateToc },
            tab.GraphSeries.Select(series => series.Id).ToArray());
        Assert.All(tab.GraphSeries, series =>
            Assert.Equal(GraphSeriesRenderMode.Points, series.RenderMode));
    }

    [Fact]
    public void MultiFilterScopeTabRendersFromCoreFilterSeries()
    {
        InfoTabDefinition tab = InfoTabCatalog.Get(InfoTabCatalog.MultiFilterScopeTabId);

        Assert.Equal(InfoTabKind.MultiFilterScope, tab.Kind);
        Assert.False(tab.UsesGraphSnapshots);
        Assert.Empty(tab.GraphSeries);
    }

    [Fact]
    public void LongTermPerfTabRendersFromCumulativeHistoryNotGraphSeries()
    {
        InfoTabDefinition tab = InfoTabCatalog.Get(InfoTabCatalog.LongTermPerfTabId);

        Assert.Equal(InfoTabKind.LongTermPerformance, tab.Kind);
        Assert.False(tab.UsesGraphSnapshots);
        Assert.Empty(tab.GraphSeries);
    }

    [Fact]
    public void TestPositionsTabRendersFromCumulativeHistoryNotGraphSeries()
    {
        InfoTabDefinition tab = InfoTabCatalog.Get(InfoTabCatalog.TestPositionsTabId);

        Assert.Equal(InfoTabKind.TestPositions, tab.Kind);
        Assert.False(tab.UsesGraphSnapshots);
        Assert.Empty(tab.GraphSeries);
    }

    [Fact]
    public void MultiPositionSeqTabRendersFromCumulativeHistoryNotGraphSeries()
    {
        InfoTabDefinition tab = InfoTabCatalog.Get(InfoTabCatalog.MultiPositionSeqTabId);

        Assert.Equal(InfoTabKind.MultiPositionSequence, tab.Kind);
        Assert.False(tab.UsesGraphSnapshots);
        Assert.Empty(tab.GraphSeries);
    }

    [Fact]
    public void BeatNoiseScopeTabRendersFromCumulativeSegmentsNotGraphSeries()
    {
        InfoTabDefinition tab = InfoTabCatalog.Get(InfoTabCatalog.BeatNoiseScopeTabId);

        Assert.Equal(InfoTabKind.BeatNoiseScope, tab.Kind);
        Assert.False(tab.UsesGraphSnapshots);
        Assert.Empty(tab.GraphSeries);
    }

    [Fact]
    public void CatalogTracksFunctionalAndPlaceholderTabCounts()
    {
        InfoTabDefinition[] functional = InfoTabCatalog.All
            .Where(tab => tab.Kind != InfoTabKind.Placeholder).ToArray();
        InfoTabDefinition[] placeholders = InfoTabCatalog.All
            .Where(tab => tab.Kind == InfoTabKind.Placeholder).ToArray();

        Assert.Equal(14, InfoTabCatalog.All.Count);
        Assert.Equal(11, functional.Length);
        Assert.Equal(3, placeholders.Length);
        Assert.All(placeholders, tab => Assert.Empty(tab.GraphSeries));
        Assert.All(placeholders, tab => Assert.False(tab.UsesGraphSnapshots));
    }
}
