namespace TimeGrapher.Core.Sim;

/// <summary>
/// Event-level detection scoring against the synthetic stream's ground-truth
/// side channel. Pure static helper shared by the headless verifier and the
/// unit tests so both score with identical semantics.
///
/// Matching is greedy nearest-neighbour 1:1 over time-ascending sequences:
/// each truth event (in order) claims the closest unused detection within
/// <c>toleranceS</c>. Events earlier than <c>evalStartS</c> are excluded on
/// both sides so detector warm-up does not bias the score.
/// </summary>
public static class DetectionScorer
{
    /// <param name="TruthCount">Truth events at or after evalStart.</param>
    /// <param name="DetectedCount">Detections at or after evalStart.</param>
    /// <param name="Matched">1:1 pairs within tolerance.</param>
    /// <param name="Precision">Matched / DetectedCount (1.0 when no detections).</param>
    /// <param name="Recall">Matched / TruthCount (1.0 when no truth).</param>
    /// <param name="MedianOffsetMs">Median of (detected - truth) over matched pairs, ms.</param>
    /// <param name="RmsAfterOffsetMs">RMS of the matched offsets after removing the
    /// median (timing jitter with the constant detection latency taken out), ms.</param>
    public sealed record Score(
        int TruthCount,
        int DetectedCount,
        int Matched,
        double Precision,
        double Recall,
        double MedianOffsetMs,
        double RmsAfterOffsetMs);

    public static Score Match(
        ReadOnlySpan<double> truthTimesS,
        ReadOnlySpan<double> detectedTimesS,
        double toleranceS = 0.005,
        double evalStartS = 0.0)
    {
        double[] truth = FilterAndSort(truthTimesS, evalStartS);
        double[] detected = FilterAndSort(detectedTimesS, evalStartS);

        var usedDetection = new bool[detected.Length];
        var offsets = new List<double>(Math.Min(truth.Length, detected.Length));

        int searchStart = 0;
        for (int t = 0; t < truth.Length; t++)
        {
            double truthTime = truth[t];

            /* Both sequences ascend, so detections left of (truthTime - tol)
             * can never match this or any later truth event. */
            while (searchStart < detected.Length && detected[searchStart] < truthTime - toleranceS)
            {
                searchStart++;
            }

            int best = -1;
            double bestDistance = double.MaxValue;
            for (int d = searchStart; d < detected.Length && detected[d] <= truthTime + toleranceS; d++)
            {
                if (usedDetection[d])
                {
                    continue;
                }
                double distance = Math.Abs(detected[d] - truthTime);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = d;
                }
            }

            if (best >= 0)
            {
                usedDetection[best] = true;
                offsets.Add(detected[best] - truthTime);
            }
        }

        int matched = offsets.Count;
        double precision = detected.Length == 0 ? 1.0 : (double)matched / detected.Length;
        double recall = truth.Length == 0 ? 1.0 : (double)matched / truth.Length;

        double medianOffsetMs = 0.0;
        double rmsAfterOffsetMs = 0.0;
        if (matched > 0)
        {
            double[] sortedOffsets = offsets.ToArray();
            Array.Sort(sortedOffsets);
            double median = (matched & 1) != 0
                ? sortedOffsets[matched / 2]
                : 0.5 * (sortedOffsets[matched / 2 - 1] + sortedOffsets[matched / 2]);

            double sumSq = 0.0;
            foreach (double offset in offsets)
            {
                double centered = offset - median;
                sumSq += centered * centered;
            }

            medianOffsetMs = median * 1000.0;
            rmsAfterOffsetMs = Math.Sqrt(sumSq / matched) * 1000.0;
        }

        return new Score(truth.Length, detected.Length, matched,
                         precision, recall, medianOffsetMs, rmsAfterOffsetMs);
    }

    private static double[] FilterAndSort(ReadOnlySpan<double> times, double evalStartS)
    {
        var kept = new List<double>(times.Length);
        foreach (double time in times)
        {
            if (time >= evalStartS)
            {
                kept.Add(time);
            }
        }
        double[] result = kept.ToArray();
        Array.Sort(result);
        return result;
    }
}
