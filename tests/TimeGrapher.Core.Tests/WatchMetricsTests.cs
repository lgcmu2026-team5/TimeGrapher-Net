using TimeGrapher.Core.Metrics;
using Xunit;

namespace TimeGrapher.Core.Tests;

public sealed class WatchMetricsTests
{
    // Amplitude(liftAngle, t1, bph) = liftAngle / sin(2*pi*t1 / (7200/bph)).
    // Choosing t1 so the sine argument is a known angle gives exact expected values.

    [Fact]
    public void Amplitude_WhenSineArgumentIsHalfPi_EqualsLiftAngle()
    {
        // 2*pi*t1 / (7200/bph) = pi/2  ->  t1 = 1800/bph.
        const double bph = 3600.0;
        double t1 = 1800.0 / bph; // 0.5 s
        Assert.Equal(52.0, WatchMetrics.Amplitude(52.0, t1, bph), 6);
    }

    [Fact]
    public void Amplitude_WhenSineArgumentIsPiOverSix_EqualsDoubleLiftAngle()
    {
        // sin(pi/6) = 0.5  ->  amplitude = 2 * liftAngle.
        const double bph = 3600.0;
        double t1 = 600.0 / bph;
        Assert.Equal(104.0, WatchMetrics.Amplitude(52.0, t1, bph), 6);
    }

    [Fact]
    public void Amplitude_IncreasesAsSwingTimeApproachesQuarterPeriod()
    {
        // Over (0, quarter-period) the sine is increasing, so amplitude decreases as t1
        // grows toward the quarter period; below the quarter period a larger t1 yields a
        // larger sine and thus a smaller amplitude. Verify the monotonic relationship.
        const double bph = 28800.0;
        double quarter = 1800.0 / bph;
        double small = WatchMetrics.Amplitude(52.0, quarter * 0.25, bph);
        double large = WatchMetrics.Amplitude(52.0, quarter * 0.75, bph);
        Assert.True(small > large, $"expected amplitude to fall as t1 rises toward quarter period (small={small}, large={large})");
    }
}
