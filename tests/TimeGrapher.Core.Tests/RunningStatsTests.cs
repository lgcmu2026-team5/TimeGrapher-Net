using TimeGrapher.Core.Metrics;
using Xunit;

namespace TimeGrapher.Core.Tests;

/// <summary>
/// Welford online accumulator against closed-form hand computations.
/// Sigma is the population standard deviation (divide by N): the accumulator
/// summarizes every measured beat, not a sample estimate of a wider population.
/// </summary>
public sealed class RunningStatsTests
{
    private static RunningStats Filled(params double[] values)
    {
        var stats = new RunningStats();
        foreach (double value in values)
        {
            stats.Add(value);
        }

        return stats;
    }

    [Fact]
    public void EmptyAccumulatorReportsZeroes()
    {
        var stats = new RunningStats();

        Assert.Equal(0, stats.Count);
        Assert.Equal(0.0, stats.Min);
        Assert.Equal(0.0, stats.Max);
        Assert.Equal(0.0, stats.Mean);
        Assert.Equal(0.0, stats.Sigma);
    }

    [Fact]
    public void SingleValueHasZeroSpreadAndSigma()
    {
        RunningStats stats = Filled(5.0);

        Assert.Equal(1, stats.Count);
        Assert.Equal(5.0, stats.Min);
        Assert.Equal(5.0, stats.Max);
        Assert.Equal(5.0, stats.Mean);
        Assert.Equal(0.0, stats.Sigma);
    }

    [Fact]
    public void ClassicDatasetMatchesHandComputedPopulationSigma()
    {
        // {2,4,4,4,5,5,7,9}: mean = 40/8 = 5; squared deviations
        // 9+1+1+1+0+0+4+16 = 32; population variance = 32/8 = 4; sigma = 2.
        RunningStats stats = Filled(2.0, 4.0, 4.0, 4.0, 5.0, 5.0, 7.0, 9.0);

        Assert.Equal(8, stats.Count);
        Assert.Equal(2.0, stats.Min);
        Assert.Equal(9.0, stats.Max);
        Assert.Equal(5.0, stats.Mean, 12);
        Assert.Equal(2.0, stats.Sigma, 12);
    }

    [Fact]
    public void FourPointDatasetMatchesClosedForm()
    {
        // {1,2,3,4}: mean = 2.5; squared deviations 2.25+0.25+0.25+2.25 = 5;
        // population variance = 5/4 = 1.25.
        RunningStats stats = Filled(1.0, 2.0, 3.0, 4.0);

        Assert.Equal(2.5, stats.Mean, 12);
        Assert.Equal(Math.Sqrt(1.25), stats.Sigma, 12);
    }

    [Fact]
    public void MinMaxTrackNegativeAndOutOfOrderValues()
    {
        RunningStats stats = Filled(-3.0, 7.0, 1.0);

        Assert.Equal(-3.0, stats.Min);
        Assert.Equal(7.0, stats.Max);
        Assert.Equal(5.0 / 3.0, stats.Mean, 12);
    }

    [Fact]
    public void ResetClearsStateAndAccumulatorIsReusable()
    {
        RunningStats stats = Filled(2.0, 9.0);

        stats.Reset();
        Assert.Equal(0, stats.Count);
        Assert.Equal(0.0, stats.Sigma);

        stats.Add(10.0);
        Assert.Equal(1, stats.Count);
        Assert.Equal(10.0, stats.Min);
        Assert.Equal(10.0, stats.Max);
        Assert.Equal(10.0, stats.Mean);
        Assert.Equal(0.0, stats.Sigma);
    }
}
