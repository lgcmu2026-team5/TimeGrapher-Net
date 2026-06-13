using System.Collections.Generic;
using System.Linq;
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
    public void AcceptBand_IsHiddenUntilAReadingExists()
    {
        var stats = new StatsSummary(Valid: true, Min: -2.0, Max: 4.0, Mean: 1.0, Sigma: 0.5, Count: 4);

        Assert.False(VarioGaugePolicy.ShouldShowAcceptBand(default, current: null));
        Assert.True(VarioGaugePolicy.ShouldShowAcceptBand(stats, current: null));
        Assert.True(VarioGaugePolicy.ShouldShowAcceptBand(default, current: 1.0));
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
    public void ForRate_ClassifiesPendingInRangeUnstableAndOutOfRange()
    {
        var few = new StatsSummary(Valid: true, Min: 0, Max: 0, Mean: 0, Sigma: 0, Count: 5);
        Assert.Equal(VarioVerdictLevel.Pending, VarioVerdict.ForRate(few, -10, 10).Level);

        var stable = new StatsSummary(true, -2, 4, 1.0, 1.0, 200);
        Assert.Equal(VarioVerdictLevel.Good, VarioVerdict.ForRate(stable, -10, 10).Level);

        var jittery = new StatsSummary(true, -9, 9, 1.0, 5.0, 200);
        Assert.Equal(VarioVerdictLevel.Warn, VarioVerdict.ForRate(jittery, -10, 10).Level);

        var fast = new StatsSummary(true, 8, 20, 15.0, 2.0, 200);
        VarioVerdict fastVerdict = VarioVerdict.ForRate(fast, -10, 10);
        Assert.Equal(VarioVerdictLevel.Bad, fastVerdict.Level);
        Assert.Contains("Fast", fastVerdict.Text);

        var slow = new StatsSummary(true, -20, -8, -15.0, 2.0, 200);
        Assert.Contains("Slow", VarioVerdict.ForRate(slow, -10, 10).Text);
    }

    [Fact]
    public void ForAmplitude_ClassifiesHealthyLowHighAndService()
    {
        var healthy = new StatsSummary(true, 275, 295, 285.0, 4.0, 200);
        Assert.Equal(VarioVerdictLevel.Good, VarioVerdict.ForAmplitude(healthy, 270, 300).Level);

        var slightlyLow = new StatsSummary(true, 240, 260, 250.0, 4.0, 200);
        Assert.Equal(VarioVerdictLevel.Warn, VarioVerdict.ForAmplitude(slightlyLow, 270, 300).Level);

        var high = new StatsSummary(true, 305, 315, 310.0, 4.0, 200);
        Assert.Equal(VarioVerdictLevel.Warn, VarioVerdict.ForAmplitude(high, 270, 300).Level);

        var service = new StatsSummary(true, 190, 215, 200.0, 5.0, 200);
        VarioVerdict serviceVerdict = VarioVerdict.ForAmplitude(service, 270, 300);
        Assert.Equal(VarioVerdictLevel.Bad, serviceVerdict.Level);
        Assert.Contains("service", serviceVerdict.Text);
    }

    [Fact]
    public void Overall_TakesWorseSeverityAndStaysPendingUntilBothReady()
    {
        var good = new VarioVerdict("Stable · in range", VarioVerdictLevel.Good);
        var warn = new VarioVerdict("Slightly low", VarioVerdictLevel.Warn);
        var bad = new VarioVerdict("Low · service", VarioVerdictLevel.Bad);

        Assert.Equal(VarioVerdictLevel.Good, VarioVerdict.Overall(good, good).Level);
        Assert.Equal(VarioVerdictLevel.Warn, VarioVerdict.Overall(good, warn).Level);
        Assert.Equal(VarioVerdictLevel.Bad, VarioVerdict.Overall(good, bad).Level);

        VarioVerdict overall = VarioVerdict.Overall(good, bad);
        Assert.Equal("Overall: ALERT - Service required before recording", overall.Text);
        Assert.DoesNotContain("Stable · in range", overall.Text);
        Assert.DoesNotContain("Low · service", overall.Text);

        Assert.Equal(VarioVerdictLevel.Pending, VarioVerdict.Overall(VarioVerdict.Measuring, good).Level);
    }

    // ---- Gauge label layout (lane placement / edge anchoring) stress tests ----

    private static void AssertNoSameLaneLabelOverlap(IReadOnlyList<GaugeLabel> labels, double lo, double hi)
    {
        double minGap = (hi - lo) * VarioGaugeLayout.LabelWidthFraction;
        foreach (IGrouping<double, GaugeLabel> lane in labels.GroupBy(l => l.Y))
        {
            GaugeLabel[] ordered = lane.OrderBy(l => l.X).ToArray();
            for (int i = 1; i < ordered.Length; i++)
            {
                Assert.True(ordered[i].X - ordered[i - 1].X >= minGap,
                    $"labels '{ordered[i - 1].Role}' and '{ordered[i].Role}' overlap on lane {lane.Key}");
            }
        }
    }

    [Fact]
    public void Layout_WellSpreadValues_ShowsAllFourLabels()
    {
        IReadOnlyList<GaugeLabel> labels =
            VarioGaugeLayout.LayOut(-11, 11, min: -4.5, max: 6.6, avg: 4.3, current: 2.7);

        Assert.Equal(4, labels.Count);
        Assert.Equal(new[] { "min", "current", "avg", "max" }, labels.Select(l => l.Role));
        Assert.Equal(VarioGaugeLayout.MinMaxLabelY, labels.Single(l => l.Role == "min").Y);
        Assert.Equal(VarioGaugeLayout.MinMaxLabelY, labels.Single(l => l.Role == "max").Y);
        Assert.Equal(VarioGaugeLayout.AverageLabelY, labels.Single(l => l.Role == "avg").Y);
        Assert.Equal(VarioGaugeLayout.CurrentLabelY, labels.Single(l => l.Role == "current").Y);
        AssertNoSameLaneLabelOverlap(labels, -11, 11);
    }

    [Fact]
    public void Layout_TightCluster_UsesSeparateLanes()
    {
        IReadOnlyList<GaugeLabel> labels =
            VarioGaugeLayout.LayOut(195, 305, min: 200, max: 201, avg: 200.5, current: 200.7);

        Assert.Equal(new[] { "min/max", "avg", "current" }, labels.Select(l => l.Role));
        Assert.Equal(VarioGaugeLayout.MinMaxLabelY, labels.Single(l => l.Role == "min/max").Y);
        Assert.Equal(VarioGaugeLayout.AverageLabelY, labels.Single(l => l.Role == "avg").Y);
        Assert.Equal(VarioGaugeLayout.CurrentLabelY, labels.Single(l => l.Role == "current").Y);
        AssertNoSameLaneLabelOverlap(labels, 195, 305);
    }

    [Fact]
    public void Layout_AvgAndCurrentClose_UsesSeparateLanes()
    {
        IReadOnlyList<GaugeLabel> labels =
            VarioGaugeLayout.LayOut(185, 305, min: 190, max: 240, avg: 215, current: 215.5);

        Assert.Contains(labels, l => l.Role == "avg");
        Assert.Contains(labels, l => l.Role == "current");
        Assert.Equal(VarioGaugeLayout.AverageLabelY, labels.Single(l => l.Role == "avg").Y);
        Assert.Equal(VarioGaugeLayout.CurrentLabelY, labels.Single(l => l.Role == "current").Y);
        AssertNoSameLaneLabelOverlap(labels, 185, 305);
    }

    [Fact]
    public void Layout_EdgeMarkers_AnchorInwardToAvoidClipping()
    {
        IReadOnlyList<GaugeLabel> labels =
            VarioGaugeLayout.LayOut(-11, 11, min: -10.8, max: 10.9, avg: 0.0, current: 0.0);

        Assert.Equal(GaugeLabelAnchor.Left, labels.Single(l => l.Role == "min").Anchor);
        Assert.Equal(GaugeLabelAnchor.Right, labels.Single(l => l.Role == "max").Anchor);
        Assert.All(labels.Where(l => l.Role is "avg" or "current"), l => Assert.Equal(GaugeLabelAnchor.Center, l.Anchor));
        Assert.Equal(VarioGaugeLayout.AverageLabelY, labels.Single(l => l.Role == "avg").Y);
        Assert.Equal(VarioGaugeLayout.CurrentLabelY, labels.Single(l => l.Role == "current").Y);
    }

    [Fact]
    public void Layout_MarkerNearButNotAtEdge_KeepsCenteredAnchor()
    {
        IReadOnlyList<GaugeLabel> labels =
            VarioGaugeLayout.LayOut(188.1, 303.9, min: 192, max: 205, avg: 200, current: 199);

        Assert.Equal(GaugeLabelAnchor.Center, labels.Single(l => l.Role == "min").Anchor);
    }

    [Theory]
    // A sweep of arrangements: spread, clustered low, clustered high/above band,
    // wide, all-negative, identical values — none may overlap within the same lane.
    [InlineData(-11.0, 11.0, -4.5, 6.6, 4.3, 2.7)]
    [InlineData(195.0, 305.0, 200.0, 216.0, 207.0, 203.0)]
    [InlineData(265.0, 340.0, 305.0, 335.0, 320.0, 330.0)]
    [InlineData(-60.0, 60.0, -50.0, 55.0, 2.0, -3.0)]
    [InlineData(-11.0, 11.0, -9.0, -8.5, -8.7, -8.6)]
    [InlineData(-11.0, 11.0, 5.0, 5.0, 5.0, 5.0)]
    public void Layout_NeverOverlaps_AcrossArrangements(
        double lo, double hi, double min, double max, double avg, double current)
    {
        IReadOnlyList<GaugeLabel> labels = VarioGaugeLayout.LayOut(lo, hi, min, max, avg, current);
        AssertNoSameLaneLabelOverlap(labels, lo, hi);
        Assert.All(labels, l => Assert.InRange(l.X, lo, hi));
        Assert.All(labels, l => Assert.InRange(l.Y, VarioGaugeLayout.CurrentLabelY, VarioGaugeLayout.MinMaxLabelY));
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
