namespace TimeGrapher.App.Rendering;

/// <summary>
/// The shared strip-lane sampling policy: max-decimate an envelope segment to a
/// point budget (envelope-preserving) and normalize to the segment's own peak so
/// beat shapes compare at a glance. Callers map each emitted point into their
/// own coordinate space (strip slots, A-aligned lanes, ...).
/// </summary>
internal static class EnvelopeLaneSampler
{
    /// <param name="point">Decimated point index (0-based).</param>
    /// <param name="pointCount">Total decimated points.</param>
    /// <param name="stride">Source samples folded into each point.</param>
    /// <param name="normalizedValue">Bucket max divided by the segment peak (0..1).</param>
    public delegate void EmitPoint(int point, int pointCount, int stride, double normalizedValue);

    public static void MaxDecimateNormalized(ReadOnlySpan<float> samples, int pointBudget, EmitPoint emit)
    {
        if (samples.Length == 0)
        {
            return;
        }

        int stride = Math.Max(1, samples.Length / pointBudget);

        float peak = 0f;
        foreach (float value in samples)
        {
            if (value > peak)
            {
                peak = value;
            }
        }

        if (peak <= 0f)
        {
            peak = 1f;
        }

        int points = samples.Length / stride;
        for (int p = 0; p < points; p++)
        {
            float max = samples[p * stride];
            for (int s = 1; s < stride; s++)
            {
                float value = samples[p * stride + s];
                if (value > max)
                {
                    max = value;
                }
            }

            emit(p, points, stride, max / peak);
        }
    }
}
