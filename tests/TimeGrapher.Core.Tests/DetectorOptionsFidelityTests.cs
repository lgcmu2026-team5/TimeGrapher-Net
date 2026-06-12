using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Sim;
using Xunit;

namespace TimeGrapher.Core.Tests;

/// <summary>
/// Hard fidelity constraint as a unit test: a TgDetector constructed with an
/// all-off <see cref="TgDetectorOptions"/> instance must behave
/// block-for-block identically (events, sync state, threshold diagnostics)
/// to one constructed with a null options reference, across clean, realistic,
/// noisy, and impulsive streams. The golden-master tests pin the null arm
/// against pre-change absolute values; this test pins the all-off arm to the
/// null arm.
/// </summary>
public sealed class DetectorOptionsFidelityTests
{
    public static TheoryData<string> Streams => new() { "clean", "realistic", "noisy", "impulse" };

    [Theory]
    [MemberData(nameof(Streams))]
    public void AllOffOptions_AreBitIdenticalToNullOptions(string streamKind)
    {
        WatchSynthStreamConfig synthConfig = MakeStream(streamKind);

        var synthA = new WatchSynthStream(synthConfig);
        var synthB = new WatchSynthStream(synthConfig);
        var detectorNull = new TgDetector(TgConfig.Default());
        var detectorAllOff = new TgDetector(TgConfig.Default(), new TgDetectorOptions());
        var resultNull = new TgResult();
        var resultAllOff = new TgResult();

        var blockA = new float[4096];
        var blockB = new float[4096];
        int remaining = 48000 * 8;
        while (remaining > 0)
        {
            int slice = Math.Min(blockA.Length, remaining);
            synthA.Generate(blockA.AsSpan(0, slice));
            synthB.Generate(blockB.AsSpan(0, slice));
            detectorNull.Process(blockA.AsSpan(0, slice), resultNull);
            detectorAllOff.Process(blockB.AsSpan(0, slice), resultAllOff);
            AssertBlockIdentical(resultNull, resultAllOff);
            remaining -= slice;
        }

        // End-of-stream drain must stay identical too.
        detectorNull.Flush(resultNull);
        detectorAllOff.Flush(resultAllOff);
        AssertBlockIdentical(resultNull, resultAllOff);
    }

    [Fact]
    public void EngineWithAllOffOptions_MatchesEngineWithNullOptions()
    {
        WatchSynthStreamConfig synthConfig = MakeStream("realistic");

        var synthA = new WatchSynthStream(synthConfig);
        var synthB = new WatchSynthStream(synthConfig);
        DetectorMetricsEngine NewEngine(TgDetectorOptions? options) =>
            new(new DetectorMetricsEngineConfig(
                SampleRate: 48000,
                LiftAngle: 52.0,
                AveragingPeriod: 2,
                UseCOnset: false,
                AutoBph: true,
                ManualBph: 0,
                HpfCutoffHz: 0.0,
                DetectorOptions: options));
        DetectorMetricsEngine engineNull = NewEngine(null);
        DetectorMetricsEngine engineAllOff = NewEngine(new TgDetectorOptions());

        var blockA = new float[4096];
        var blockB = new float[4096];
        int remaining = 48000 * 8;
        while (remaining > 0)
        {
            int slice = Math.Min(blockA.Length, remaining);
            synthA.Generate(blockA.AsSpan(0, slice));
            synthB.Generate(blockB.AsSpan(0, slice));
            DetectorMetricsBlockUpdate updateNull = engineNull.Process(blockA.AsSpan(0, slice));
            DetectorMetricsBlockUpdate updateAllOff = engineAllOff.Process(blockB.AsSpan(0, slice));
            AssertUpdateIdentical(updateNull, updateAllOff);
            remaining -= slice;
        }

        // End-of-stream drain (Flush routes through endOfStream) too.
        AssertUpdateIdentical(engineNull.Flush(), engineAllOff.Flush());
    }

    private static void AssertUpdateIdentical(
        DetectorMetricsBlockUpdate updateNull, DetectorMetricsBlockUpdate updateAllOff)
    {
        Assert.Equal(updateNull.Result.SyncStatus, updateAllOff.Result.SyncStatus);
        Assert.Equal(updateNull.Result.DetectedBph, updateAllOff.Result.DetectedBph);
        Assert.Equal(updateNull.Result.OnsetThreshold, updateAllOff.Result.OnsetThreshold);
        Assert.Equal(updateNull.Result.NoiseFloor, updateAllOff.Result.NoiseFloor);
        Assert.Equal(updateNull.Events.Count, updateAllOff.Events.Count);
        for (int i = 0; i < updateNull.Events.Count; i++)
        {
            Assert.Equal(updateNull.Events[i].EventSample, updateAllOff.Events[i].EventSample);
            Assert.Equal(updateNull.Events[i].Event.Type, updateAllOff.Events[i].Event.Type);
            Assert.Equal(
                updateNull.Events[i].MetricsUpdate.ResultsText,
                updateAllOff.Events[i].MetricsUpdate.ResultsText);
        }
    }

    private static WatchSynthStreamConfig MakeStream(string kind)
    {
        WatchSynthStreamConfig cfg = kind == "realistic"
            ? WatchSynthStreamConfig.Realistic()
            : WatchSynthStreamConfig.Clean();
        cfg.SampleRateHz = 48000;
        cfg.Bph = 21600;
        switch (kind)
        {
            case "clean":
                cfg.PcmPeakAmplitude = 0.40;
                cfg.NoisePeakAmplitude = 0.0;
                break;
            case "realistic":
                cfg.PcmPeakAmplitude = 0.30;
                cfg.NoisePeakAmplitude = 0.01;
                break;
            case "noisy":
                cfg.PcmPeakAmplitude = 0.25;
                cfg.NoisePeakAmplitude = 0.08;
                break;
            case "impulse":
                cfg.PcmPeakAmplitude = 0.20;
                cfg.NoisePeakAmplitude = 0.01;
                cfg.ImpulseNoiseRatePerSecond = 2.0;
                cfg.ImpulseNoisePeakAmplitude = 0.8;
                break;
        }
        return cfg;
    }

    private static void AssertBlockIdentical(TgResult expected, TgResult actual)
    {
        Assert.Equal(expected.SyncStatus, actual.SyncStatus);
        Assert.Equal(expected.DetectedBph, actual.DetectedBph);
        Assert.Equal(expected.MeasuredPeriodS, actual.MeasuredPeriodS);
        Assert.Equal(expected.SyncLostEvent, actual.SyncLostEvent);
        Assert.Equal(expected.SyncAcquiredEvent, actual.SyncAcquiredEvent);
        Assert.Equal(expected.DetectorResetEvent, actual.DetectorResetEvent);
        Assert.Equal(expected.OnsetThreshold, actual.OnsetThreshold);
        Assert.Equal(expected.MinPeakThreshold, actual.MinPeakThreshold);
        Assert.Equal(expected.NoiseFloor, actual.NoiseFloor);
        Assert.Equal(expected.ReferencePeak, actual.ReferencePeak);

        Assert.Equal(expected.Events.Count, actual.Events.Count);
        for (int i = 0; i < expected.Events.Count; i++)
        {
            TgEvent e = expected.Events[i];
            TgEvent a = actual.Events[i];
            Assert.Equal(e.Type, a.Type);
            Assert.Equal(e.SampleIndex, a.SampleIndex);
            Assert.Equal(e.SubSampleOffset, a.SubSampleOffset);
            Assert.Equal(e.TimeSeconds, a.TimeSeconds);
            Assert.Equal(e.PeakValue, a.PeakValue);
            Assert.Equal(e.IsPreSync, a.IsPreSync);
            Assert.Equal(e.OnsetSampleIndex, a.OnsetSampleIndex);
            Assert.Equal(e.OnsetSubSampleOffset, a.OnsetSubSampleOffset);
            Assert.Equal(e.OnsetTimeSeconds, a.OnsetTimeSeconds);
            Assert.Equal(e.OnsetValid, a.OnsetValid);
        }
    }
}
