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

    // Derived timing measures (project plan "Expected Enhancements"): DiffTicTac,
    // DiffPeriod (short fixed window) and AvgPeriod (since start / last segment
    // restart on a re-lock at a different BPH).
    private const int DiffPeriodWindowSeconds = 4;
    private readonly RollingAverage _rollPeriodDelta = new(0);
    private double _avgPeriodDeltaSumMs = 0.0;
    private long _avgPeriodDeltaCount = 0;
    private bool _skipNextPeriodDelta = false;
    private double _diffTicTacMs = 0.0;
    private bool _diffTicTacValid = false;
    private double _signedBeatErrorMs = 0.0;
    private bool _signedBeatErrorValid = false;
    private ulong _missedBeats = 0;

    // Per-event numeric stash consumed by the BeatTimingSample/AmplitudeSample
    // emission in HandleAEvent/HandleCEvent. _lastRateErrorMs is always fresh when
    // the emission gate (_haveStartTime && _bphValid) holds, because that is the
    // same condition under which ComputeRateError's synced branch just ran.
    private double _lastRateErrorMs = 0.0;
    private bool _lastAmplitudeInstValid = false;
    private double _lastAmplitudeInstDeg = 0.0;
    private bool _lastAmplitudePairUpdated = false;
    private double _lastAmplitudePairDeg = 0.0;

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

        ResetDerivedMeasures();
        _missedBeats = 0;
        _lastAmplitudeInstValid = false;
        _lastAmplitudePairUpdated = false;
    }

    private void ResetDerivedMeasures()
    {
        _rollPeriodDelta.Reset();
        _avgPeriodDeltaSumMs = 0.0;
        _avgPeriodDeltaCount = 0;
        _skipNextPeriodDelta = true;
        _diffTicTacValid = false;
        _signedBeatErrorValid = false;
    }

    public WatchMetricsUpdate HandleAEvent(double eventSample, bool haveValidBph, double bph)
    {
        var update = new WatchMetricsUpdate();
        ComputeRateError(eventSample, haveValidBph, bph, update);
        ComputeBeatError(eventSample);

        if (_haveStartTime && _bphValid)
        {
            update.SetBeatTimingSample(new BeatTimingSample(
                _ticTocBeatNumber,
                eventSample / (double)_config.SampleRate,
                CurrentBeatPhase() == Tic,
                _lastRateErrorMs,
                _rlsRateValid,
                _rlsRate,
                _signedBeatErrorValid,
                _signedBeatErrorMs,
                _bph));

            update.SetDerivedMeasures(new DerivedTimingMeasures(
                _diffTicTacValid,
                _diffTicTacMs,
                _rollPeriodDelta.CurrentSize() > 0,
                _rollPeriodDelta.GetAverage(),
                _avgPeriodDeltaCount > 0,
                _avgPeriodDeltaCount > 0 ? _avgPeriodDeltaSumMs / _avgPeriodDeltaCount : 0.0));
        }

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

        if (_lastAmplitudeInstValid || _lastAmplitudePairUpdated)
        {
            update.SetAmplitudeSample(new AmplitudeSample(
                eventSample / (double)_config.SampleRate,
                _lastAmplitudeInstValid,
                _lastAmplitudeInstDeg,
                _lastAmplitudePairUpdated,
                _lastAmplitudePairDeg));
        }

        return update;
    }

    public int CurrentBeatPhase()
    {
        return (int)((_ticTocBeatNumber - 1) & 1);
    }

    /// <summary>
    /// Session-cumulative count of beats the detector skipped over (A-to-A intervals
    /// spanning more than one nominal beat). QA evidence for the low-missed-beats
    /// requirement; reset only with <see cref="Reset"/>, not on sync re-acquisition.
    /// </summary>
    public ulong MissedBeats => _missedBeats;

    public static double Amplitude(double liftAngle, double t1, double bph)
    {
        return liftAngle / Math.Sin((2.0 * Math.PI * t1) / (7200.0 / bph));
    }

    private void ComputeRateError(double eventSample, bool haveValidBph, double bph, WatchMetricsUpdate update)
    {
        // The shipped pipeline never delivers haveValidBph=false after the first
        // lock: DetectorMetricsEngine suppresses pre-sync events and TgDetector
        // drops the whole batch in which sync is lost. A re-lock at a different
        // BPH therefore arrives with the segment still anchored to the old
        // watch, so treat the BPH change itself as the segment restart.
        if (haveValidBph && _haveStartTime && (int)bph != _bph)
        {
            _haveStartTime = false;
        }

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

            // Derived measures restart with the sync segment: a stale _lastAEvent
            // from before a sync loss must not contribute a bogus period delta.
            _rollPeriodDelta.Resize(DiffPeriodWindowSeconds * _watchHertz);
            ResetDerivedMeasures();
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

            // Classify the A-to-A interval (re-anchoring the beat counter across
            // any detection gap) before the parity read and the expected-time
            // computation below consume the counter.
            bool gapDetected = AccumulatePeriodDelta(eventSample, expectedTimeTarget);

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
            _lastRateErrorMs = instTimingErrorMs;

            if (gapDetected)
            {
                // A regression window mixing pre- and post-gap points sees the
                // re-anchored schedule's sub-beat residual as a step and emits
                // a transient slope spike (thousands of s/d for one missed
                // beat) that the cumulative rate statistics would record
                // permanently. Restart both estimators from the post-gap
                // segment instead - the same recovery a fresh sync lock
                // performs; the reading returns after two beats per phase. The
                // gap-ending event seeds the new window below: its own instant
                // is measured on the post-gap schedule.
                _rlsTicRate.Reset();
                _rlsTocRate.Reset();
                _rlsRateValid = false;
            }

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

    /// <summary>
    /// Accumulates measured-vs-expected beat-duration deltas (consecutive A events)
    /// for DiffPeriod / AvgPeriod. Intervals off by more than half a beat span a
    /// detection gap rather than a single beat and would poison the averages, so
    /// they are excluded; gaps additionally re-anchor the tic/toc beat counter to
    /// the physical schedule, so this must run before the counter is advanced for
    /// the current event. Returns true when the interval spans a detection gap.
    /// </summary>
    private bool AccumulatePeriodDelta(double eventSample, double expectedTimeTarget)
    {
        bool gapDetected = false;
        if (_haveAEvent && !_skipNextPeriodDelta)
        {
            double measuredPeriodS = (eventSample - _lastAEvent) / (double)_config.SampleRate;
            double deltaMs = (measuredPeriodS - expectedTimeTarget) * 1000.0;
            if (Math.Abs(deltaMs) < expectedTimeTarget * 500.0)
            {
                _rollPeriodDelta.Add(deltaMs);
                _avgPeriodDeltaSumMs += deltaMs;
                _avgPeriodDeltaCount++;
            }
            else if (deltaMs > 0.0)
            {
                // An over-long interval means the detector skipped beats; the
                // interval covers ~N nominal beats, of which N-1 went undetected.
                int beatsSpanned = QRound(measuredPeriodS / expectedTimeTarget);
                ulong skippedBeats = (ulong)Math.Max(1, beatsSpanned - 1);
                _missedBeats += skippedBeats;
                // Advance the beat counter past the undetected beats: its parity
                // is the tic/toc label and its product with the nominal period is
                // the expected-time schedule, so leaving it behind sign-inverts
                // every signed beat error / DiffTicTac after an odd-length gap
                // (and mispairs the amplitude average), while every gap shifts
                // the rate-error baseline by a full beat per missed beat.
                _ticTocBeatNumber += skippedBeats;
                gapDetected = true;
            }
        }

        _skipNextPeriodDelta = false;
        return gapDetected;
    }

    /// <summary>
    /// True when the A-to-A interval is within half a nominal beat of the locked
    /// beat period, i.e. it represents exactly one beat (no detection gap, no
    /// spurious extra event). Callable only while _bphValid.
    /// </summary>
    private bool IsSingleBeatInterval(double intervalS)
    {
        double expectedS = 3600.0 / _bph;
        return Math.Abs(intervalS - expectedS) < expectedS * 0.5;
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

            if (_haveStartTime && IsSingleBeatInterval(t1) && IsSingleBeatInterval(t2))
            {
                // The window start's phase equals the current event's phase (the
                // window advances two beats per completion), so a tic-start window
                // makes t1 the tick duration and t2 the tock duration; normalize
                // DiffTicTac to (tick - tock) regardless of the start phase.
                double diffMs = (t1 - t2) * 1000.0;
                _diffTicTacMs = CurrentBeatPhase() == Tic ? diffMs : -diffMs;
                _diffTicTacValid = true;
                _signedBeatErrorMs = _diffTicTacMs / 2.0;
                _signedBeatErrorValid = true;
            }
            else if (_haveStartTime)
            {
                // A window interval spanning a detection gap (or a spurious extra
                // event) is not a tick/tock duration; a single missed beat would
                // otherwise inject a half-beat-sized fake error (~62.5 ms at
                // 28800 bph) into the cumulative history and position statistics.
                // Same half-beat criterion as AccumulatePeriodDelta; the signed
                // values stay invalid until the next clean window (two beats).
                _diffTicTacValid = false;
                _signedBeatErrorValid = false;
            }

            _beatErrorTimes[0] = _beatErrorTimes[2];
            _beatErrorIdx = 1;
        }
    }

    private void ComputeAmplitude(double eventSample, double bph)
    {
        _lastAmplitudeInstValid = false;
        _lastAmplitudePairUpdated = false;

        if ((_haveAEvent) && (_bphValid))
        {
            int ticOrToc = CurrentBeatPhase();
            double time = (eventSample - _lastAEvent) / (double)_config.SampleRate;
            double tempAmp = Amplitude(_config.LiftAngle, time, bph);
            if (tempAmp < 360.00)
            {
                _lastAmplitudeInstValid = true;
                _lastAmplitudeInstDeg = tempAmp;

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
                        _lastAmplitudePairUpdated = true;
                        _lastAmplitudePairDeg = averageAmplitudeTicToc;
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

    private string FormatResults() => BuildResults(
        _bphValid, _bph,
        _rlsRateValid, _rlsRate,
        _rollBeatError.CurrentSize() > 0, _rollBeatError.GetAverage(),
        _rollAmplitude.CurrentSize() > 0, _rollAmplitude.GetAverage());

    /// <summary>
    /// Pure formatter for the title-bar readout. Each field is fixed-width so the line never
    /// shifts as values change; present numeric values are wrapped in value-span markers (so
    /// the UI can accent only the numbers) while dash placeholders are left unmarked. Widths:
    /// rate 5 ("-99.9"), amplitude 3 + constant degree sign, beat error 4 ("-9.9"), bph 5.
    /// </summary>
    internal static string BuildResults(
        bool bphValid, int bph,
        bool rateValid, double rate,
        bool beatErrorValid, double beatError,
        bool amplitudeValid, double amplitude)
    {
        string beatsPerHour = bphValid ? Mark(ArgInt(bph, 5)) : "-----";
        string rateError = rateValid ? Mark(PrintfPlusFloat(rate, 5, 1)) : "-----";
        string beatErrorText = beatErrorValid ? Mark(ArgFixed(beatError, 4, 1)) : "----";
        string amplitudeText = amplitudeValid ? Mark(ArgLong(QRound64(amplitude), 3)) : "---";

        return "RATE " + rateError + " s/d | AMPLITUDE " + amplitudeText + "°" +
               " | BEAT ERROR " + beatErrorText + " ms | BEAT " + beatsPerHour + " bph";
    }

    // MainWindow::WrapInToRange: fmod into the range, adding the range size
    // when the remainder is negative (C# '%' on doubles == C fmod).
    private double WrapIntoRange(double number, double lowerBound, double upperBound)
    {
        double rangeWidth = upperBound - lowerBound;
        double wrapped = (number - lowerBound) % rangeWidth;
        if (wrapped < 0)
        {
            wrapped += rangeWidth;
        }

        return wrapped + lowerBound;
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
