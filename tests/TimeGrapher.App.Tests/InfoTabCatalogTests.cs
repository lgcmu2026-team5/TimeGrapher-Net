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
}
