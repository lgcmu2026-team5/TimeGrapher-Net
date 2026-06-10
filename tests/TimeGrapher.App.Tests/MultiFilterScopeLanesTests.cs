using TimeGrapher.App.Rendering;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

/// <summary>
/// Lane catalog behind the Multi-Filter Scope tab: the four stacked lanes map
/// 1:1 onto the Core filter series in F0..F3 order, each with its own
/// description line and a distinct theme-palette color slot.
/// </summary>
public sealed class MultiFilterScopeLanesTests
{
    [Fact]
    public void LanesPlotTheCoreFilterSeriesInF0ToF3Order()
    {
        Assert.Equal(
            new[]
            {
                AnalysisGraphSeries.FilterF0,
                AnalysisGraphSeries.FilterF1,
                AnalysisGraphSeries.FilterF2,
                AnalysisGraphSeries.FilterF3,
            },
            MultiFilterScopeLanes.All.Select(lane => lane.SeriesId).ToArray());

        Assert.Equal(
            new[] { "F0", "F1", "F2", "F3" },
            MultiFilterScopeLanes.All.Select(lane => lane.Label).ToArray());
    }

    [Fact]
    public void EveryLaneCarriesAOneLineDescription()
    {
        Assert.All(MultiFilterScopeLanes.All, lane =>
        {
            Assert.False(string.IsNullOrWhiteSpace(lane.Description));
            Assert.DoesNotContain('\n', lane.Description);
        });
    }

    [Fact]
    public void LanesUseTheWaveTickTockTextPaletteSlotsRespectively()
    {
        // Palette with a distinct sentinel per slot, so the selector of each
        // lane is identifiable by the value it picks.
        var palette = new PlotThemePalette(
            SurfaceBg: 1, ScopeBg: 2, ScopeGrid: 3, TextPrimary: 4,
            TraceWave: 5, TraceTick: 6, TraceTock: 7);

        Assert.Equal(
            new uint[] { palette.TraceWave, palette.TraceTick, palette.TraceTock, palette.TextPrimary },
            MultiFilterScopeLanes.All.Select(lane => lane.Color(palette)).ToArray());
    }
}
