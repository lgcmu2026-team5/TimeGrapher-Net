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

    private static GraphSeriesFrame Snapshot(SweepFrameProjector projector, bool force = false)
    {
        var frame = new AnalysisFrame();
        projector.AppendSnapshot(frame, force);
        return Assert.Single(frame.ScopeSeries, s => s.Id == AnalysisGraphSeries.SweepTrace);
    }

    private static int BinOf(double positionSamples, double windowSamples) =>
        (int)(positionSamples / windowSamples * SweepFrameProjector.SweepBinBudget);

    [Fact]
    public void SyncDropoutKeepsTheLatchedWindowAndPattern()
    {
        // 18000-bph watch: 0.2 s beat = 9600 samples, 2x window = 19200 samples.
        // A dropout forces MeasuredPeriodS to 0 / NotSynced; without the latched
        // period the projector would fall back to the nominal 28800-bph 0.125 s,
        // re-tune to a 0.25 s window and clear the accumulated pattern - twice
        // per dropout (once on loss, once on re-lock).
        const double period18000 = 0.2;
        const int windowSamples = 19200;
        var projector = new SweepFrameProjector(SampleRate);

        projector.Project(Update(
            ImpulseBlock(windowSamples, 1.0f, 0), 0,
            measuredPeriodS: period18000, detectedBph: 18000));
        GraphSeriesFrame locked = Snapshot(projector);
        double windowMsBefore = locked.X[^1];
        Assert.Contains(locked.Y, v => v > 0.5);

        // Dropout: empty block, period 0, not synced.
        projector.Project(Update(
            Array.Empty<float>(), windowSamples,
            measuredPeriodS: 0.0, sync: TgSyncStatus.NotSynced, detectedBph: 0));

        GraphSeriesFrame afterDropout = Snapshot(projector);
        Assert.Equal(windowMsBefore, afterDropout.X[^1], 9); // window unchanged
        Assert.Contains(afterDropout.Y, v => v > 0.5);       // pattern preserved

        // Re-lock at the same bph: still no clear. The probe sample sits mid-window
        // so its per-pass overwrite touches an empty bin, not the stored pattern.
        projector.Project(Update(
            ImpulseBlock(1, 0.0f), (ulong)windowSamples + 9600,
            measuredPeriodS: period18000, detectedBph: 18000));
        Assert.Contains(Snapshot(projector).Y, v => v > 0.5);
    }

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
    public void SnapshotIsSharedWithinThePublishFloorAndHidesInBetweenData()
    {
        // 2x window = 12000 samples; the publish floor is 50 ms = 2400 samples.
        var projector = new SweepFrameProjector(SampleRate);
        const int windowSamples = 12000;
        projector.Project(Update(ImpulseBlock(windowSamples, 1.0f, 0), 0UL));
        GraphSeriesFrame first = Snapshot(projector);

        // 1000 more samples (~21 ms) with a fresh impulse: under the floor the
        // same immutable instance re-attaches and the new bin stays hidden.
        projector.Project(Update(ImpulseBlock(1000, 1.0f, 600), (ulong)windowSamples));
        GraphSeriesFrame second = Snapshot(projector);

        Assert.Same(first, second);
        Assert.Equal(0.0, second.Y[BinOf(600, windowSamples)]);
    }

    [Fact]
    public void SnapshotRebuildsWithFreshDataOnceThePublishFloorElapses()
    {
        var projector = new SweepFrameProjector(SampleRate);
        const int windowSamples = 12000;
        projector.Project(Update(ImpulseBlock(windowSamples, 1.0f, 0), 0UL));
        GraphSeriesFrame first = Snapshot(projector);

        // 3000 samples (62.5 ms) cross the 50 ms floor: a new instance
        // publishes and carries the impulse projected meanwhile.
        projector.Project(Update(ImpulseBlock(3000, 1.0f, 600), (ulong)windowSamples));
        GraphSeriesFrame second = Snapshot(projector);

        Assert.NotSame(first, second);
        Assert.Equal(1.0, second.Y[BinOf(600, windowSamples)]);
    }

    [Fact]
    public void SweepXListIsSharedAcrossRebuildsUntilARetune()
    {
        var projector = new SweepFrameProjector(SampleRate);
        const int windowSamples = 12000;
        projector.Project(Update(ImpulseBlock(windowSamples, 1.0f, 0), 0UL));
        GraphSeriesFrame first = Snapshot(projector);

        // The bin centers are bit-identical between retunes, so a rebuild
        // re-attaches the cached X list instead of rebuilding it.
        projector.Project(Update(ImpulseBlock(3000, 1.0f, 600), (ulong)windowSamples));
        GraphSeriesFrame rebuilt = Snapshot(projector);
        Assert.NotSame(first, rebuilt);
        Assert.Same(first.X, rebuilt.X);

        // A retune (different sweep multiple) invalidates the cached axis.
        projector.SetSweepMultiple(4);
        projector.Project(Update(Array.Empty<float>(), 15000UL));
        GraphSeriesFrame retuned = Snapshot(projector);
        Assert.NotSame(rebuilt.X, retuned.X);
    }

    [Fact]
    public void PublishIntervalScaleStretchesTheFloor()
    {
        var projector = new SweepFrameProjector(SampleRate);
        const int windowSamples = 12000;
        projector.SetPublishIntervalScale(4); // deadline-ladder knob: 50 ms -> 200 ms
        projector.Project(Update(ImpulseBlock(windowSamples, 1.0f, 0), 0UL));
        GraphSeriesFrame first = Snapshot(projector);

        // 3000 samples would clear the unscaled floor but not the stretched one.
        projector.Project(Update(ImpulseBlock(3000, 1.0f, 600), (ulong)windowSamples));
        Assert.Same(first, Snapshot(projector));

        // 9600 samples (200 ms) since the publish clear the stretched floor.
        projector.Project(Update(ImpulseBlock(6600, 1.0f, 0), 15000UL));
        Assert.NotSame(first, Snapshot(projector));
    }

    [Fact]
    public void ForcedSnapshotRebuildsRegardlessOfTheFloor()
    {
        // The drain/flush path force-publishes so the final kept frame - the
        // one a paused review keeps re-rendering - carries the freshest bins.
        var projector = new SweepFrameProjector(SampleRate);
        const int windowSamples = 12000;
        projector.Project(Update(ImpulseBlock(windowSamples, 1.0f, 0), 0UL));
        GraphSeriesFrame first = Snapshot(projector);

        projector.Project(Update(ImpulseBlock(1000, 1.0f, 600), (ulong)windowSamples));
        GraphSeriesFrame forced = Snapshot(projector, force: true);

        Assert.NotSame(first, forced);
        Assert.Equal(1.0, forced.Y[BinOf(600, windowSamples)]);
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
