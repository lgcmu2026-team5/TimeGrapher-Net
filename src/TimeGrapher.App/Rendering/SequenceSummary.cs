using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// One Multi-Position Sequence table row: the per-position result means of the
/// measures the plan requires (rate, amplitude, beat error) plus how many beats
/// contributed. A mean is null until that measure recorded its first sample at
/// the position. Beats is the largest of the three measure counts — rate needs
/// a regression warm-up and amplitude updates once per tic/toc pair, so the
/// beat-error count usually carries the full tally.
/// </summary>
internal sealed record SequencePositionRow(
    WatchPosition Position,
    double? RateSPerDay,
    double? AmplitudeDeg,
    double? BeatErrorMs,
    long Beats);

/// <summary>
/// Pure summary math behind the Multi-Position Sequence Display, computed from
/// the cumulative per-position aggregates the frame snapshot already carries
/// (kept out of the renderer so it is unit-testable without live controls).
/// Sequence measures follow the Witschi Chronoscope X1 G3 manual's Sequence
/// display: X = mean over all measured positions (of the per-position means),
/// D = difference between the largest and smallest per-position mean, and the
/// vertical-vs-horizontal comparison (the manual's DVH). Means need one
/// measured position; spreads and the V/H delta need two, so a half-finished
/// sequence shows em dashes instead of degenerate zeros.
/// </summary>
internal sealed record SequenceSummary(
    IReadOnlyList<SequencePositionRow> Rows,
    double? RateMeanSPerDay,
    double? RateSpreadSPerDay,
    double? AmplitudeMeanDeg,
    double? AmplitudeSpreadDeg,
    double? VerticalRateMeanSPerDay,
    double? HorizontalRateMeanSPerDay,
    double? VerticalHorizontalRateDeltaSPerDay,
    double? VerticalRateSpreadSPerDay,
    bool UnbalanceSuspected)
{
    /// <summary>
    /// Unbalance-hint threshold (heuristic): the Witschi training course reads
    /// "large rate variations between different vertical test positions" as
    /// balance-wheel unbalance (action: centering/balancing). This project
    /// flags the hint once the rate spread across the measured hanging
    /// positions exceeds 15 s/d.
    /// </summary>
    public const double UnbalanceVerticalRateSpreadSPerDay = 15.0;

    public static SequenceSummary Compute(IReadOnlyList<PositionSummary> positions)
    {
        var rows = new List<SequencePositionRow>(positions.Count);
        var rateMeans = new List<double>(positions.Count);
        var amplitudeMeans = new List<double>(positions.Count);
        var verticalRateMeans = new List<double>(positions.Count);
        var horizontalRateMeans = new List<double>(positions.Count);

        foreach (PositionSummary position in positions)
        {
            rows.Add(new SequencePositionRow(
                position.Position,
                position.Rate.Valid ? position.Rate.Mean : null,
                position.Amplitude.Valid ? position.Amplitude.Mean : null,
                position.BeatError.Valid ? position.BeatError.Mean : null,
                Math.Max(position.Rate.Count,
                    Math.Max(position.Amplitude.Count, position.BeatError.Count))));

            if (position.Rate.Valid)
            {
                rateMeans.Add(position.Rate.Mean);
                // 45° intermediate positions count toward the sequence means and
                // spreads but not toward the V/H comparison or the unbalance
                // heuristic, which the manual defines over full positions only.
                if (!position.Position.IsIntermediate())
                {
                    (position.Position.IsHorizontal() ? horizontalRateMeans : verticalRateMeans)
                        .Add(position.Rate.Mean);
                }
            }

            if (position.Amplitude.Valid)
            {
                amplitudeMeans.Add(position.Amplitude.Mean);
            }
        }

        double? verticalRateMean = Mean(verticalRateMeans);
        double? horizontalRateMean = Mean(horizontalRateMeans);
        double? verticalRateSpread = Spread(verticalRateMeans);

        return new SequenceSummary(
            rows,
            Mean(rateMeans),
            Spread(rateMeans),
            Mean(amplitudeMeans),
            Spread(amplitudeMeans),
            verticalRateMean,
            horizontalRateMean,
            verticalRateMean is double vertical && horizontalRateMean is double horizontal
                ? vertical - horizontal
                : null,
            verticalRateSpread,
            verticalRateSpread is double spread && spread > UnbalanceVerticalRateSpreadSPerDay);
    }

    /// <summary>Unweighted mean over the per-position means; null when none measured.</summary>
    private static double? Mean(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        double sum = 0.0;
        foreach (double value in values)
        {
            sum += value;
        }

        return sum / values.Count;
    }

    /// <summary>Max−min over the per-position means; null until two positions measured.</summary>
    private static double? Spread(List<double> values)
    {
        if (values.Count < 2)
        {
            return null;
        }

        double min = values[0];
        double max = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            min = Math.Min(min, values[i]);
            max = Math.Max(max, values[i]);
        }

        return max - min;
    }
}
