using TimeGrapher.App.Rendering;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

/// <summary>
/// Pure logic behind the Vario stability tab: gauge window policy, readout
/// formatting, and review-cursor series lookup.
/// </summary>
public sealed class VarioLogicTests
{
    [Fact]
    public void GaugeRange_CoversAcceptableBandWithPadding()
    {
        (double lo, double hi) = VarioGaugePolicy.GaugeRange(-10.0, 10.0, default, null);

        // 20-wide band padded by 5% on each side.
        Assert.Equal(-11.0, lo, 9);
        Assert.Equal(11.0, hi, 9);
    }

    [Fact]
    public void GaugeRange_WidensToIncludeOutliersAndCurrent()
    {
        var stats = new StatsSummary(Valid: true, Min: -25.0, Max: 5.0, Mean: -3.0, Sigma: 4.0, Count: 10);
        (double lo, double hi) = VarioGaugePolicy.GaugeRange(-10.0, 10.0, stats, current: 30.0);

        // Span widens to [-25, 30] before padding; markers never sit on the edge.
        Assert.True(lo < -25.0);
        Assert.True(hi > 30.0);
    }

    [Fact]
    public void AmplitudePolicy_MatchesTraceAlertBand()
    {
        // One source of truth: the vario green zone IS the trace alert band.
        Assert.Equal(TraceAlertEvaluator.AmplitudeMinDeg, VarioGaugePolicy.AmplitudeAcceptMinDeg);
        Assert.Equal(TraceAlertEvaluator.AmplitudeMaxDeg, VarioGaugePolicy.AmplitudeAcceptMaxDeg);
    }

    [Fact]
    public void Format_RendersValueWithUnitOrDash()
    {
        Assert.Equal("+5.2 s/d", VarioReadout.Format(5.2, "+0.0;-0.0;0.0", " s/d"));
        Assert.Equal("285°", VarioReadout.Format(285.4, "0", "°"));
        Assert.Equal(VarioReadout.Missing, VarioReadout.Format(null, "0.0", "°"));
    }

    [Theory]
    [InlineData(0.0, "00:00")]
    [InlineData(83.0, "01:23")]
    [InlineData(3661.0, "1:01:01")]
    public void FormatElapsed_SwitchesToHoursFormAtOneHour(double seconds, string expected)
    {
        Assert.Equal(expected, VarioReadout.FormatElapsed(seconds));
    }

    [Fact]
    public void ValueAt_ReturnsNewestPointAtOrBeforeTheCursor()
    {
        var series = new MetricsHistorySeries
        {
            X = new[] { 10.0, 20.0, 30.0 },
            Y = new[] { 1.0, 2.0, 3.0 },
        };

        Assert.Equal(2.0, VarioReadout.ValueAt(series, 25.0));
        Assert.Equal(3.0, VarioReadout.ValueAt(series, 30.0));
        Assert.Null(VarioReadout.ValueAt(series, 5.0));
        Assert.Null(VarioReadout.ValueAt(MetricsHistorySeries.Empty, 25.0));
    }
}
