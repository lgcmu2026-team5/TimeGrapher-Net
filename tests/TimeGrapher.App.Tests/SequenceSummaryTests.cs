using TimeGrapher.App.Rendering;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

/// <summary>
/// Pure math behind the Multi-Position Sequence Display: per-position result
/// rows, the Witschi-style X (mean of all measured positions) and D (max−min
/// spread) sequence measures, the vertical-vs-horizontal comparison, and the
/// balance-wheel unbalance hint.
/// </summary>
public sealed class SequenceSummaryTests
{
    private static StatsSummary Stats(double mean, long count = 10) =>
        new(Valid: true, Min: mean, Max: mean, Mean: mean, Sigma: 0.0, Count: count);

    private static StatsSummary NoStats() => default;

    private static PositionSummary Position(
        WatchPosition position,
        double? rate = null,
        double? amplitude = null,
        double? beatError = null,
        long count = 10) => new(
        position,
        rate is double r ? Stats(r, count) : NoStats(),
        amplitude is double a ? Stats(a, count / 2) : NoStats(),
        beatError is double b ? Stats(b, count) : NoStats());

    [Fact]
    public void Compute_EmptySequenceHasNoRowsAndNoSummaries()
    {
        SequenceSummary summary = SequenceSummary.Compute(Array.Empty<PositionSummary>());

        Assert.Empty(summary.Rows);
        Assert.Null(summary.RateMeanSPerDay);
        Assert.Null(summary.RateSpreadSPerDay);
        Assert.Null(summary.AmplitudeMeanDeg);
        Assert.Null(summary.AmplitudeSpreadDeg);
        Assert.Null(summary.VerticalRateMeanSPerDay);
        Assert.Null(summary.HorizontalRateMeanSPerDay);
        Assert.Null(summary.VerticalHorizontalRateDeltaSPerDay);
        Assert.Null(summary.VerticalRateSpreadSPerDay);
        Assert.False(summary.UnbalanceSuspected);
    }

    [Fact]
    public void Compute_IntermediatePositionsCountTowardMeansButNotVHComparison()
    {
        // A 45° intermediate position joins X̄ and D but stays out of the
        // vertical/horizontal groups and the unbalance heuristic.
        SequenceSummary summary = SequenceSummary.Compute(new[]
        {
            Position(WatchPosition.CH, rate: 0.0),
            Position(WatchPosition.P6H, rate: 10.0),
            Position(WatchPosition.P6H45, rate: 100.0),
        });

        Assert.Equal(3, summary.Rows.Count);
        Assert.Equal((0.0 + 10.0 + 100.0) / 3.0, summary.RateMeanSPerDay!.Value, 9);
        Assert.Equal(100.0, summary.RateSpreadSPerDay!.Value, 9);

        // V/H comparison sees only CH (horizontal) and 6H (vertical).
        Assert.Equal(10.0, summary.VerticalRateMeanSPerDay!.Value, 9);
        Assert.Equal(0.0, summary.HorizontalRateMeanSPerDay!.Value, 9);
        Assert.Null(summary.VerticalRateSpreadSPerDay); // one vertical position only
        Assert.False(summary.UnbalanceSuspected);
    }

    [Fact]
    public void Compute_RowsCarryPerPositionMeansAndBeatCount()
    {
        SequenceSummary summary = SequenceSummary.Compute(new[]
        {
            Position(WatchPosition.CH, rate: 4.0, amplitude: 280.0, beatError: 0.3, count: 24),
        });

        SequencePositionRow row = Assert.Single(summary.Rows);
        Assert.Equal(WatchPosition.CH, row.Position);
        Assert.Equal(4.0, row.RateSPerDay);
        Assert.Equal(280.0, row.AmplitudeDeg);
        Assert.Equal(0.3, row.BeatErrorMs);
        // Largest of the three measure counts (amplitude pairs update at half rate).
        Assert.Equal(24, row.Beats);
    }

    [Fact]
    public void Compute_RowMeansAreNullUntilTheMeasureRecorded()
    {
        SequenceSummary summary = SequenceSummary.Compute(new[]
        {
            Position(WatchPosition.P6H, rate: -2.0, amplitude: null, beatError: null),
        });

        SequencePositionRow row = Assert.Single(summary.Rows);
        Assert.Equal(-2.0, row.RateSPerDay);
        Assert.Null(row.AmplitudeDeg);
        Assert.Null(row.BeatErrorMs);
    }

    [Fact]
    public void Compute_XIsTheMeanOfPositionMeansForRateAndAmplitude()
    {
        SequenceSummary summary = SequenceSummary.Compute(new[]
        {
            Position(WatchPosition.CH, rate: 2.0, amplitude: 280.0),
            Position(WatchPosition.CB, rate: 4.0, amplitude: 290.0),
            Position(WatchPosition.P6H, rate: 12.0), // no amplitude yet
        });

        Assert.Equal(6.0, summary.RateMeanSPerDay!.Value, 12);
        Assert.Equal(285.0, summary.AmplitudeMeanDeg!.Value, 12);
    }

    [Fact]
    public void Compute_DIsTheMaxMinSpreadOfPositionMeans()
    {
        SequenceSummary summary = SequenceSummary.Compute(new[]
        {
            Position(WatchPosition.CH, rate: 2.0, amplitude: 280.0),
            Position(WatchPosition.CB, rate: -4.0, amplitude: 292.0),
            Position(WatchPosition.P6H, rate: 5.0),
        });

        Assert.Equal(9.0, summary.RateSpreadSPerDay!.Value, 12);
        Assert.Equal(12.0, summary.AmplitudeSpreadDeg!.Value, 12);
    }

    [Fact]
    public void Compute_SpreadNeedsTwoMeasuredPositions()
    {
        SequenceSummary summary = SequenceSummary.Compute(new[]
        {
            Position(WatchPosition.CH, rate: 2.0, amplitude: 280.0),
        });

        // X exists from the first position; a one-position "spread" would be a
        // degenerate 0, so D stays unknown instead.
        Assert.Equal(2.0, summary.RateMeanSPerDay);
        Assert.Null(summary.RateSpreadSPerDay);
        Assert.Null(summary.AmplitudeSpreadDeg);
    }

    [Fact]
    public void Compute_ComparesVerticalAgainstHorizontalRateMeans()
    {
        SequenceSummary summary = SequenceSummary.Compute(new[]
        {
            Position(WatchPosition.CH, rate: 1.0),  // horizontal
            Position(WatchPosition.CB, rate: 3.0),  // horizontal
            Position(WatchPosition.P6H, rate: -4.0), // vertical
            Position(WatchPosition.P12H, rate: -6.0), // vertical
        });

        Assert.Equal(-5.0, summary.VerticalRateMeanSPerDay!.Value, 12);
        Assert.Equal(2.0, summary.HorizontalRateMeanSPerDay!.Value, 12);
        Assert.Equal(-7.0, summary.VerticalHorizontalRateDeltaSPerDay!.Value, 12);
    }

    [Fact]
    public void Compute_VerticalHorizontalDeltaNeedsBothOrientations()
    {
        SequenceSummary summary = SequenceSummary.Compute(new[]
        {
            Position(WatchPosition.CH, rate: 1.0),
            Position(WatchPosition.CB, rate: 3.0),
        });

        Assert.Equal(2.0, summary.HorizontalRateMeanSPerDay!.Value, 12);
        Assert.Null(summary.VerticalRateMeanSPerDay);
        Assert.Null(summary.VerticalHorizontalRateDeltaSPerDay);
    }

    [Fact]
    public void Compute_FlagsUnbalanceWhenVerticalRateSpreadExceedsThreshold()
    {
        // 6H -10 s/d vs 12H +8 s/d: 18 s/d spread among the hanging positions —
        // the heuristic's balance-wheel unbalance signature.
        SequenceSummary summary = SequenceSummary.Compute(new[]
        {
            Position(WatchPosition.CH, rate: 0.0),
            Position(WatchPosition.P6H, rate: -10.0),
            Position(WatchPosition.P12H, rate: 8.0),
        });

        Assert.Equal(18.0, summary.VerticalRateSpreadSPerDay!.Value, 12);
        Assert.True(summary.UnbalanceSuspected);
    }

    [Fact]
    public void Compute_SpreadAcrossOrientationsDoesNotFlagUnbalance()
    {
        // Large vertical-vs-horizontal difference but consistent hanging
        // positions: a regulator-pin symptom, not unbalance — no flag.
        SequenceSummary summary = SequenceSummary.Compute(new[]
        {
            Position(WatchPosition.CH, rate: 20.0),
            Position(WatchPosition.P6H, rate: -1.0),
            Position(WatchPosition.P12H, rate: 1.0),
        });

        Assert.Equal(2.0, summary.VerticalRateSpreadSPerDay!.Value, 12);
        Assert.False(summary.UnbalanceSuspected);
        Assert.Equal(21.0, summary.RateSpreadSPerDay!.Value, 12);
    }

    [Fact]
    public void Compute_UnbalanceThresholdIsExclusive()
    {
        SequenceSummary summary = SequenceSummary.Compute(new[]
        {
            Position(WatchPosition.P6H, rate: 0.0),
            Position(WatchPosition.P12H, rate: SequenceSummary.UnbalanceVerticalRateSpreadSPerDay),
        });

        Assert.False(summary.UnbalanceSuspected);
    }

    [Fact]
    public void Compute_SingleVerticalPositionCannotFlagUnbalance()
    {
        SequenceSummary summary = SequenceSummary.Compute(new[]
        {
            Position(WatchPosition.P6H, rate: 99.0),
        });

        Assert.Null(summary.VerticalRateSpreadSPerDay);
        Assert.False(summary.UnbalanceSuspected);
    }
}
