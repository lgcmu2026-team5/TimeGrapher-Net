using TimeGrapher.App.Rendering;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

/// <summary>
/// Pure accumulator behind the Escapement Analyzer: the A→C interval window
/// over the last 32 segments (peak reference always, onset when valid), its
/// mean/sigma per reference, the smaller-sigma repeatability verdict and the
/// seen-watermark that makes re-fed snapshots idempotent.
/// </summary>
public sealed class EscapementTimingTrackerTests
{
    private static BeatSegment Segment(
        double startTimeS, double aMs = 5.0, double? cPeakMs = null, double? cOnsetMs = null) => new()
    {
        MsPerPoint = 0.25,
        StartTimeS = startTimeS,
        AOffsetMs = aMs,
        CPeakValid = cPeakMs is not null,
        CPeakOffsetMs = cPeakMs ?? 0.0,
        COnsetValid = cOnsetMs is not null,
        COnsetOffsetMs = cOnsetMs ?? 0.0,
    };

    private static BeatSegmentsSnapshot Snapshot(params BeatSegment[] segments) => new()
    {
        Version = 1,
        Segments = segments,
    };

    [Fact]
    public void Accumulate_MeasuresAToCIntervalsPerReference()
    {
        var tracker = new EscapementTimingTracker();

        tracker.Accumulate(Snapshot(
            Segment(0.000, aMs: 5.0, cPeakMs: 147.5, cOnsetMs: 146.5),
            Segment(0.250, aMs: 5.0, cPeakMs: 151.5, cOnsetMs: 148.5)));

        Assert.Equal(2, tracker.PeakCount);
        Assert.Equal(2, tracker.OnsetCount);
        Assert.Equal(144.5, tracker.PeakMeanMs!.Value, 9);  // (142.5 + 146.5) / 2
        Assert.Equal(2.0, tracker.PeakSigmaMs!.Value, 9);   // population sigma of {142.5, 146.5}
        Assert.Equal(142.5, tracker.OnsetMeanMs!.Value, 9); // (141.5 + 143.5) / 2
        Assert.Equal(1.0, tracker.OnsetSigmaMs!.Value, 9);
    }

    [Fact]
    public void Accumulate_SkipsSegmentsAlreadySeen()
    {
        var tracker = new EscapementTimingTracker();
        BeatSegment first = Segment(0.000, cPeakMs: 150.0);
        BeatSegment second = Segment(0.250, cPeakMs: 150.0);

        // Re-feeding the same snapshot, or an overlapping one (the cumulative
        // ring shares older segments), must not double-count.
        tracker.Accumulate(Snapshot(first));
        tracker.Accumulate(Snapshot(first));
        tracker.Accumulate(Snapshot(first, second));

        Assert.Equal(2, tracker.PeakCount);
    }

    [Fact]
    public void Accumulate_StoresNothingForSegmentsWithoutAValidCPeak()
    {
        var tracker = new EscapementTimingTracker();

        tracker.Accumulate(Snapshot(
            Segment(0.000, cPeakMs: null),
            Segment(0.250, cPeakMs: 150.0)));

        Assert.Equal(1, tracker.PeakCount);
        Assert.Equal(0, tracker.OnsetCount);

        // The C-less segment still advanced the watermark: feeding it again
        // changes nothing.
        tracker.Accumulate(Snapshot(Segment(0.000, cPeakMs: 999.0)));
        Assert.Equal(1, tracker.PeakCount);
        Assert.Equal(150.0 - 5.0, tracker.PeakMeanMs!.Value, 9);
    }

    [Fact]
    public void Accumulate_CountsOnsetOnlyWhenValid()
    {
        var tracker = new EscapementTimingTracker();

        tracker.Accumulate(Snapshot(
            Segment(0.000, cPeakMs: 150.0, cOnsetMs: 148.0),
            Segment(0.250, cPeakMs: 150.0, cOnsetMs: null)));

        Assert.Equal(2, tracker.PeakCount);
        Assert.Equal(1, tracker.OnsetCount);
        Assert.Equal(143.0, tracker.OnsetMeanMs!.Value, 9);
        Assert.Equal(0.0, tracker.OnsetSigmaMs!.Value, 9);
    }

    [Fact]
    public void Window_KeepsOnlyTheLast32Segments()
    {
        var tracker = new EscapementTimingTracker();

        // 8 outliers first, then 32 identical intervals: the outliers must be
        // evicted, leaving a zero-sigma window of the newest 32.
        for (int i = 0; i < 8; i++)
        {
            tracker.Accumulate(Snapshot(Segment(i * 0.250, cPeakMs: 305.0)));
        }

        for (int i = 8; i < 40; i++)
        {
            tracker.Accumulate(Snapshot(Segment(i * 0.250, cPeakMs: 155.0)));
        }

        Assert.Equal(EscapementTimingTracker.WindowSegments, tracker.PeakCount);
        Assert.Equal(150.0, tracker.PeakMeanMs!.Value, 9);
        Assert.Equal(0.0, tracker.PeakSigmaMs!.Value, 9);
    }

    [Fact]
    public void Verdict_RequiresMinimumSamplesOnBothReferences()
    {
        var tracker = new EscapementTimingTracker();

        // 8 peaks but only 7 onsets: still undecided.
        tracker.Accumulate(Snapshot(Segment(0.0, cPeakMs: 150.0, cOnsetMs: null)));
        for (int i = 1; i < EscapementTimingTracker.MinSamplesForVerdict; i++)
        {
            tracker.Accumulate(Snapshot(Segment(i * 0.250, cPeakMs: 150.0, cOnsetMs: 148.0)));
        }

        Assert.Equal(EscapementTimingTracker.MinSamplesForVerdict, tracker.PeakCount);
        Assert.Equal(EscapementReferenceVerdict.Undecided, tracker.Verdict);

        // The 8th onset completes both references.
        tracker.Accumulate(Snapshot(Segment(99.0, cPeakMs: 150.0, cOnsetMs: 148.0)));
        Assert.NotEqual(EscapementReferenceVerdict.Undecided, tracker.Verdict);
    }

    [Fact]
    public void Verdict_PicksTheReferenceWithTheSmallerSigma()
    {
        // Scattered peaks, steady onsets → onset is more repeatable.
        var onsetWins = new EscapementTimingTracker();
        for (int i = 0; i < EscapementTimingTracker.MinSamplesForVerdict; i++)
        {
            double jitter = i % 2 == 0 ? -2.0 : 2.0;
            onsetWins.Accumulate(Snapshot(Segment(i * 0.250, cPeakMs: 150.0 + jitter, cOnsetMs: 148.0)));
        }

        Assert.Equal(EscapementReferenceVerdict.Onset, onsetWins.Verdict);

        // Steady peaks, scattered onsets → peak is more repeatable.
        var peakWins = new EscapementTimingTracker();
        for (int i = 0; i < EscapementTimingTracker.MinSamplesForVerdict; i++)
        {
            double jitter = i % 2 == 0 ? -2.0 : 2.0;
            peakWins.Accumulate(Snapshot(Segment(i * 0.250, cPeakMs: 150.0, cOnsetMs: 148.0 + jitter)));
        }

        Assert.Equal(EscapementReferenceVerdict.Peak, peakWins.Verdict);

        // Equal sigma keeps the conventional peak reference.
        var tie = new EscapementTimingTracker();
        for (int i = 0; i < EscapementTimingTracker.MinSamplesForVerdict; i++)
        {
            tie.Accumulate(Snapshot(Segment(i * 0.250, cPeakMs: 150.0, cOnsetMs: 148.0)));
        }

        Assert.Equal(EscapementReferenceVerdict.Peak, tie.Verdict);
    }

    [Fact]
    public void Reset_ClearsTheWindowAndTheSeenWatermark()
    {
        var tracker = new EscapementTimingTracker();
        BeatSegmentsSnapshot snapshot = Snapshot(Segment(0.0, cPeakMs: 150.0, cOnsetMs: 148.0));
        tracker.Accumulate(snapshot);

        tracker.Reset();

        Assert.Equal(0, tracker.PeakCount);
        Assert.Equal(0, tracker.OnsetCount);
        Assert.Null(tracker.PeakMeanMs);
        Assert.Null(tracker.OnsetSigmaMs);
        Assert.Equal(EscapementReferenceVerdict.Undecided, tracker.Verdict);

        // A new run restarts stream time at zero; the same times accumulate again.
        tracker.Accumulate(snapshot);
        Assert.Equal(1, tracker.PeakCount);
    }
}
