namespace TimeGrapher.Core.Metrics;

/// <summary>
/// Online min/max/mean/standard-deviation accumulator over a value stream:
/// Welford's algorithm keeps the mean and the sum of squared deviations (M2)
/// incrementally, so <see cref="Add"/> is O(1) with O(1) state and no sample is
/// ever stored — hour-long runs cost the same as second-long ones (SAP
/// performance tactic: bound resource usage / reduce overhead), and the result
/// is numerically stable where the naive sum-of-squares formula cancels.
/// <see cref="Sigma"/> is the population standard deviation (divide by N, not
/// N-1): the Vario display summarizes the spread of every beat actually
/// measured, not a sample-based estimate of a hypothetical wider population.
/// </summary>
public sealed class RunningStats
{
    private long _count;
    private double _mean;
    private double _m2;
    private double _min;
    private double _max;

    public long Count => _count;

    /// <summary>Smallest value seen so far (0 while empty — check <see cref="Count"/>).</summary>
    public double Min => _min;

    /// <summary>Largest value seen so far (0 while empty — check <see cref="Count"/>).</summary>
    public double Max => _max;

    /// <summary>Running mean (0 while empty — check <see cref="Count"/>).</summary>
    public double Mean => _mean;

    /// <summary>Population standard deviation, sqrt(M2/N) (0 while empty).</summary>
    public double Sigma => _count > 0 ? Math.Sqrt(_m2 / _count) : 0.0;

    public void Add(double value)
    {
        if (_count == 0)
        {
            _min = value;
            _max = value;
        }
        else
        {
            if (value < _min)
            {
                _min = value;
            }

            if (value > _max)
            {
                _max = value;
            }
        }

        _count++;
        double delta = value - _mean;
        _mean += delta / _count;
        _m2 += delta * (value - _mean);
    }

    public void Reset()
    {
        _count = 0;
        _mean = 0.0;
        _m2 = 0.0;
        _min = 0.0;
        _max = 0.0;
    }
}
