using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Sim;
using Xunit;

namespace TimeGrapher.Core.Tests;

public sealed class SyntheticDetectorTests
{
    public static TheoryData<int, int, double, double> GeneratedCases => new()
    {
        { 18000, 48000, 0.40, 0.00 },
        { 21600, 48000, 0.18, 0.02 },
        { 28800, 96000, 0.40, 0.00 },
        { 36000, 48000, 0.35, 0.01 },
        { 43200, 192000, 0.35, 0.00 },
    };

    [Theory]
    [MemberData(nameof(GeneratedCases))]
    public void SyntheticStreamPipelineDetectsConfiguredBphAndMetrics(int expectedBph, int sampleRate, double pcmPeak, double noisePeak)
    {
        WatchSynthStreamConfig synthConfig = WatchSynthStreamConfig.Clean();
        synthConfig.SampleRateHz = (uint)sampleRate;
        synthConfig.Bph = expectedBph;
        synthConfig.NoisePeakAmplitude = noisePeak;
        synthConfig.PcmPeakAmplitude = pcmPeak;

        var synth = new WatchSynthStream(synthConfig);
        var engine = new DetectorMetricsEngine(new DetectorMetricsEngineConfig(
            SampleRate: sampleRate,
            LiftAngle: 52.0,
            AveragingPeriod: 2,
            UseCOnset: false,
            AutoBph: true,
            ManualBph: 0,
            HpfCutoffHz: 0.0));

        float[] block = new float[4096];
        DetectorMetricsBlockUpdate update = engine.Flush();
        string resultsText = "";

        int remaining = sampleRate * 10;
        while (remaining > 0)
        {
            int slice = Math.Min(block.Length, remaining);
            Span<float> span = block.AsSpan(0, slice);
            synth.Generate(span);
            update = engine.Process(span);
            foreach (DetectedEventUpdate eventUpdate in update.Events)
            {
                if (eventUpdate.MetricsUpdate.ResultsUpdated)
                {
                    resultsText = eventUpdate.MetricsUpdate.ResultsText;
                }
            }
            remaining -= slice;
        }

        update = engine.Flush();
        foreach (DetectedEventUpdate eventUpdate in update.Events)
        {
            if (eventUpdate.MetricsUpdate.ResultsUpdated)
            {
                resultsText = eventUpdate.MetricsUpdate.ResultsText;
            }
        }

        Assert.Equal(TimeGrapher.Core.Detection.TgSyncStatus.Synced, update.Result.SyncStatus);
        Assert.Equal(expectedBph, update.Result.DetectedBph);
        Assert.False(string.IsNullOrWhiteSpace(resultsText));
    }
}
