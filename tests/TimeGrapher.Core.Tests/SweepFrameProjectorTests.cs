using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.Core.Tests;

/// <summary>
/// Scope Mode sweep folding: on-period impulses land in the same bins on every
/// sweep pass (visually stable pattern), off-period impulses move the peak bin
/// (drift), and a sweep-multiple change re-tunes the window and clears the bins.
/// </summary>
public sealed class SweepFrameProjectorTests
{
    private const int SampleRate = 48000;
    private const double BeatPeriodS = 0.125; // 6000 samples at 48 kHz

    private static DetectorMetricsBlockUpdate Update(
        float[] pcm,
        ulong startSample,
        double measuredPeriodS = BeatPeriodS,
        TgSyncStatus sync = TgSyncStatus.Synced,
        int detectedBph = 28800)
    {
        var result = new DetectorResultSnapshot(
            sync, detectedBph, measuredPeriodS, Array.Empty<TgEvent>(),
            pcm, pcm.Length, startSample,
            false, false, false, 0f, 0f, 0f, 0f);
        return new DetectorMetricsBlockUpdate(result, Array.Empty<DetectedEventUpdate>());
    }

    /// <summary>Block of zeros with single-sample impulses at the given block-relative offsets.</summary>
    private static float[] ImpulseBlock(int length, float peak, params int[] impulseOffsets)
    {
        var pcm = new float[length];
        foreach (int offset in impulseOffsets)
        {
            pcm[offset] = peak;
        }

        return pcm;
    }

    private static GraphSeriesFrame Snapshot(SweepFrameProjector projector)
    {
        var frame = new AnalysisFrame();
        projector.AppendSnapshot(frame);
        return Assert.Single(frame.ScopeSeries, s => s.Id == AnalysisGraphSeries.SweepTrace);
    }

    private static int BinOf(double positionSamples, double windowSamples) =>
        (int)(positionSamples / windowSamples * SweepFrameProjector.SweepBinBudget);

    [Fact]
    public void OnPeriodImpulsesStackIntoTheSameBinsAcrossPasses()
    {
        // 2x window = 12000 samples; impulses every beat (6000 samples) over four
        // passes always land at phases 0 and 6000 -> bins 0 and budget/2.
        var projector = new SweepFrameProjector(SampleRate);
        const int windowSamples = 12000;
        for (int pass = 0; pass < 4; pass++)
        {
            projector.Project(Update(
                ImpulseBlock(windowSamples, 1.0f, 0, 6000),
                (ulong)(pass * windowSamples)));
        }

        GraphSeriesFrame series = Snapshot(projector);

        Assert.True(series.Replace);
        Assert.Equal(SweepFrameProjector.SweepBinBudget, series.X.Count);
        Assert.Equal(1.0, series.Y[BinOf(0, windowSamples)]);
        Assert.Equal(1.0, series.Y[BinOf(6000, windowSamples)]);

        // x is ms within the window: bin centers spanning ~0..250 ms.
        double binWidthMs = 250.0 / SweepFrameProjector.SweepBinBudget;
        Assert.Equal(binWidthMs / 2.0, series.X[0], 6);
        Assert.Equal(250.0 - binWidthMs / 2.0, series.X[^1], 6);
    }

    [Fact]
    public void OffPeriodImpulsesMoveThePeakBinAcrossPasses()
    {
        // Slow watch: impulses every 6050 samples against a latched 12000-sample
        // window. Pass 0 paints bins at phases 0 and 6050; pass 1 overwrites the
        // window, erasing bin 0 and painting the drifted phase 100 instead.
        var projector = new SweepFrameProjector(SampleRate);
        const int windowSamples = 12000;
        projector.Project(Update(ImpulseBlock(windowSamples, 1.0f, 0, 6050), 0UL));

        GraphSeriesFrame firstPass = Snapshot(projector);
        Assert.Equal(1.0, firstPass.Y[BinOf(0, windowSamples)]);
        Assert.Equal(1.0, firstPass.Y[BinOf(6050, windowSamples)]);

        // Next pass: impulses at absolute 12100 and 18150 -> phases 100 and 6150.
        projector.Project(Update(
            ImpulseBlock(windowSamples, 1.0f, 100, 6150),
            (ulong)windowSamples));

        GraphSeriesFrame secondPass = Snapshot(projector);
        Assert.Equal(0.0, secondPass.Y[BinOf(0, windowSamples)]);
        Assert.Equal(1.0, secondPass.Y[BinOf(100, windowSamples)]);
        Assert.Equal(1.0, secondPass.Y[BinOf(6150, windowSamples)]);
    }

    [Fact]
    public void SameBinKeepsItsEnvelopeMaximumWithinOnePass()
    {
        // 12000 samples / 4000 bins = 3 samples per bin: 0.3 then 0.2 in bin 0
        // keeps 0.3 (envelope), it is not overwritten by the newer sample.
        var projector = new SweepFrameProjector(SampleRate);
        var pcm = new float[12000];
        pcm[0] = 0.3f;
        pcm[1] = 0.2f;
        projector.Project(Update(pcm, 0UL));

        GraphSeriesFrame series = Snapshot(projector);
        Assert.Equal(0.3, series.Y[0], 6);
    }

    [Fact]
    public void ChangingSweepMultipleRetunesTheWindowAndClearsTheBins()
    {
        var projector = new SweepFrameProjector(SampleRate);
        projector.Project(Update(ImpulseBlock(12000, 1.0f, 0, 6000), 0UL));
        Assert.Equal(1.0, Snapshot(projector).Y[0]);

        projector.SetSweepMultiple(4);
        projector.Project(Update(Array.Empty<float>(), 12000UL));

        GraphSeriesFrame series = Snapshot(projector);
        Assert.All(series.Y, y => Assert.Equal(0.0, y));

        // 4x window = 500 ms: the bin centers now span ~0..500 ms.
        double binWidthMs = 500.0 / SweepFrameProjector.SweepBinBudget;
        Assert.Equal(500.0 - binWidthMs / 2.0, series.X[^1], 6);
    }

    [Fact]
    public void SmallMeasuredPeriodJitterKeepsTheLatchedWindow()
    {
        // PLL jitter (well under the re-tune threshold) must not clear the bins,
        // otherwise no pattern could ever accumulate.
        var projector = new SweepFrameProjector(SampleRate);
        projector.Project(Update(ImpulseBlock(12000, 1.0f, 0), 0UL, measuredPeriodS: 0.125));
        projector.Project(Update(Array.Empty<float>(), 12000UL, measuredPeriodS: 0.12504));

        Assert.Equal(1.0, Snapshot(projector).Y[0]);
    }

    [Fact]
    public void WindowFallsBackToDetectedBphThenDefaultPeriod()
    {
        // No measured period but synced at 18000 BPH: window = 2 * 3600/18000 = 400 ms.
        var bphProjector = new SweepFrameProjector(SampleRate);
        bphProjector.Project(Update(
            new float[1], 0UL, measuredPeriodS: 0.0, detectedBph: 18000));
        double binWidthMs = 400.0 / SweepFrameProjector.SweepBinBudget;
        Assert.Equal(400.0 - binWidthMs / 2.0, Snapshot(bphProjector).X[^1], 6);

        // Nothing known at all: nominal 28800-BPH default, window = 2 * 125 ms.
        var defaultProjector = new SweepFrameProjector(SampleRate);
        defaultProjector.Project(Update(
            new float[1], 0UL, measuredPeriodS: 0.0, sync: TgSyncStatus.NotSynced, detectedBph: 0));
        binWidthMs = 250.0 / SweepFrameProjector.SweepBinBudget;
        Assert.Equal(250.0 - binWidthMs / 2.0, Snapshot(defaultProjector).X[^1], 6);
    }
}
