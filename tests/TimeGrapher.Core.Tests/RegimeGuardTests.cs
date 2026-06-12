using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Sim;
using Xunit;

namespace TimeGrapher.Core.Tests;

/// <summary>
/// I-3 RegimeGuard mechanism tests, driving TgDetectorCore directly with the
/// same controlled envelope style as <see cref="AdaptiveFloorTests"/>:
/// fluctuating noise carpet (min tracker ~0.0008, percentile ~0.00112) plus
/// rectangular bursts. Bursts at 0.01 are comfortably accepted
/// (baseline minPeakThr ~0.0025), so the regime ring fills with real peaks.
/// </summary>
public sealed class RegimeGuardTests
{
    private const double Fs = 48000.0;
    private const int BurstWidth = 576;     // 12 ms
    private const float TickAmp = 0.01f;

    private static TgDetectorCore NewCore(bool guard)
    {
        var core = new TgDetectorCore();
        core.Init(Fs);
        if (guard)
        {
            core.RegimeGuardEnabled = true;
        }
        return core;
    }

    private static float[] BuildEnvelope(int totalSamples, IEnumerable<(int Start, float Amp)> bursts)
    {
        var env = new float[totalSamples];
        for (int i = 0; i < totalSamples; i++)
        {
            env[i] = (i % 7) < 2 ? 0.0008f : 0.00112f;
        }
        foreach ((int start, float amp) in bursts)
        {
            for (int i = start; i < start + BurstWidth && i < totalSamples; i++)
            {
                env[i] = amp;
            }
        }
        return env;
    }

    /// <summary>Processes the envelope, counting regime trips as the library would.</summary>
    private static int Run(TgDetectorCore core, float[] envelope)
    {
        var events = new TgRawEvent[512];
        int count = 0;
        int trips = 0;
        for (int offset = 0; offset < envelope.Length; offset += 4096)
        {
            int n = Math.Min(4096, envelope.Length - offset);
            core.Process(envelope.AsSpan(offset, n), n, events, ref count, events.Length);
            if (core.ConsumeRegimeReset() != 0)
            {
                trips++;
            }
        }
        return trips;
    }

    private static IEnumerable<(int Start, float Amp)> Train(double fromS, int count, float amp)
    {
        for (int k = 0; k < count; k++)
        {
            yield return ((int)((fromS + 0.25 * k) * Fs), amp);
        }
    }

    [Fact]
    public void SingleImpulse_TripsBaseline_ButNotGuard()
    {
        // 8 ordinary ticks, one 12x impulse, more ordinary ticks. The V5.6
        // instantaneous trip fires on the impulse; the guard requires a run
        // of 3 qualifying peaks, and the next ordinary tick resets the run.
        var bursts = new List<(int, float)>();
        bursts.AddRange(Train(1.0, 8, TickAmp));
        bursts.Add(((int)(3.0 * Fs), 0.12f));
        bursts.AddRange(Train(3.25, 8, TickAmp));
        float[] envelope = BuildEnvelope((int)(6.0 * Fs), bursts);

        Assert.Equal(1, Run(NewCore(guard: false), envelope)); // V5.6 pin (W-3)
        Assert.Equal(0, Run(NewCore(guard: true), envelope));
    }

    [Fact]
    public void GenuineGainStep_TripsGuardWithinThreeBeats()
    {
        // 8 ticks at 0.01, then a sustained 12x gain step. Every post-step
        // peak qualifies, so the guard trips on the third one; the 1 s
        // cooldown then suppresses the immediately following qualifiers
        // (5 loud bursts at 0.25 s spacing stay inside one cooldown window).
        var bursts = new List<(int, float)>();
        bursts.AddRange(Train(1.0, 8, TickAmp));
        bursts.AddRange(Train(3.0, 5, 0.12f));
        float[] envelope = BuildEnvelope((int)(5.0 * Fs), bursts);

        Assert.Equal(1, Run(NewCore(guard: true), envelope));
    }

    [Fact]
    public void TripRunIsStructurallyCappedByTheRegimeRingDepth()
    {
        // A sustained gain step floods the 8-entry regime ring: after 8 loud
        // peaks the ring min rises to the loud level and peaks stop
        // qualifying, so the run can never exceed 8. TripBeats = 8 still
        // trips; TripBeats = 9 would never trip - which is why the
        // TgDetector ctor clamps the option to [1, TG_REGIME_RING_N].
        var bursts = new List<(int, float)>();
        bursts.AddRange(Train(1.0, 8, TickAmp));
        bursts.AddRange(Train(3.0, 12, 0.12f));
        float[] envelope = BuildEnvelope((int)(7.0 * Fs), bursts);

        TgDetectorCore atCap = NewCore(guard: true);
        atCap.RegimeTripBeats = 8;
        Assert.Equal(1, Run(atCap, envelope));

        TgDetectorCore overCap = NewCore(guard: true);
        overCap.RegimeTripBeats = 9;
        Assert.Equal(0, Run(overCap, envelope));
    }

    [Fact]
    public void TgDetectorClampsOversizedTripBeats()
    {
        // Through the public seam an oversized knob is clamped, so a genuine
        // sustained gain change still flushes (DetectorResetEvent) instead of
        // the V5.6 reset being silently disabled.
        WatchSynthStreamConfig cfg = WatchSynthStreamConfig.Clean();
        cfg.SampleRateHz = 48000;
        cfg.Bph = 21600;
        cfg.PcmPeakAmplitude = 0.04;
        cfg.NoisePeakAmplitude = 0.0;

        var synth = new WatchSynthStream(cfg);
        var detector = new TgDetector(TgConfig.Default(),
            new TgDetectorOptions { EnableRegimeGuard = true, RegimeTripBeats = 99 });
        var result = new TgResult();

        bool sawReset = false;
        var block = new float[4096];
        long total = 48000L * 10;
        long done = 0;
        long stepAt = 48000L * 5;
        while (done < total)
        {
            int slice = (int)Math.Min(block.Length, total - done);
            Span<float> span = block.AsSpan(0, slice);
            synth.Generate(span);
            for (int i = 0; i < slice; i++)
            {
                if (done + i >= stepAt)
                {
                    span[i] *= 14f; // sustained gain-up step
                }
            }
            detector.Process(span, result);
            sawReset |= result.DetectorResetEvent;
            done += slice;
        }

        Assert.True(sawReset, "clamped guard never tripped on a sustained gain step");
    }

    [Fact]
    public void RepeatedImpulses_SeparatedByTicks_NeverTripGuard()
    {
        // The impulse-DoS pattern: a large impulse roughly once a second with
        // ordinary ticks in between. Baseline trips repeatedly (cooldown
        // permitting); the guard never accumulates a run of 3.
        var bursts = new List<(int, float)>();
        bursts.AddRange(Train(1.0, 24, TickAmp));
        for (int k = 0; k < 5; k++)
        {
            bursts.Add(((int)((3.1 + 1.1 * k) * Fs), 0.12f));
        }
        float[] envelope = BuildEnvelope((int)(9.0 * Fs), bursts);

        Assert.True(Run(NewCore(guard: false), envelope) >= 2, "baseline should reset repeatedly");
        Assert.Equal(0, Run(NewCore(guard: true), envelope));
    }
}
