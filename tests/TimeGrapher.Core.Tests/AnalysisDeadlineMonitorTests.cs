using TimeGrapher.Core.Analysis;
using Xunit;

namespace TimeGrapher.Core.Tests;

public sealed class AnalysisDeadlineMonitorTests
{
    private const int SampleRate = 48000;

    // 2 beat periods at the 125 ms default is 250 ms = 12000 samples; stay above.
    private const ulong BreachLag = 24000;   // 500 ms
    private const ulong RecoveredLag = 1000; // ~21 ms, under 0.5 beat periods

    [Fact]
    public void SustainedBreachEscalatesOneLevelPerStreak()
    {
        var monitor = new AnalysisDeadlineMonitor(escalateAfterPasses: 4, deescalateAfterPasses: 4);

        for (int i = 0; i < 3; i++)
        {
            Assert.False(monitor.Observe(BreachLag, SampleRate, beatPeriodSeconds: 0.0));
        }
        Assert.Equal(0, monitor.Level);

        Assert.True(monitor.Observe(BreachLag, SampleRate, 0.0));
        Assert.Equal(1, monitor.Level);
    }

    [Fact]
    public void SingleGoodPassResetsTheBreachStreak()
    {
        var monitor = new AnalysisDeadlineMonitor(escalateAfterPasses: 4, deescalateAfterPasses: 4);

        for (int i = 0; i < 3; i++)
        {
            monitor.Observe(BreachLag, SampleRate, 0.0);
        }
        monitor.Observe(RecoveredLag, SampleRate, 0.0); // streak broken

        for (int i = 0; i < 3; i++)
        {
            Assert.False(monitor.Observe(BreachLag, SampleRate, 0.0));
        }
        Assert.Equal(0, monitor.Level);
    }

    [Fact]
    public void SustainedRecoveryStepsBackDownOneLevelAtATime()
    {
        var monitor = new AnalysisDeadlineMonitor(escalateAfterPasses: 1, deescalateAfterPasses: 3);

        monitor.Observe(BreachLag, SampleRate, 0.0);
        monitor.Observe(BreachLag, SampleRate, 0.0);
        Assert.Equal(2, monitor.Level);

        for (int i = 0; i < 2; i++)
        {
            Assert.False(monitor.Observe(RecoveredLag, SampleRate, 0.0));
        }
        Assert.True(monitor.Observe(RecoveredLag, SampleRate, 0.0));
        Assert.Equal(1, monitor.Level);
    }

    [Fact]
    public void LevelNeverExceedsMax()
    {
        var monitor = new AnalysisDeadlineMonitor(escalateAfterPasses: 1, deescalateAfterPasses: 1);

        for (int i = 0; i < 10; i++)
        {
            monitor.Observe(BreachLag, SampleRate, 0.0);
        }

        Assert.Equal(AnalysisDeadlineMonitor.MaxLevel, monitor.Level);
    }

    [Fact]
    public void BeatPeriodFromPllRaisesTheThreshold()
    {
        // At an 18000-BPH beat (200 ms), a 300 ms lag is only 1.5 beat periods —
        // inside the 2-beat budget that would be breached at the 125 ms default.
        var monitor = new AnalysisDeadlineMonitor(escalateAfterPasses: 1, deescalateAfterPasses: 1);

        ulong lag300Ms = (ulong)(SampleRate * 0.3);
        Assert.False(monitor.Observe(lag300Ms, SampleRate, beatPeriodSeconds: 0.2));
        Assert.Equal(0, monitor.Level);

        Assert.True(monitor.Observe(lag300Ms, SampleRate, beatPeriodSeconds: 0.0));
        Assert.Equal(1, monitor.Level);
    }

    [Fact]
    public void ResetClearsLevelAndStreaks()
    {
        var monitor = new AnalysisDeadlineMonitor(escalateAfterPasses: 1, deescalateAfterPasses: 1);
        monitor.Observe(BreachLag, SampleRate, 0.0);
        Assert.Equal(1, monitor.Level);

        monitor.Reset();

        Assert.Equal(0, monitor.Level);
    }
}
