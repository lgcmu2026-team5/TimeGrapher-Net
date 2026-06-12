namespace TimeGrapher.Core.Analysis;

/// <summary>
/// Watches per-pass analysis backlog against the watch's beat period and drives a
/// graceful-degradation ladder when the pipeline sustains deadline pressure.
///
/// Budget rationale: one beat at 28800 BPH is 125 ms (3600 s / 28800). If per-beat
/// work (capture -> DSP/detection -> metrics -> projection) costs more than one
/// beat period, backlog grows; <see cref="Shared.AnalysisFrame.AnalysisLagSamples"/> is the
/// integral symptom of that, so it is the breach signal — measured in beat periods
/// derived from the nominal locked period (125 ms default until BPH lock).
/// ProcessingElapsedMs stays telemetry-only: a single pass is bounded by the 4096-
/// sample chunk and says nothing about the budget unless normalized; lag already is.
///
/// Hysteresis: escalate one level after EscalateAfterPasses consecutive passes with
/// more than BreachBeats beat periods of backlog; step back down only after
/// DeescalateAfterPasses consecutive passes comfortably inside RecoverBeats.
/// SEI tactics: bound execution times / manage work requests (graceful degradation).
/// </summary>
public sealed class AnalysisDeadlineMonitor
{
    public const int MaxLevel = 3;

    private const double DefaultBeatPeriodS = 3600.0 / 28800.0; // 125 ms until BPH lock
    private const double BreachBeats = 2.0;
    private const double RecoverBeats = 0.5;

    private readonly int _escalateAfterPasses;
    private readonly int _deescalateAfterPasses;
    private int _breachStreak;
    private int _recoverStreak;

    public AnalysisDeadlineMonitor(int escalateAfterPasses = 16, int deescalateAfterPasses = 48)
    {
        _escalateAfterPasses = Math.Max(1, escalateAfterPasses);
        _deescalateAfterPasses = Math.Max(1, deescalateAfterPasses);
    }

    /// <summary>Current degradation level (0 = full quality).</summary>
    public int Level { get; private set; }

    /// <summary>
    /// Feed one analysis pass result. Returns true when the level changed and the
    /// caller must re-apply the degradation ladder.
    /// </summary>
    public bool Observe(ulong lagSamples, int sampleRate, double beatPeriodSeconds)
    {
        double period = beatPeriodSeconds > 0.0 ? beatPeriodSeconds : DefaultBeatPeriodS;
        double lagSeconds = sampleRate > 0 ? lagSamples / (double)sampleRate : 0.0;
        double lagBeats = lagSeconds / period;

        if (lagBeats > BreachBeats)
        {
            _recoverStreak = 0;
            if (Level < MaxLevel && ++_breachStreak >= _escalateAfterPasses)
            {
                _breachStreak = 0;
                Level++;
                return true;
            }
            return false;
        }

        _breachStreak = 0;
        if (Level > 0 && lagBeats < RecoverBeats)
        {
            if (++_recoverStreak >= _deescalateAfterPasses)
            {
                _recoverStreak = 0;
                Level--;
                return true;
            }
        }
        else
        {
            _recoverStreak = 0;
        }

        return false;
    }

    public void Reset()
    {
        Level = 0;
        _breachStreak = 0;
        _recoverStreak = 0;
    }
}
