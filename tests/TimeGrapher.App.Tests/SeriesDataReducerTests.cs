using TimeGrapher.App.Rendering;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class SeriesDataReducerTests
{
    [Fact]
    public void ReplaceSeriesDataDecimatesToPointBudget()
    {
        var targetX = new List<double>();
        var targetY = new List<double>();
        var sourceX = Enumerable.Range(0, 10).Select(value => (double)value).ToArray();
        var sourceY = sourceX.Select(value => value * 10.0).ToArray();

        SeriesDataReducer.ReplaceSeriesData(targetX, targetY, sourceX, sourceY, targetPointBudget: 3);

        Assert.Equal(new[] { 0.0, 4.0, 8.0 }, targetX);
        Assert.Equal(new[] { 0.0, 40.0, 80.0 }, targetY);
    }

    [Fact]
    public void TryReplaceSeriesDataRejectsAppendPayload()
    {
        var series = new GraphSeriesFrame
        {
            Id = AnalysisGraphSeries.ScopePcm,
            X = new List<double> { 1.0 },
            Y = new List<double> { 2.0 },
            Replace = false,
        };

        Assert.Throws<InvalidOperationException>(() =>
            SeriesDataReducer.TryReplaceSeriesData(series, new List<double>(), new List<double>(), targetPointBudget: 10));
    }

    [Fact]
    public void ReplaceSeriesDataUsesShortestCoordinateList()
    {
        var targetX = new List<double>();
        var targetY = new List<double>();

        SeriesDataReducer.ReplaceSeriesData(
            targetX,
            targetY,
            new[] { 1.0, 2.0, 3.0 },
            new[] { 10.0, 20.0 },
            targetPointBudget: 10);

        Assert.Equal(new[] { 1.0, 2.0 }, targetX);
        Assert.Equal(new[] { 10.0, 20.0 }, targetY);
    }
}
