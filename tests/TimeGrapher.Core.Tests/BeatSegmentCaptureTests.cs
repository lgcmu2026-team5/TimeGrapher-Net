using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.Core.Tests;

/// <summary>
/// Per-beat envelope segment capture for the Beat-Noise Scope: window alignment
/// around planted A/C events, windows spanning detector block boundaries,
/// pooled-buffer rotation, and bounded segment counts.
/// </summary>
public sealed class BeatSegmentCaptureTests
{
    private const int SampleRate = 48000;
    private const double SamplesPerMs = SampleRate / 1000.0;
    private const int WindowSamples = (int)(BeatSegmentCapture.WindowMs / 1000.0 * SampleRate);

    private static BeatSegmentCapture NewCapture() => new(SampleRate, liftAngleDeg: 52.0);

    private static DetectorResultSnapshot Result(float[] pcm, ulong startSample) =>
        new(TgSyncStatus.Synced, 28800, 0.125, Array.Empty<TgEvent>(), pcm, pcm.Length, startSample,
            false, false, false, 0.1f, 0f, 0f, 0f);

    private static DetectedEventUpdate AEvent(double sample, float peak = 0.5f, bool? isTic = null)
    {
        var update = new WatchMetricsUpdate();
        if (isTic is bool tic)
        {
            update.SetBeatTimingSample(new BeatTimingSample(
                1, sample / SampleRate, tic, 0.0, true, 0.0, true, 0.0, 28800));
        }

        var aEvent = new TgEvent { Type = TgEventType.A, SampleIndex = (ulong)sample, PeakValue = peak };
        return new DetectedEventUpdate(aEvent, sample, update);
    }

    private static DetectedEventUpdate CEvent(double sample, double? onsetSample = null)
    {
        var cEvent = new TgEvent
        {
            Type = TgEventType.C,
            SampleIndex = (ulong)sample,
            PeakValue = 0.3f,
            OnsetValid = onsetSample is not null,
            OnsetSampleIndex = (ulong)(onsetSample ?? 0.0),
        };
        return new DetectedEventUpdate(cEvent, sample, new WatchMetricsUpdate());
    }

    /// <summary>One Project pass: a flat-envelope block (with optional spikes at absolute samples) plus planted events.</summary>
    private static void Feed(
        BeatSegmentCapture capture,
        ulong startSample,
        int length,
        (int AbsoluteSample, float Value)[]? spikes = null,
        params DetectedEventUpdate[] events)
    {
        var pcm = new float[length];
        Array.Fill(pcm, 0.05f);
        if (spikes != null)
        {
            foreach ((int absoluteSample, float value) in spikes)
            {
                pcm[absoluteSample - (int)startSample] = value;
            }
        }

        capture.Project(new DetectorMetricsBlockUpdate(Result(pcm, startSample), events));
    }

    /// <summary>
    /// Feeds <paramref name="beats"/> A events (one block per beat, the A near
    /// the block start) plus a trailing block so every window completes.
    /// Returns the stream position after the trailing block.
    /// </summary>
    private static ulong FeedBeats(BeatSegmentCapture capture, ulong streamPosition, int beats, int beatSamples)
    {
        for (int beat = 0; beat < beats; beat++)
        {
            double aSample = streamPosition + 1000;
            Feed(capture, streamPosition, beatSamples, spikes: null, AEvent(aSample));
            streamPosition += (ulong)beatSamples;
        }

        int trailing = WindowSamples + 1000;
        Feed(capture, streamPosition, trailing);
        return streamPosition + (ulong)trailing;
    }

    [Fact]
    public void Capture_AlignsWindowAndMarkerOffsetsAroundPlantedEvents()
    {
        var capture = NewCapture();
        const int aSample = 24000;            // 0.5 s
        const int cSample = aSample + 2400;   // +50 ms
        const int cOnset = cSample - 48;      // 1 ms before the C peak

        Feed(capture, 0, 48000,
            spikes: new[] { (aSample, 0.9f), (cSample, 0.7f) },
            AEvent(aSample, peak: 0.9f, isTic: true),
            CEvent(cSample, onsetSample: cOnset));

        BeatSegmentsSnapshot? snapshot = capture.CurrentSnapshot();
        Assert.NotNull(snapshot);
        BeatSegment segment = Assert.Single(snapshot!.Segments);

        Assert.Equal(52.0, snapshot.LiftAngleDeg);
        Assert.Equal(BeatSegmentCapture.SegmentPoints, segment.Samples.Length);
        Assert.Equal(BeatSegmentCapture.MsPerPoint, segment.MsPerPoint);
        Assert.True(segment.IsTic);
        Assert.Equal(0.9f, segment.PeakValue);

        // The window starts PreEventMs before the A.
        Assert.Equal((aSample - BeatSegmentCapture.PreEventMs * SamplesPerMs) / SampleRate, segment.StartTimeS, 6);
        Assert.Equal(BeatSegmentCapture.PreEventMs, segment.AOffsetMs, 3);

        Assert.True(segment.CPeakValid);
        Assert.Equal(BeatSegmentCapture.PreEventMs + 50.0, segment.CPeakOffsetMs, 3);
        Assert.True(segment.COnsetValid);
        Assert.Equal(BeatSegmentCapture.PreEventMs + 49.0, segment.COnsetOffsetMs, 3);

        // The planted spikes land in the decimated points at their offsets.
        ReadOnlySpan<float> samples = segment.Samples.Span;
        Assert.Equal(0.9f, samples[(int)(segment.AOffsetMs / BeatSegmentCapture.MsPerPoint)]);
        Assert.Equal(0.7f, samples[(int)(segment.CPeakOffsetMs / BeatSegmentCapture.MsPerPoint)]);
        Assert.Equal(0.05f, samples[0]);
    }

    [Fact]
    public void Capture_WindowSpansBlockBoundaries()
    {
        var capture = NewCapture();
        const int aSample = 24000;
        const int spikeSample = aSample + 9600; // +200 ms, inside the second block
        const int firstBlockLength = aSample + 960; // ends 20 ms after the A

        Feed(capture, 0, firstBlockLength,
            spikes: new[] { (aSample, 0.9f) },
            AEvent(aSample, peak: 0.9f));
        Assert.Null(capture.CurrentSnapshot()); // window still pending

        // The second block carries the rest of the window (plus margin); the
        // completed segment stitches both blocks across the boundary.
        Feed(capture, firstBlockLength, 24000, spikes: new[] { (spikeSample, 0.8f) });

        BeatSegmentsSnapshot? snapshot = capture.CurrentSnapshot();
        Assert.NotNull(snapshot);
        BeatSegment segment = Assert.Single(snapshot!.Segments);

        ReadOnlySpan<float> samples = segment.Samples.Span;
        Assert.Equal(0.9f, samples[(int)(BeatSegmentCapture.PreEventMs / BeatSegmentCapture.MsPerPoint)]);
        Assert.Equal(0.8f, samples[(int)((BeatSegmentCapture.PreEventMs + 200.0) / BeatSegmentCapture.MsPerPoint)]);
    }

    [Fact]
    public void Capture_KeepsOnlyTheLastEightSegmentsAndAlternatesPhase()
    {
        var capture = NewCapture();
        FeedBeats(capture, streamPosition: 0, beats: 20, beatSamples: 6000);

        BeatSegmentsSnapshot? snapshot = capture.CurrentSnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(BeatSegmentCapture.SegmentRingCount, snapshot!.Segments.Count);

        // Oldest-first ordering with alternating phase.
        for (int i = 1; i < snapshot.Segments.Count; i++)
        {
            Assert.True(snapshot.Segments[i].StartTimeS > snapshot.Segments[i - 1].StartTimeS);
            Assert.NotEqual(snapshot.Segments[i - 1].IsTic, snapshot.Segments[i].IsTic);
        }
    }

    [Fact]
    public void Capture_ReusesPooledBuffersAfterRotation()
    {
        var capture = NewCapture();

        ulong position = FeedBeats(capture, streamPosition: 0, beats: 1, beatSamples: 6000);
        BeatSegment first = Assert.Single(capture.CurrentSnapshot()!.Segments);
        ReadOnlyMemory<float> firstBuffer = first.Samples;

        // After SegmentPoolCount further completions the pool has wrapped: the
        // newest segment is written into the same buffer instance again.
        FeedBeats(capture, position, beats: BeatSegmentCapture.SegmentPoolCount, beatSamples: 6000);

        BeatSegmentsSnapshot snapshot = capture.CurrentSnapshot()!;
        BeatSegment newest = snapshot.Segments[^1];
        Assert.True(firstBuffer.Equals(newest.Samples));

        // No older segment still visible in the ring shares the recycled buffer
        // (published segments stay immutable until rotated out of the pool).
        for (int i = 0; i < snapshot.Segments.Count - 1; i++)
        {
            Assert.False(firstBuffer.Equals(snapshot.Segments[i].Samples));
        }
    }

    [Fact]
    public void Snapshot_IsSharedUntilANewSegmentCompletes()
    {
        var capture = NewCapture();
        ulong position = FeedBeats(capture, streamPosition: 0, beats: 1, beatSamples: 6000);

        BeatSegmentsSnapshot? first = capture.CurrentSnapshot();
        Assert.NotNull(first);
        Assert.Same(first, capture.CurrentSnapshot());

        FeedBeats(capture, position, beats: 1, beatSamples: 6000);
        BeatSegmentsSnapshot? second = capture.CurrentSnapshot();
        Assert.NotSame(first, second);
        Assert.True(second!.Version > first!.Version);
    }
}
