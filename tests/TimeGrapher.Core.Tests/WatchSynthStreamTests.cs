using TimeGrapher.Core.Sim;
using Xunit;

namespace TimeGrapher.Core.Tests;

public sealed class WatchSynthStreamTests
{
    private static WatchSynthStreamConfig Clean(int bph = 28800, ulong seed = 12345, double noise = 0.0)
    {
        WatchSynthStreamConfig cfg = WatchSynthStreamConfig.Clean();
        cfg.SampleRateHz = 48000;
        cfg.Bph = bph;
        cfg.Seed = seed;
        cfg.PcmPeakAmplitude = 0.40;
        // Seed only changes output when a stochastic component is enabled (noise/jitter);
        // a fully clean packet is deterministic regardless of seed.
        cfg.NoisePeakAmplitude = noise;
        return cfg;
    }

    [Fact]
    public void Generate_ProducesNonSilentOutput()
    {
        var synth = new WatchSynthStream(Clean());
        var block = new float[48000 * 2]; // 2 s — long enough to contain several beats
        synth.Generate(block);

        Assert.Contains(block, sample => Math.Abs(sample) > 0.01f);
    }

    [Fact]
    public void Generate_IsDeterministicForSameSeed()
    {
        var a = new WatchSynthStream(Clean(seed: 777, noise: 0.05));
        var b = new WatchSynthStream(Clean(seed: 777, noise: 0.05));
        var bufA = new float[48000];
        var bufB = new float[48000];

        a.Generate(bufA);
        b.Generate(bufB);

        Assert.Equal(bufA, bufB);
    }

    [Fact]
    public void Generate_DiffersForDifferentSeed()
    {
        var a = new WatchSynthStream(Clean(seed: 1, noise: 0.05));
        var b = new WatchSynthStream(Clean(seed: 2, noise: 0.05));
        var bufA = new float[48000];
        var bufB = new float[48000];

        a.Generate(bufA);
        b.Generate(bufB);

        Assert.NotEqual(bufA, bufB);
    }
}
