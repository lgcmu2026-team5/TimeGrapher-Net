using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Detection.Scoring;
using TimeGrapher.Core.Sim;
using Xunit;

namespace TimeGrapher.Core.Tests;

/// <summary>
/// Effectiveness and safety of the shipped PLL phase-match gate on the
/// impulse-storm stream (28800 BPH, 0.25 pcm, 0.02 noise, 3 impulses/s at
/// 0.6 amplitude), plus the ORACLE upper bound that proves the seam leaves
/// measurable headroom for a future shape-based (TinyML) gate. Measured
/// values this pins after post-lock onset gating: base precision 0.964 /
/// pll 1.000 / oracle 1.000; recall 0.964 / 0.955 / 0.964; identical
/// first-sync block in all arms.
/// </summary>
public sealed class PllMatchGateTests
{
    /// <summary>Truth-fed gate: the effect upper bound for an ideal classifier.</summary>
    private sealed class OracleGate : IBeatEventGate
    {
        private readonly double[] _truthTimes;
        public OracleGate(double[] truthTimes) => _truthTimes = truthTimes;
        public string Name => "oracle";
        public double WindowPreMs => 0.0;
        public double WindowPostMs => 0.0;
        public bool Accept(ReadOnlySpan<float> envelopeWindow, int eventOffsetInWindow,
                           double sampleRate, in BeatCandidate candidate)
        {
            if (candidate.Event.Type != TgEventType.A)
            {
                return true; // Cs ride on the pair veto
            }
            double t = candidate.Event.TimeSeconds;
            foreach (double truth in _truthTimes)
            {
                if (Math.Abs(truth - t) <= 0.005)
                {
                    return true;
                }
            }
            return false;
        }
        public void Reset() { }
    }

    private sealed record ArmResult(
        DetectionScorer.Score Score, int FirstSyncBlock, ulong Vetoed, TgSyncStatus FinalSync);

    private static WatchSynthStreamConfig StormStream()
    {
        WatchSynthStreamConfig cfg = WatchSynthStreamConfig.Clean();
        cfg.SampleRateHz = 48000;
        cfg.Bph = 28800;
        cfg.PcmPeakAmplitude = 0.25;
        cfg.NoisePeakAmplitude = 0.02;
        cfg.ImpulseNoiseRatePerSecond = 3.0;
        cfg.ImpulseNoisePeakAmplitude = 0.6;
        return cfg;
    }

    private static double[] CollectTruth()
    {
        var synth = new WatchSynthStream(StormStream());
        var truth = new List<double>();
        var block = new float[4096];
        var eventBuf = new WatchSynthStreamEvent[64];
        long remaining = 48000L * 16;
        while (remaining > 0)
        {
            int slice = (int)Math.Min(block.Length, remaining);
            WatchSynthStreamFillResult fill = synth.FillF32(block.AsSpan(0, slice), eventBuf);
            for (int i = 0; i < fill.EventsWritten; i++)
            {
                truth.Add(eventBuf[i].TimeS);
            }
            remaining -= slice;
        }
        return truth.ToArray();
    }

    private static ArmResult RunArm(IBeatEventGate? gate, double[] truth)
    {
        var synth = new WatchSynthStream(StormStream());
        var engine = new DetectorMetricsEngine(new DetectorMetricsEngineConfig(
            SampleRate: 48000,
            LiftAngle: 52.0,
            AveragingPeriod: 2,
            UseCOnset: false,
            AutoBph: true,
            ManualBph: 0,
            HpfCutoffHz: 0.0,
            EventGate: gate != null ? new BeatEventGateConfig(gate) : null));

        var detected = new List<double>();
        int firstSyncBlock = -1;
        int blockIndex = 0;
        var block = new float[4096];
        DetectorMetricsBlockUpdate update = default!;
        long remaining = 48000L * 16;
        while (remaining > 0)
        {
            int slice = (int)Math.Min(block.Length, remaining);
            synth.Generate(block.AsSpan(0, slice));
            update = engine.Process(block.AsSpan(0, slice));
            if (firstSyncBlock < 0 && update.Result.SyncStatus == TgSyncStatus.Synced)
            {
                firstSyncBlock = blockIndex;
            }
            foreach (DetectedEventUpdate ev in update.MetricsEvents)
            {
                if (ev.Event.Type == TgEventType.A)
                {
                    detected.Add(ev.EventSample / 48000.0);
                }
            }
            remaining -= slice;
            blockIndex++;
        }
        update = engine.Flush();
        foreach (DetectedEventUpdate ev in update.MetricsEvents)
        {
            if (ev.Event.Type == TgEventType.A)
            {
                detected.Add(ev.EventSample / 48000.0);
            }
        }

        DetectionScorer.Score score = DetectionScorer.Match(
            truth, detected.ToArray(), toleranceS: 0.005, evalStartS: 2.0);
        return new ArmResult(score, firstSyncBlock, update.Result.VetoedEvents, update.Result.SyncStatus);
    }

    [Fact]
    public void PllGate_RaisesPrecisionWithoutTouchingLockAcquisition()
    {
        double[] truth = CollectTruth();
        ArmResult baseArm = RunArm(null, truth);
        ArmResult pllArm = RunArm(new PllMatchGate(), truth);

        // Precision: spurious impulse events are vetoed before metrics.
        Assert.True(pllArm.Score.Precision >= baseArm.Score.Precision + 0.03,
            $"pll precision {pllArm.Score.Precision:F3} vs base {baseArm.Score.Precision:F3}");
        Assert.True(pllArm.Score.Precision >= 0.95, $"pll precision {pllArm.Score.Precision:F3}");

        // Recall cost stays marginal.
        Assert.True(pllArm.Score.Recall >= baseArm.Score.Recall - 0.05,
            $"pll recall {pllArm.Score.Recall:F3} vs base {baseArm.Score.Recall:F3}");

        // Structural safety: lock acquisition is bit-identical (the gate
        // sits after BPH/PLL), and the veto counter is observable.
        Assert.Equal(baseArm.FirstSyncBlock, pllArm.FirstSyncBlock);
        Assert.Equal(TgSyncStatus.Synced, pllArm.FinalSync);
        Assert.Equal(0UL, baseArm.Vetoed);
        Assert.True(pllArm.Vetoed > 0);
    }

    [Fact]
    public void OracleGate_ShowsTheSeamHeadroomForAFutureClassifier()
    {
        double[] truth = CollectTruth();
        ArmResult baseArm = RunArm(null, truth);
        ArmResult oracleArm = RunArm(new OracleGate(truth), truth);

        Assert.True(oracleArm.Score.Precision >= 0.99,
            $"oracle precision {oracleArm.Score.Precision:F3}");
        Assert.True(oracleArm.Score.Recall >= baseArm.Score.Recall - 0.01,
            $"oracle recall {oracleArm.Score.Recall:F3} vs base {baseArm.Score.Recall:F3}");
        Assert.Equal(baseArm.FirstSyncBlock, oracleArm.FirstSyncBlock);
    }
}
