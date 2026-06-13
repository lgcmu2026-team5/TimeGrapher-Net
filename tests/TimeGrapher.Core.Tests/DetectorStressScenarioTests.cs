using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Sim;
using Xunit;

namespace TimeGrapher.Core.Tests;

/// <summary>
/// In-process mirrors of the verifier's two strongest adverse rows
/// (harness-test alignment convention), so `dotnet test` alone proves the
/// default detector recovery without running the Verify executable:
///  - impulse-dos: full-scale impulses once a second over a quiet watch.
///    The detector rides it out without reset storms.
///  - quiet-step: a 0.13x gain step after 6 s. The detector decays the
///    reference and re-acquires.
///  - leadin-quiet: two seconds of silence before a healthy watch does not
///    let bootstrap regime trips wipe BPH acquisition history.
/// </summary>
public sealed class DetectorStressScenarioTests
{
    private sealed record StressResult(
        TgSyncStatus FinalSync,
        int DetectedBph,
        int Resets,
        DetectionScorer.Score Score);

    private static StressResult Run(
        double pcmPeak, double noisePeak, int bph, int seconds,
        double impulseRate = 0.0, double impulseAmp = 0.0,
        double gainStepAtS = 0.0, double gainStepFactor = 1.0,
        int silenceLeadInSamples = 0,
        double evalStartS = 2.0)
    {
        WatchSynthStreamConfig cfg = WatchSynthStreamConfig.Clean();
        cfg.SampleRateHz = 48000;
        cfg.Bph = bph;
        cfg.PcmPeakAmplitude = pcmPeak;
        cfg.NoisePeakAmplitude = noisePeak;
        if (impulseRate > 0.0)
        {
            cfg.ImpulseNoiseRatePerSecond = impulseRate;
            cfg.ImpulseNoisePeakAmplitude = impulseAmp;
        }

        var synth = new WatchSynthStream(cfg);
        var engine = new DetectorMetricsEngine(new DetectorMetricsEngineConfig(
            SampleRate: 48000,
            LiftAngle: 52.0,
            AveragingPeriod: 2,
            UseCOnset: false,
            AutoBph: true,
            ManualBph: 0,
            HpfCutoffHz: 0.0));

        int resets = 0;
        var block = new float[4096];
        var eventBuf = new WatchSynthStreamEvent[64];
        double leadInS = (double)silenceLeadInSamples / 48000;
        var truthTimes = new List<double>();
        var detectedATimes = new List<double>();
        DetectorMetricsBlockUpdate update = default!;

        while (silenceLeadInSamples > 0)
        {
            int slice = Math.Min(block.Length, silenceLeadInSamples);
            Array.Clear(block, 0, slice);
            update = engine.Process(block.AsSpan(0, slice));
            Collect(update, detectedATimes, ref resets);
            silenceLeadInSamples -= slice;
        }

        long total = 48000L * seconds;
        long done = 0;
        long stepAt = gainStepAtS > 0.0 ? (long)(gainStepAtS * 48000) : long.MaxValue;
        while (done < total)
        {
            int slice = (int)Math.Min(block.Length, total - done);
            Span<float> span = block.AsSpan(0, slice);
            WatchSynthStreamFillResult fill = synth.FillF32(span, eventBuf);
            for (int i = 0; i < fill.EventsWritten; i++)
            {
                truthTimes.Add(leadInS + eventBuf[i].TimeS);
            }
            for (int i = 0; i < slice; i++)
            {
                if (done + i >= stepAt)
                {
                    span[i] *= (float)gainStepFactor;
                }
            }
            update = engine.Process(span);
            Collect(update, detectedATimes, ref resets);
            done += slice;
        }
        update = engine.Flush();
        Collect(update, detectedATimes, ref resets);
        DetectionScorer.Score score = DetectionScorer.Match(
            truthTimes.ToArray(), detectedATimes.ToArray(),
            toleranceS: 0.005, evalStartS: leadInS + evalStartS);
        return new StressResult(update.Result.SyncStatus, update.Result.DetectedBph, resets, score);
    }

    private static void Collect(DetectorMetricsBlockUpdate update, List<double> detectedATimes, ref int resets)
    {
        if (update.Result.DetectorResetEvent)
        {
            resets++;
        }
        foreach (DetectedEventUpdate ev in update.MetricsEvents)
        {
            if (ev.Event.Type == TgEventType.A)
            {
                detectedATimes.Add(ev.EventSample / 48000);
            }
        }
    }

    [Fact]
    public void ImpulseDos_DefaultDetectorHoldsLock()
    {
        StressResult result = Run(pcmPeak: 0.03, noisePeak: 0.004,
            bph: 21600, seconds: 16, impulseRate: 1.0, impulseAmp: 0.95);

        Assert.True(result.Resets <= 1, $"resets {result.Resets}");
        Assert.Equal(TgSyncStatus.Synced, result.FinalSync);
        Assert.Equal(21600, result.DetectedBph);
    }

    [Fact]
    public void QuietStep_DefaultDetectorReacquires()
    {
        StressResult result = Run(pcmPeak: 0.60, noisePeak: 0.01,
            bph: 21600, seconds: 16, gainStepAtS: 6.0, gainStepFactor: 0.13,
            evalStartS: 10.0);

        Assert.Equal(TgSyncStatus.Synced, result.FinalSync);
        Assert.Equal(21600, result.DetectedBph);
        Assert.True(result.Score.Recall >= 0.95, $"recall {result.Score.Recall:F3}");
        Assert.True(result.Score.Precision >= 0.95, $"precision {result.Score.Precision:F3}");
    }

    [Fact]
    public void SilentLeadIn_DefaultDetectorAcquiresLock()
    {
        StressResult result = Run(pcmPeak: 0.30, noisePeak: 0.01,
            bph: 21600, seconds: 12, silenceLeadInSamples: 96000);

        Assert.Equal(0, result.Resets);
        Assert.Equal(TgSyncStatus.Synced, result.FinalSync);
        Assert.Equal(21600, result.DetectedBph);
    }
}
