using System;

namespace TimeGrapher.Core.Metrics;

/// <summary>
/// Port of RollingLeastSquares (RollingLeastSquares.h/.cpp). Rolling linear
/// regression over a circular buffer of (x,y) points; GetRate returns the slope.
/// The original used raw new[]/delete[]; here managed double[] arrays replace them
/// (no destructor / Dispose needed).
/// </summary>
public sealed class RollingLeastSquares
{
    private double[] _xBuf = Array.Empty<double>();
    private double[] _yBuf = Array.Empty<double>();
    private int _capacity;
    private int _size;
    private int _head;
    private double _sumX, _sumY, _sumXY, _sumX2;

    private void Deallocate()
    {
        // Managed arrays; nothing to free. Mirror of delete[] in the original.
        _xBuf = Array.Empty<double>();
        _yBuf = Array.Empty<double>();
    }

    private void Allocate(int newCapacity)
    {
        _capacity = newCapacity;
        _xBuf = new double[_capacity];
        _yBuf = new double[_capacity];
        Reset();
    }

    public RollingLeastSquares(int windowSize)
    {
        _size = 0;
        _head = 0;
        Allocate(windowSize);
    }

    public void Reset()
    {
        _size = 0;
        _head = 0;
        _sumX = _sumY = _sumXY = _sumX2 = 0.0;
    }

    public void Resize(int newSize)
    {
        Deallocate();
        Allocate(newSize);
    }

    public void AddPoint(double x, double y)
    {
        if (_capacity == 0) return;

        // If buffer full, remove oldest data from sums
        if (_size == _capacity)
        {
            double oldX = _xBuf[_head];
            double oldY = _yBuf[_head];
            _sumX -= oldX;
            _sumY -= oldY;
            _sumXY -= oldX * oldY;
            _sumX2 -= oldX * oldX;
        }
        else
        {
            _size++;
        }

        // Add new data
        _xBuf[_head] = x;
        _yBuf[_head] = y;
        _sumX += x;
        _sumY += y;
        _sumXY += x * y;
        _sumX2 += x * x;

        _head = (_head + 1) % _capacity; // Circular buffer
    }

    public bool GetRate(out double slope)
    {
        slope = 0.0;
        if (_size < 2) return false;

        double n = _size;
        double denominator = (n * _sumX2 - _sumX * _sumX);

        if (Math.Abs(denominator) < 1e-10) return false; // Singular matrix

        slope = (n * _sumXY - _sumX * _sumY) / denominator;
        return true;
    }
}
