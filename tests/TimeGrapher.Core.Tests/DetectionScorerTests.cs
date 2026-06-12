using TimeGrapher.Core.Sim;
using Xunit;

namespace TimeGrapher.Core.Tests;

public sealed class DetectionScorerTests
{
    [Fact]
    public void PerfectDetection_ScoresFullPrecisionAndRecall()
    {
        double[] truth = { 1.0, 2.0, 3.0 };
        double[] detected = { 1.001, 2.001, 3.001 };

        DetectionScorer.Score score = DetectionScorer.Match(truth, detected);

        Assert.Equal(3, score.TruthCount);
        Assert.Equal(3, score.DetectedCount);
        Assert.Equal(3, score.Matched);
        Assert.Equal(1.0, score.Precision);
        Assert.Equal(1.0, score.Recall);
        Assert.Equal(1.0, score.MedianOffsetMs, 9);
        Assert.Equal(0.0, score.RmsAfterOffsetMs, 9);
    }

    [Fact]
    public void MissedBeat_LowersRecallOnly()
    {
        double[] truth = { 1.0, 2.0, 3.0 };
        double[] detected = { 1.001, 3.001 };

        DetectionScorer.Score score = DetectionScorer.Match(truth, detected);

        Assert.Equal(2, score.Matched);
        Assert.Equal(1.0, score.Precision);
        Assert.Equal(2.0 / 3.0, score.Recall, 9);
    }

    [Fact]
    public void SpuriousDetection_LowersPrecisionOnly()
    {
        double[] truth = { 1.0, 2.0 };
        double[] detected = { 1.001, 1.5, 2.001 };

        DetectionScorer.Score score = DetectionScorer.Match(truth, detected);

        Assert.Equal(2, score.Matched);
        Assert.Equal(2.0 / 3.0, score.Precision, 9);
        Assert.Equal(1.0, score.Recall);
    }

    [Fact]
    public void DoubleDetection_OnlyOneMatchesPerTruthEvent()
    {
        double[] truth = { 1.0 };
        double[] detected = { 0.999, 1.001 };

        DetectionScorer.Score score = DetectionScorer.Match(truth, detected);

        Assert.Equal(1, score.Matched);
        Assert.Equal(0.5, score.Precision, 9);
        Assert.Equal(1.0, score.Recall);
    }

    [Fact]
    public void GreedyMatching_EarlierTruthClaimsSharedDetectionFirst()
    {
        // The detection at 1.004 is within tolerance of both truths; the first
        // truth claims it (greedy in time order), the second matches 1.008.
        double[] truth = { 1.0, 1.006 };
        double[] detected = { 1.004, 1.008 };

        DetectionScorer.Score score = DetectionScorer.Match(truth, detected);

        Assert.Equal(2, score.Matched);
        Assert.Equal(1.0, score.Precision);
        Assert.Equal(1.0, score.Recall);
    }

    [Fact]
    public void ToleranceBoundary_ExactDistanceMatches()
    {
        double[] truth = { 1.0 };
        double[] detected = { 1.005 };

        DetectionScorer.Score score = DetectionScorer.Match(truth, detected, toleranceS: 0.005);

        Assert.Equal(1, score.Matched);
    }

    [Fact]
    public void OutsideTolerance_DoesNotMatch()
    {
        double[] truth = { 1.0 };
        double[] detected = { 1.006 };

        DetectionScorer.Score score = DetectionScorer.Match(truth, detected, toleranceS: 0.005);

        Assert.Equal(0, score.Matched);
        Assert.Equal(0.0, score.Precision);
        Assert.Equal(0.0, score.Recall);
    }

    [Fact]
    public void EvalStart_ExcludesEarlyEventsOnBothSides()
    {
        double[] truth = { 0.5, 1.0, 2.0 };
        double[] detected = { 0.4, 0.501, 1.001, 2.001 };

        DetectionScorer.Score score = DetectionScorer.Match(truth, detected, evalStartS: 0.9);

        Assert.Equal(2, score.TruthCount);
        Assert.Equal(2, score.DetectedCount);
        Assert.Equal(2, score.Matched);
        Assert.Equal(1.0, score.Precision);
        Assert.Equal(1.0, score.Recall);
    }

    [Fact]
    public void EmptyDetections_VacuousPrecisionZeroRecall()
    {
        double[] truth = { 1.0, 2.0 };

        DetectionScorer.Score score = DetectionScorer.Match(truth, ReadOnlySpan<double>.Empty);

        Assert.Equal(1.0, score.Precision);
        Assert.Equal(0.0, score.Recall);
        Assert.Equal(0.0, score.MedianOffsetMs);
        Assert.Equal(0.0, score.RmsAfterOffsetMs);
    }

    [Fact]
    public void EmptyTruth_VacuousRecallZeroPrecision()
    {
        double[] detected = { 1.0 };

        DetectionScorer.Score score = DetectionScorer.Match(ReadOnlySpan<double>.Empty, detected);

        Assert.Equal(1.0, score.Recall);
        Assert.Equal(0.0, score.Precision);
    }

    [Fact]
    public void OffsetStatistics_SeparateBiasFromJitter()
    {
        // Offsets: +2 ms, +2 ms, +4 ms -> median +2 ms, centered {0, 0, +2},
        // RMS = sqrt(4/3) ms.
        double[] truth = { 1.0, 2.0, 3.0 };
        double[] detected = { 1.002, 2.002, 3.004 };

        DetectionScorer.Score score = DetectionScorer.Match(truth, detected);

        Assert.Equal(2.0, score.MedianOffsetMs, 9);
        Assert.Equal(Math.Sqrt(4.0 / 3.0), score.RmsAfterOffsetMs, 9);
    }
}
