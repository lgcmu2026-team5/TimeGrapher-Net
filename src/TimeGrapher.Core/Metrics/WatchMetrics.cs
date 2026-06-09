using System;
using System.Collections.Generic;
using System.Globalization;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Metrics;

/// <summary>Port of WatchMetricsConfig (WatchMetrics.h).</summary>
public struct WatchMetricsConfig
{
    public int SampleRate;          // 48000
    public double LiftAngle;        // 52.0
    public int AveragingPeriod;     // 2
    public int MaxRateDataPoints;   // 250
    public double RateErrorYScale;  // 10.0
    public int RlsWindowInit;       // 100

    /// <summary>Mirror of the C++ struct's in-class default member initializers.</summary>
    public WatchMetricsConfig()
    {
        SampleRate = 48000;
        LiftAngle = 52.0;
        AveragingPeriod = 2;
        MaxRateDataPoints = 250;
        RateErrorYScale = 10.0;
        RlsWindowInit = 100;
    }
}

/// <summary>
/// Port of WatchMetrics (WatchMetrics.h/.cpp): computes rate error, beat error and
/// amplitude from detected A/C tick events and formats the result/marker text.
/// </summary>
public sealed class WatchMetrics
{
    // Local constants from the anonymous namespace in WatchMetrics.cpp.
    private const int Tic = 0;
    private const int Toc = 1;

    private readonly WatchMetricsConfig _config;

    private ulong _ticTocBeatNumber = 0;
    private readonly List<double> _xTic = new();
    private readonly List<double> _xToc = new();
    private readonly List<double> _yTic = new();
    private readonly List<double> _yToc = new();
    private int _xTicIndex = 0;
    private int _xTocIndex = 0;
    private bool _haveStartTime = false;
    private bool _haveZeroOffset = false;
    private double _startTime = 0.0;
    private double _zeroOffsetValue = 0.0;
    private readonly RollingLeastSquares _rlsTicRate;
    private readonly RollingLeastSquares _rlsTocRate;
    private double _rlsRate = 0.0;
    private bool _rlsRateValid = false;
    private int _bph = 0;
    private bool _bphValid = false;
    private int _watchHertz = 0;

    private readonly double[] _beatErrorTimes = { 0.0, 0.0, 0.0 };
    private int _beatErrorIdx = 0;
    private double _beatErrorMs = 0.0;
    private readonly RollingAverage _rollBeatError;

    private double _lastAEvent = 0.0;
    private bool _haveAEvent = false;
    private readonly RollingAverage _rollAmplitude;
    private double _amplitudeTic = 0.0;
    private double _amplitudeToc = 0.0;
    private bool _amplitudeTicValid = false;

    public WatchMetrics(WatchMetricsConfig config)
    {
        _config = config;
        _rlsTicRate = new RollingLeastSquares(config.RlsWindowInit);
        _rlsTocRate = new RollingLeastSquares(config.RlsWindowInit);
        _rollBeatError = new RollingAverage(10);
        _rollAmplitude = new RollingAverage(10);
        // mX*/mY*.reserve(max_rate_data_points) in the original is a capacity hint only.
    }

    public void Reset()
    {
        _haveAEvent = false;
        _amplitudeTicValid = false;
        _rollAmplitude.Reset();

        _beatErrorIdx = 0;
        _rollBeatError.Reset();

        _bphValid = false;
        _xTicIndex = 0;
        _xTocIndex = 0;
        _startTime = 0.0;
        _haveStartTime = false;
        _haveZeroOffset = false;
        _zeroOffsetValue = 0.0;
        _xTic.Clear();
        _xToc.Clear();
        _yTic.Clear();
        _yToc.Clear();
        _rlsTicRate.Reset();
        _rlsTocRate.Reset();
        _rlsRateValid = false;
    }

    public WatchMetricsUpdate HandleAEvent(double eventSample, bool haveValidBph, double bph)
    {
        var update = new WatchMetricsUpdate();
        ComputeRateError(eventSample, haveValidBph, bph, update);
        ComputeBeatError(eventSample);
        _haveAEvent = true;
        _lastAEvent = eventSample;
        return update;
    }

    public WatchMetricsUpdate HandleCEvent(double eventSample, bool haveValidBph, double bph)
    {
        var update = new WatchMetricsUpdate();
        double beatTimeSeconds = _haveAEvent
            ? (eventSample - _lastAEvent) / (double)_config.SampleRate
            : 0.0;

        update.SetCMarkerText(FormatCMarkerText(beatTimeSeconds, haveValidBph, bph));

        ComputeAmplitude(eventSample, bph);
        update.SetResults(FormatResults());
        return update;
    }

    public int CurrentBeatPhase()
    {
        return (int)((_ticTocBeatNumber - 1) & 1);
    }

    public static double Amplitude(double liftAngle, double t1, double bph)
    {
        return liftAngle / Math.Sin((2.0 * Math.PI * t1) / (7200.0 / bph));
    }

    private void ComputeRateError(double eventSample, bool haveValidBph, double bph, WatchMetricsUpdate update)
    {
        if ((!haveValidBph) && (_haveStartTime))
        {
            _haveStartTime = false;
            _bphValid = false;
        }
        else if ((haveValidBph) && (!_haveStartTime))
        {
            _haveStartTime = true;
            _ticTocBeatNumber = 0;
            _bphValid = true;
            _bph = (int)bph;
            _startTime = eventSample / (double)_config.SampleRate;
            _haveZeroOffset = false;
            _zeroOffsetValue = 0.0;
            _rlsRateValid = false;
            _watchHertz = _bph / 3600;
            _rlsTicRate.Resize(_config.AveragingPeriod * _watchHertz);
            _rlsTocRate.Resize(_config.AveragingPeriod * _watchHertz);
            _rlsTicRate.Reset();
            _rlsTocRate.Reset();

            _rollBeatError.Reset();
            _rollAmplitude.Reset();
        }

        if ((haveValidBph) && (_haveStartTime))
        {
            double instTimingError;
            double instTimingErrorMs;
            double expectedTimeTarget;
            double timeMeasured;

            timeMeasured = eventSample / (double)_config.SampleRate;
            // Original: 3600.0f / bph. bph is double, so the float literal 3600.0f is
            // promoted to double (== 3600.0 exactly) and the division is done in double;
            // there is no narrowing of the result.
            expectedTimeTarget = 3600.0 / bph;

            _ticTocBeatNumber++;

            int ticOrToc = CurrentBeatPhase();

            instTimingError = (_startTime + _ticTocBeatNumber * expectedTimeTarget) - timeMeasured;
            instTimingErrorMs = instTimingError * 1000.00;
            if (!_haveZeroOffset)
            {
                _haveZeroOffset = true;
                _zeroOffsetValue = -instTimingErrorMs;
            }
            instTimingErrorMs = instTimingErrorMs + _zeroOffsetValue;

            double wrappedRateError = WrapIntoRange(
                instTimingErrorMs,
                -_config.RateErrorYScale,
                _config.RateErrorYScale);

            if (ticOrToc == Tic)
            {
                _rlsTicRate.AddPoint(timeMeasured, instTimingError);
                AddOrOverwrite(_xTic, _yTic, wrappedRateError, _config.MaxRateDataPoints, ref _xTicIndex);
                update.SetTicRate(_xTic, _yTic);
            }
            else
            {
                _rlsTocRate.AddPoint(timeMeasured, instTimingError);
                AddOrOverwrite(_xToc, _yToc, wrappedRateError, _config.MaxRateDataPoints, ref _xTocIndex);
                update.SetTocRate(_xToc, _yToc);
            }

            if (ticOrToc == Toc)
            {
                double slopeTic;
                double rlsTic;
                double slopeToc;
                double rlsToc;

                if ((_rlsTicRate.GetRate(out slopeTic)) &&
                    (_rlsTocRate.GetRate(out slopeToc)))
                {
                    rlsTic = slopeTic * 86400.00;
                    rlsToc = slopeToc * 86400.00;
                    _rlsRate = (rlsTic + rlsToc) / 2.0;
                    _rlsRateValid = true;
                }
                else
                {
                    _rlsRateValid = false;
                }
            }
        }
    }

    private void ComputeBeatError(double eventSample)
    {
        _beatErrorTimes[_beatErrorIdx] = eventSample;
        _beatErrorIdx++;
        if (_beatErrorIdx == 3)
        {
            double t1 = (_beatErrorTimes[1] - _beatErrorTimes[0]) / (double)_config.SampleRate;
            double t2 = (_beatErrorTimes[2] - _beatErrorTimes[1]) / (double)_config.SampleRate;

            _beatErrorMs = Math.Abs(((t1 - t2) / 2.0) * 1000.0);
            _rollBeatError.Add(_beatErrorMs);
            _beatErrorTimes[0] = _beatErrorTimes[2];
            _beatErrorIdx = 1;
        }
    }

    private void ComputeAmplitude(double eventSample, double bph)
    {
        if ((_haveAEvent) && (_bphValid))
        {
            int ticOrToc = CurrentBeatPhase();
            double time = (eventSample - _lastAEvent) / (double)_config.SampleRate;
            double tempAmp = Amplitude(_config.LiftAngle, time, bph);
            if (tempAmp < 360.00)
            {
                if (ticOrToc == Tic)
                {
                    _amplitudeTicValid = true;
                    _amplitudeTic = tempAmp;
                }
                else
                {
                    _amplitudeToc = tempAmp;
                    if (_amplitudeTicValid)
                    {
                        double averageAmplitudeTicToc = (_amplitudeTic + _amplitudeToc) / 2.0;
                        _rollAmplitude.Add(averageAmplitudeTicToc);
                        _amplitudeTicValid = false;
                    }
                }
            }
            else if (ticOrToc == Tic)
            {
                _amplitudeTicValid = false;
            }
        }
    }

    private string FormatCMarkerText(double beatTimeSeconds, bool haveValidBph, double bph)
    {
        if ((haveValidBph) && (_bphValid) && (_haveAEvent))
        {
            int amp = QRound(Amplitude(_config.LiftAngle, beatTimeSeconds, bph));
            if (amp < 360)
            {
                // " %1 ms\n%2%3" : ms (f,1), amp (int), degree sign (U+00B0)
                return " " + FormatFixed(beatTimeSeconds * 1000.0, 1) + " ms\n"
                       + amp.ToString(CultureInfo.InvariantCulture) + "°";
            }
        }

        // " %1 ms " : ms (f,1)
        return " " + FormatFixed(beatTimeSeconds * 1000.0, 1) + " ms ";
    }

    // The title-bar readout wraps each live numeric value in these markers so the UI can color
    // only the numbers (not the labels or dash placeholders). Braces never occur in the values
    // or labels themselves, and the UI strips them before display.
    public const char ValueSpanStart = '{';
    public const char ValueSpanEnd = '}';

    private static string Mark(string value) => ValueSpanStart + value + ValueSpanEnd;

    private string FormatResults()
    {
        string beatsPerHour;
        string rateError;
        string beatError;
        string amplitudeText;

        if (_bphValid)
        {
            // QString("%1").arg(mBph, 5, 10, QChar(' ')) : int, width 5, space-padded, right-aligned
            beatsPerHour = Mark(ArgInt(_bph, 5));
        }
        else
        {
            beatsPerHour = "-----";
        }

        if (_rlsRateValid)
        {
            // Width 5: forced sign + "%.1f" covers "-99.9".."+99.9"; rate stays within ±99.9.
            rateError = Mark(PrintfPlusFloat(_rlsRate, 5, 1));
        }
        else
        {
            rateError = "-----";
        }

        if (_rollBeatError.CurrentSize() > 0)
        {
            // Width 4 covers the full range ("-9.9".."99.9"); beat error never reaches ±10.
            beatError = Mark(ArgFixed(_rollBeatError.GetAverage(), 4, 1));
        }
        else
        {
            beatError = "----";
        }

        if (_rollAmplitude.CurrentSize() > 0)
        {
            // Width 3; the degree sign is appended as a constant suffix below so the field
            // width stays identical whether or not a value is present.
            amplitudeText = Mark(ArgLong(QRound64(_rollAmplitude.GetAverage()), 3));
        }
        else
        {
            amplitudeText = "---";
        }

        return "RATE " + rateError + " s/d | AMPLITUDE " + amplitudeText + "°" +
               " | BEAT ERROR " + beatError + " ms | BEAT " + beatsPerHour + " bph";
    }

    private double WrapIntoRange(double number, double lowerBound, double upperBound)
    {
        double rangeWidth = upperBound - lowerBound;
        number = number - lowerBound;
        // Qt's qFloor(qreal) returns int (int(std::floor(v))); preserve that truncation.
        number = number - (int)Math.Floor(number / rangeWidth) * rangeWidth;
        return number + lowerBound;
    }

    private void AddOrOverwrite(List<double> xvec, List<double> yvec, double value, int maxSize, ref int index)
    {
        if (yvec.Count < maxSize)
        {
            yvec.Add(value);
            xvec.Add(index);
            index = (index + 1) % maxSize;
        }
        else
        {
            yvec[index] = value;
            index = (index + 1) % maxSize;
        }
    }

    // --- Formatting / rounding helpers (Qt-compatible) ---

    /// <summary>qRound(double): round half away from zero, returning int (matches Qt).</summary>
    private static int QRound(double d)
    {
        return d >= 0.0 ? (int)(d + 0.5) : (int)(d - 0.5);
    }

    /// <summary>qRound64(double): round half away from zero, returning long (matches Qt).</summary>
    private static long QRound64(double d)
    {
        return d >= 0.0 ? (long)(d + 0.5) : (long)(d - 0.5);
    }

    /// <summary>Fixed-point format with given decimals, like QString::arg(v,0,'f',prec).</summary>
    private static string FormatFixed(double v, int decimals)
    {
        return v.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>QString::arg(int, fieldWidth, base 10, ' '): right-aligned, space-padded.</summary>
    private static string ArgInt(int value, int fieldWidth)
    {
        string s = value.ToString(CultureInfo.InvariantCulture);
        return s.Length < fieldWidth ? s.PadLeft(fieldWidth, ' ') : s;
    }

    /// <summary>QString::arg(qint64, fieldWidth, base 10, ' '): right-aligned, space-padded.</summary>
    private static string ArgLong(long value, int fieldWidth)
    {
        string s = value.ToString(CultureInfo.InvariantCulture);
        return s.Length < fieldWidth ? s.PadLeft(fieldWidth, ' ') : s;
    }

    /// <summary>QString::arg(double, fieldWidth, 'f', prec): fixed prec, right-aligned, space-padded.</summary>
    private static string ArgFixed(double value, int fieldWidth, int decimals)
    {
        string s = FormatFixed(value, decimals);
        return s.Length < fieldWidth ? s.PadLeft(fieldWidth, ' ') : s;
    }

    /// <summary>C printf "%+W.Pf": forced sign, fixed P decimals, right-aligned in width W (space pad).</summary>
    private static string PrintfPlusFloat(double value, int width, int decimals)
    {
        // Format magnitude with fixed decimals, then prepend the explicit sign.
        string sign = value < 0.0 ? "-" : "+";
        string body = Math.Abs(value).ToString(
            "F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        string s = sign + body;
        return s.Length < width ? s.PadLeft(width, ' ') : s;
    }
}
