using TimeGrapher.Core.Metrics;
using Xunit;

namespace TimeGrapher.Core.Tests;

public sealed class DecimatingSeriesTests
{
    private static (List<double> X, List<double> Y, List<double> Min, List<double> Max) Snapshot(DecimatingSeries series)
    {
        var x = new List<double>();
        var y = new List<double>();
        var min = new List<double>();
        var max = new List<double>();
        series.SnapshotTo(x, y, min, max);
        return (x, y, min, max);
    }

    [Fact]
    public void StoresRawPointsUpToCapacity()
    {
        var series = new DecimatingSeries(4);
        series.Add(1.0, 10.0);
        series.Add(2.0, 20.0);

        Assert.Equal(2, series.Count);
        Assert.Equal(1, series.BucketSize);

        (List<double> x, List<double> y, _, _) = Snapshot(series);
        Assert.Equal(new[] { 1.0, 2.0 }, x);
        Assert.Equal(new[] { 10.0, 20.0 }, y);
    }

    [Fact]
    public void HalvesResolutionWhenFull()
    {
        var series = new DecimatingSeries(4);
        for (int i = 1; i <= 4; i++)
        {
            series.Add(i, i * 10.0);
        }

        // The 5th point triggers compaction: pairs (1,2) and (3,4) merge, the new
        // sample becomes the half-filled first bucket at the doubled bucket size.
        series.Add(5.0, 50.0);
        Assert.Equal(2, series.Count);
        Assert.Equal(2, series.BucketSize);

        // The 6th point completes that bucket.
        series.Add(6.0, 60.0);
        Assert.Equal(3, series.Count);

        (List<double> x, List<double> y, _, _) = Snapshot(series);
        Assert.Equal(new[] { 1.5, 3.5, 5.5 }, x);
        Assert.Equal(new[] { 15.0, 35.0, 55.0 }, y);
    }

    [Fact]
    public void TracksPerBucketMinAndMax()
    {
        var series = new DecimatingSeries(4);
        series.Add(1.0, 10.0);
        series.Add(2.0, 40.0);
        series.Add(3.0, 20.0);
        series.Add(4.0, 30.0);
        series.Add(5.0, 5.0); // compaction to two merged points

        (_, _, List<double> min, List<double> max) = Snapshot(series);
        Assert.Equal(new[] { 10.0, 20.0 }, min);
        Assert.Equal(new[] { 40.0, 30.0 }, max);
    }

    [Fact]
    public void StaysBoundedOverLongFeeds()
    {
        var series = new DecimatingSeries(64);
        for (int i = 0; i < 100_000; i++)
        {
            series.Add(i, Math.Sin(i * 0.01));
        }

        Assert.InRange(series.Count, 1, 64);

        // X stays strictly increasing after arbitrary compaction rounds.
        (List<double> x, _, _, _) = Snapshot(series);
        for (int i = 1; i < x.Count; i++)
        {
            Assert.True(x[i] > x[i - 1], $"x[{i}]={x[i]} should exceed x[{i - 1}]={x[i - 1]}");
        }
    }

    [Fact]
    public void ResetRestoresRawResolution()
    {
        var series = new DecimatingSeries(4);
        for (int i = 0; i < 10; i++)
        {
            series.Add(i, i);
        }

        series.Reset();
        Assert.Equal(0, series.Count);
        Assert.Equal(1, series.BucketSize);

        series.Add(1.0, 2.0);
        (List<double> x, List<double> y, _, _) = Snapshot(series);
        Assert.Equal(new[] { 1.0 }, x);
        Assert.Equal(new[] { 2.0 }, y);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void RejectsInvalidCapacity(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecimatingSeries(capacity));
    }
}
