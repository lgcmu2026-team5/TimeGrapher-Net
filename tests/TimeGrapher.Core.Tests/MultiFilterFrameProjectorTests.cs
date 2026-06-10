using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.Core.Tests;

/// <summary>
/// Multi-Filter Scope frame contract: four bounded replace series
/// (filter.f0..f3) sharing one absolute-sample-tick X base, trimmed to the
/// rolling two-second window and decimated at the producer.
/// </summary>
public sealed class MultiFilterFrameProjectorTests
{
    private const int SampleRate = 48000;

    private static readonly string[] AllIds =
    {
        AnalysisGraphSeries.FilterF0,
        AnalysisGraphSeries.FilterF1,
        AnalysisGraphSeries.FilterF2,
        AnalysisGraphSeries.FilterF3,
    };

    private static AnalysisFrame Snapshot(MultiFilterFrameProjector projector, bool force = false)
    {
        var frame = new AnalysisFrame();
        projector.AppendSnapshot(frame, force);
        return frame;
    }

    private static GraphSeriesFrame Series(AnalysisFrame frame, string id) =>
        Assert.Single(frame.ScopeSeries, s => s.Id == id);

    /// <summary>Alternating ±0.2 block: a busy signal so every filter view has data.</summary>
    private static float[] AlternatingBlock(int length)
    {
        var block = new float[length];
        for (int i = 0; i < length; i++)
        {
            block[i] = (i & 1) == 0 ? 0.2f : -0.2f;
        }

        return block;
    }

    [Fact]
    public void FreshProjectorPublishesNothing()
    {
        var projector = new MultiFilterFrameProjector(SampleRate);

        AnalysisFrame frame = Snapshot(projector);

        Assert.Empty(frame.ScopeSeries);
    }

    [Fact]
    public void PublishesFourBoundedReplaceSeriesSharingOneXBase()
    {
        var projector = new MultiFilterFrameProjector(SampleRate);
        projector.ProcessSamples(AlternatingBlock(2 * SampleRate));

        AnalysisFrame frame = Snapshot(projector);

        GraphSeriesFrame f0 = Series(frame, AnalysisGraphSeries.FilterF0);
        foreach (string id in AllIds)
        {
            GraphSeriesFrame series = Series(frame, id);
            Assert.True(series.Replace);
            Assert.True(series.X.Count > 0);
            Assert.True(series.X.Count <= MultiFilterFrameProjector.FilterPointBudget);
            Assert.Equal(series.X.Count, series.Y.Count);
            // One shared X list across the four views (same decimation grid).
            Assert.Same(f0.X, series.X);
        }
    }

    [Fact]
    public void WindowKeepsOnlyTheLastTwoSecondsOfTicks()
    {
        var projector = new MultiFilterFrameProjector(SampleRate);
        const int totalSamples = 3 * SampleRate;
        projector.ProcessSamples(AlternatingBlock(totalSamples));

        GraphSeriesFrame f0 = Series(Snapshot(projector), AnalysisGraphSeries.FilterF0);

        Assert.True(f0.X[0] >= totalSamples - 2 * SampleRate);
        Assert.True(f0.X[^1] < totalSamples);
        for (int i = 1; i < f0.X.Count; i++)
        {
            Assert.True(f0.X[i] > f0.X[i - 1]);
        }
    }

    [Fact]
    public void XTicksAreAbsoluteAndContinueAcrossSnapshots()
    {
        var projector = new MultiFilterFrameProjector(SampleRate);
        projector.ProcessSamples(AlternatingBlock(SampleRate));
        GraphSeriesFrame first = Series(Snapshot(projector), AnalysisGraphSeries.FilterF0);

        projector.ProcessSamples(AlternatingBlock(SampleRate));
        GraphSeriesFrame second = Series(Snapshot(projector), AnalysisGraphSeries.FilterF0);

        // The counter starts at the first raw sample and keeps running across
        // snapshots — it is this projector's own x base, not GraphTickEnd.
        Assert.Equal(0.0, first.X[0]);
        Assert.True(second.X[^1] > first.X[^1]);
        Assert.True(second.X[^1] >= SampleRate);
    }

    [Fact]
    public void SnapshotIsSharedWithinThePublishFloorAndHidesInBetweenTicks()
    {
        // The publish floor is 50 ms = 2400 raw samples at 48 kHz.
        var projector = new MultiFilterFrameProjector(SampleRate);
        projector.ProcessSamples(AlternatingBlock(SampleRate));
        AnalysisFrame first = Snapshot(projector);
        GraphSeriesFrame firstF0 = Series(first, AnalysisGraphSeries.FilterF0);

        // 1000 more samples (~21 ms): under the floor the same immutable
        // instances re-attach for all four views and the new ticks stay hidden.
        projector.ProcessSamples(AlternatingBlock(1000));
        AnalysisFrame second = Snapshot(projector);

        foreach (string id in AllIds)
        {
            Assert.Same(Series(first, id), Series(second, id));
        }

        Assert.True(firstF0.X[^1] < SampleRate);
    }

    [Fact]
    public void SnapshotRebuildsWithFreshTicksOnceThePublishFloorElapses()
    {
        var projector = new MultiFilterFrameProjector(SampleRate);
        projector.ProcessSamples(AlternatingBlock(SampleRate));
        GraphSeriesFrame first = Series(Snapshot(projector), AnalysisGraphSeries.FilterF0);

        // 3000 samples (62.5 ms) cross the 50 ms floor: new instances carrying
        // the ticks filtered meanwhile.
        projector.ProcessSamples(AlternatingBlock(3000));
        GraphSeriesFrame second = Series(Snapshot(projector), AnalysisGraphSeries.FilterF0);

        Assert.NotSame(first, second);
        Assert.True(second.X[^1] >= SampleRate);
    }

    [Fact]
    public void PublishIntervalScaleStretchesTheFloor()
    {
        var projector = new MultiFilterFrameProjector(SampleRate);
        projector.SetPublishIntervalScale(4); // deadline-ladder knob: 50 ms -> 200 ms
        projector.ProcessSamples(AlternatingBlock(SampleRate));
        GraphSeriesFrame first = Series(Snapshot(projector), AnalysisGraphSeries.FilterF0);

        // 3000 samples would clear the unscaled floor but not the stretched one.
        projector.ProcessSamples(AlternatingBlock(3000));
        Assert.Same(first, Series(Snapshot(projector), AnalysisGraphSeries.FilterF0));

        // 9600 samples (200 ms) since the publish clear the stretched floor.
        projector.ProcessSamples(AlternatingBlock(6600));
        Assert.NotSame(first, Series(Snapshot(projector), AnalysisGraphSeries.FilterF0));
    }

    [Fact]
    public void ForcedSnapshotRebuildsRegardlessOfTheFloor()
    {
        // The drain/flush path force-publishes so the final kept frame - the
        // one a paused review keeps re-rendering - carries the freshest window.
        var projector = new MultiFilterFrameProjector(SampleRate);
        projector.ProcessSamples(AlternatingBlock(SampleRate));
        GraphSeriesFrame first = Series(Snapshot(projector), AnalysisGraphSeries.FilterF0);

        projector.ProcessSamples(AlternatingBlock(1000));
        GraphSeriesFrame forced = Series(Snapshot(projector, force: true), AnalysisGraphSeries.FilterF0);

        Assert.NotSame(first, forced);
        Assert.True(forced.X[^1] >= SampleRate);
    }

    [Fact]
    public void ConstantInputRidesThroughTheFilterBankAsZeroViews()
    {
        var projector = new MultiFilterFrameProjector(SampleRate);
        var block = new float[SampleRate / 2];
        Array.Fill(block, 0.25f);
        projector.ProcessSamples(block);

        AnalysisFrame frame = Snapshot(projector);

        // The bank's running mean primes on the first sample, so a constant
        // input produces flat-zero F0..F3 — proves the views come from
        // ScopeFilters rather than the raw samples.
        foreach (string id in AllIds)
        {
            Assert.All(Series(frame, id).Y, y => Assert.Equal(0.0, y));
        }
    }
}
