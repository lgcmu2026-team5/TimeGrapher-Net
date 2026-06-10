using TimeGrapher.App.Audio;
using TimeGrapher.Core.AudioIo;
using TimeGrapher.Core.Sim;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class AnalysisBenchmarkRunnerTests
{
    [Fact]
    public void RunCompletesShortSyntheticBenchmark()
    {
        int exitCode = AnalysisBenchmarkRunner.Run(
            new[] { "--analysis-benchmark", "--bph", "43200", "--rate", "48000", "--duration-ms", "3000" });

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void RunCompletesWavBenchmark()
    {
        string path = Path.Combine(Path.GetTempPath(), "43200BPH_benchmark_" + Guid.NewGuid().ToString("N") + ".wav");

        try
        {
            WriteSyntheticWav(path, bph: 43200, sampleRate: 48000, durationMs: 3000);

            int exitCode = AnalysisBenchmarkRunner.Run(
                new[] { "--analysis-benchmark", "--wav", path });

            Assert.Equal(0, exitCode);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void WriteSyntheticWav(string path, int bph, int sampleRate, int durationMs)
    {
        WatchSynthStreamConfig config = WatchSynthStreamConfig.Realistic();
        config.SampleRateHz = (uint)sampleRate;
        config.Bph = bph;
        config.PcmPeakAmplitude = 0.35;

        var synth = new WatchSynthStream(config);
        using var writer = new WavStreamWriter();
        Assert.True(writer.Open(path, sampleRate, channels: 1));

        var block = new float[4096];
        int remaining = (int)Math.Ceiling(sampleRate * (durationMs / 1000.0));
        while (remaining > 0)
        {
            int count = Math.Min(block.Length, remaining);
            Span<float> span = block.AsSpan(0, count);
            synth.Generate(span);
            Assert.True(writer.Write(span));
            remaining -= count;
        }

        Assert.True(writer.Close());
    }
}
