using System.Collections.Generic;

namespace TimeGrapher.Core.Metrics;

/// <summary>
/// Port of RollingAverage (RollingAverage.h/.cpp). Maintains a running sum over a
/// FIFO window of doubles. The original used std::deque&lt;double&gt;; here a
/// LinkedList preserves the same front/push_back semantics.
/// </summary>
public sealed class RollingAverage
{
    private readonly LinkedList<double> _window = new();
    private long _maxSize; // size_t in the original
    private double _runningSum;

    public RollingAverage(long size)
    {
        _maxSize = size;
        _runningSum = 0.0;
    }

    public double Add(double val)
    {
        if (_maxSize == 0) return 0.0;

        _runningSum += val;
        _window.AddLast(val);

        if (_window.Count > _maxSize)
        {
            _runningSum -= _window.First!.Value;
            _window.RemoveFirst();
        }

        return _runningSum / _window.Count;
    }

    public int CurrentSize()
    {
        return _window.Count;
    }

    public void Resize(long newSize)
    {
        _maxSize = newSize;
        while (_window.Count > _maxSize)
        {
            _runningSum -= _window.First!.Value;
            _window.RemoveFirst();
        }
    }

    public void Reset()
    {
        _window.Clear();
        _runningSum = 0;
    }

    public double GetAverage()
    {
        if (_window.Count == 0) return 0.0;
        return _runningSum / _window.Count;
    }
}
