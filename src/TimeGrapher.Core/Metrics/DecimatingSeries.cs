namespace TimeGrapher.Core.Metrics;

/// <summary>
/// Bounded time-series store for arbitrarily long runs: a fixed-capacity point
/// array that halves its resolution (merging adjacent point pairs) every time it
/// fills, so hour-long histories cost the same memory as second-long ones
/// (SAP performance tactic: bound resource usage). Each stored point is the
/// average of a fixed-size bucket of raw samples; per-bucket min/max are kept so
/// displays can draw a variation band as buckets coarsen. Buckets stay uniform:
/// a pending bucket that is mid-fill when the store compacts simply continues
/// filling to the doubled bucket size before it is flushed.
/// </summary>
public sealed class DecimatingSeries
{
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double[] _yMin;
    private readonly double[] _yMax;
    private int _count;
    private int _bucketSize = 1;

    private double _pendingXSum;
    private double _pendingYSum;
    private double _pendingYMin;
    private double _pendingYMax;
    private int _pendingCount;

    public DecimatingSeries(int capacity)
    {
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least 2.");
        }
        if ((capacity & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be even so point pairs merge cleanly.");
        }

        _x = new double[capacity];
        _y = new double[capacity];
        _yMin = new double[capacity];
        _yMax = new double[capacity];
    }

    public int Count => _count;

    /// <summary>Raw samples represented by one stored point (doubles on each compaction).</summary>
    public int BucketSize => _bucketSize;

    public void Add(double x, double y)
    {
        if (_pendingCount == 0)
        {
            _pendingYMin = y;
            _pendingYMax = y;
        }
        else
        {
            _pendingYMin = Math.Min(_pendingYMin, y);
            _pendingYMax = Math.Max(_pendingYMax, y);
        }

        _pendingXSum += x;
        _pendingYSum += y;
        _pendingCount++;

        if (_pendingCount < _bucketSize)
        {
            return;
        }

        if (_count == _x.Length)
        {
            Compact();
            if (_pendingCount < _bucketSize)
            {
                return;
            }
        }

        _x[_count] = _pendingXSum / _pendingCount;
        _y[_count] = _pendingYSum / _pendingCount;
        _yMin[_count] = _pendingYMin;
        _yMax[_count] = _pendingYMax;
        _count++;

        _pendingXSum = 0.0;
        _pendingYSum = 0.0;
        _pendingCount = 0;
    }

    public void Reset()
    {
        _count = 0;
        _bucketSize = 1;
        _pendingXSum = 0.0;
        _pendingYSum = 0.0;
        _pendingCount = 0;
    }

    /// <summary>Copies the stored points into the given lists (cleared first).</summary>
    public void SnapshotTo(List<double> x, List<double> y, List<double>? yMin = null, List<double>? yMax = null)
    {
        x.Clear();
        y.Clear();
        yMin?.Clear();
        yMax?.Clear();

        for (int i = 0; i < _count; i++)
        {
            x.Add(_x[i]);
            y.Add(_y[i]);
            yMin?.Add(_yMin[i]);
            yMax?.Add(_yMax[i]);
        }
    }

    private void Compact()
    {
        int half = _count / 2;
        for (int i = 0; i < half; i++)
        {
            int a = 2 * i;
            int b = a + 1;
            _x[i] = (_x[a] + _x[b]) / 2.0;
            _y[i] = (_y[a] + _y[b]) / 2.0;
            _yMin[i] = Math.Min(_yMin[a], _yMin[b]);
            _yMax[i] = Math.Max(_yMax[a], _yMax[b]);
        }

        _count = half;
        _bucketSize *= 2;
    }
}
