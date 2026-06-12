using TimeGrapher.Core.Sim;
using Xunit;

namespace TimeGrapher.Core.Tests;

/// <summary>
/// Impulse-noise knob coverage: rate 0 must leave the generated stream
/// bit-identical (the knob is purely additive), rate &gt; 0 must be
/// deterministic per seed, roughly Poisson in count, and clamped to [-1, 1].
/// </summary>
public sealed class WatchSynthImpulseNoiseTests
{
    private static float[] Generate(WatchSynthStreamConfig cfg, int seconds)
    {
        var synth = new WatchSynthStream(cfg);
        var samples = new float[(int)cfg.SampleRateHz * seconds];
        var block = samples.AsSpan();
        while (block.Length > 0)
        {
            int slice = Math.Min(4096, block.Length);
            synth.Generate(block.Slice(0, slice));
            block = block.Slice(slice);
        }
        return samples;
    }

    [Fact]
    public void RateZero_StreamIsBitIdenticalToUnconfiguredBaseline()
    {
        WatchSynthStreamConfig baseline = WatchSynthStreamConfig.Realistic();

        WatchSynthStreamConfig knobsSet = WatchSynthStreamConfig.Realistic();
        knobsSet.ImpulseNoiseRatePerSecond = 0.0;
        knobsSet.ImpulseNoisePeakAmplitude = 0.8;
        knobsSet.ImpulseNoiseFreqHz = 6000.0;
        knobsSet.ImpulseNoiseDecayMs = 5.0;

        float[] a = Generate(baseline, 3);
        float[] b = Generate(knobsSet, 3);
        Assert.Equal(a, b); // exact float equality: zero draws, zero added terms
    }

    [Fact]
    public void SameSeed_ImpulseStreamIsDeterministic()
    {
        WatchSynthStreamConfig cfg = WatchSynthStreamConfig.Clean();
        cfg.ImpulseNoiseRatePerSecond = 2.0;
        cfg.ImpulseNoisePeakAmplitude = 0.6;

        float[] a = Generate(cfg, 4);
        float[] b = Generate(cfg, 4);
        Assert.Equal(a, b);
    }

    [Fact]
    public void TickAndJitterSequencesAreUnaffectedByEnablingImpulses()
    {
        // Compare ground-truth event side channels: the impulse RNG stream is
        // separate, so beat times must match to the sample even with impulses on.
        WatchSynthStreamConfig quiet = WatchSynthStreamConfig.Realistic();
        WatchSynthStreamConfig impulsive = WatchSynthStreamConfig.Realistic();
        impulsive.ImpulseNoiseRatePerSecond = 3.0;
        impulsive.ImpulseNoisePeakAmplitude = 0.9;

        var quietEvents = CollectEvents(quiet, 5);
        var impulsiveEvents = CollectEvents(impulsive, 5);

        Assert.Equal(quietEvents.Count, impulsiveEvents.Count);
        for (int i = 0; i < quietEvents.Count; i++)
        {
            Assert.Equal(quietEvents[i].SampleIndex, impulsiveEvents[i].SampleIndex);
            Assert.Equal(quietEvents[i].Kind, impulsiveEvents[i].Kind);
        }
    }

    [Fact]
    public void ImpulseCount_IsRoughlyPoisson()
    {
        // Impulses only: no ticks, no white noise. Count threshold crossings
        // with a refractory window; 30 s at 3/s gives a mean of 90.
        WatchSynthStreamConfig cfg = WatchSynthStreamConfig.Clean();
        cfg.PcmPeakAmplitude = 0.0;
        cfg.NoisePeakAmplitude = 0.0;
        cfg.ImpulseNoiseRatePerSecond = 3.0;
        cfg.ImpulseNoisePeakAmplitude = 0.5;

        float[] samples = Generate(cfg, 30);

        int count = 0;
        int refractory = 0;
        int refractorySamples = (int)(0.020 * cfg.SampleRateHz); // > 8 * decay tau
        for (int i = 0; i < samples.Length; i++)
        {
            if (refractory > 0)
            {
                refractory--;
                continue;
            }
            if (Math.Abs(samples[i]) > 0.1)
            {
                count++;
                refractory = refractorySamples;
            }
        }

        Assert.InRange(count, 55, 135); // ~±40% around the Poisson mean of 90
    }

    [Fact]
    public void FullScaleImpulses_StayClamped()
    {
        WatchSynthStreamConfig cfg = WatchSynthStreamConfig.Clean();
        cfg.ImpulseNoiseRatePerSecond = 10.0;
        cfg.ImpulseNoisePeakAmplitude = 1.0;

        float[] samples = Generate(cfg, 3);
        Assert.All(samples, s => Assert.InRange(s, -1.0f, 1.0f));
    }

    [Theory]
    [InlineData(-1.0, 0.5, 4500.0, 2.0)]   // negative rate
    [InlineData(60.0, 0.5, 4500.0, 2.0)]   // rate above cap
    [InlineData(2.0, 1.5, 4500.0, 2.0)]    // amplitude out of range
    [InlineData(2.0, 0.5, 50.0, 2.0)]      // frequency below floor
    [InlineData(2.0, 0.5, 4500.0, 0.0)]    // decay not positive
    public void InvalidImpulseConfig_IsRejected(double rate, double amp, double freq, double decayMs)
    {
        WatchSynthStreamConfig cfg = WatchSynthStreamConfig.Clean();
        cfg.ImpulseNoiseRatePerSecond = rate;
        cfg.ImpulseNoisePeakAmplitude = amp;
        cfg.ImpulseNoiseFreqHz = freq;
        cfg.ImpulseNoiseDecayMs = decayMs;

        Assert.False(WatchSynthStream.ValidateConfig(cfg, out string err));
        Assert.Contains("impulse_noise", err);
    }

    private static List<WatchSynthStreamEvent> CollectEvents(WatchSynthStreamConfig cfg, int seconds)
    {
        var synth = new WatchSynthStream(cfg);
        var events = new List<WatchSynthStreamEvent>();
        var block = new float[4096];
        var eventBuf = new WatchSynthStreamEvent[64];
        int remaining = (int)cfg.SampleRateHz * seconds;
        while (remaining > 0)
        {
            int slice = Math.Min(block.Length, remaining);
            WatchSynthStreamFillResult r = synth.FillF32(block.AsSpan(0, slice), eventBuf);
            for (int i = 0; i < r.EventsWritten; i++)
            {
                events.Add(eventBuf[i]);
            }
            remaining -= slice;
        }
        return events;
    }
}
