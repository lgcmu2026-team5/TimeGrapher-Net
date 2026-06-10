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
        // The 400 ms window is still pending after the first block (the Scope 2
        // lane may already have published, but no segment has completed).
        Assert.Empty(capture.CurrentSnapshot()?.Segments ?? Array.Empty<BeatSegment>());

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
    public void Capture_NeverRefillsBuffersOfTheLastTwoSnapshotsDuringACatchUpBurst()
    {
        // The race the publication gate closes: a backlog catch-up pass can
        // complete many beats inside one Project call, and completion-count
        // rotation alone would refill buffers a routed frame still displays.
        var capture = NewCapture();
        ulong position = FeedBeats(capture, streamPosition: 0, beats: 1, beatSamples: 6000);
        BeatSegment published = Assert.Single(capture.CurrentSnapshot()!.Segments);
        ReadOnlyMemory<float> publishedBuffer = published.Samples;

        // Burst of two full pool rotations with NO intervening snapshot build.
        FeedBeats(capture, position, beats: BeatSegmentCapture.SegmentPoolCount * 2, beatSamples: 6000);

        // The published snapshot's buffer was skipped for the whole burst: no
        // segment of the new snapshot lives in that buffer instance.
        BeatSegmentsSnapshot after = capture.CurrentSnapshot()!;
        Assert.All(after.Segments, segment => Assert.False(publishedBuffer.Equals(segment.Samples)));
    }

    [Fact]
    public void Capture_PoolStaysBoundedAcrossLongRuns()
    {
        // Buffers do recycle once their protecting snapshots age out: the set
        // of distinct buffer instances ever published never exceeds the pool.
        var capture = NewCapture();
        var seen = new HashSet<float[]>();
        ulong position = 0;
        for (int round = 0; round < 12; round++)
        {
            position = FeedBeats(capture, position, beats: 6, beatSamples: 6000);
            foreach (BeatSegment segment in capture.CurrentSnapshot()!.Segments)
            {
                Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
                    segment.Samples, out ArraySegment<float> array));
                seen.Add(array.Array!);
            }
        }

        Assert.InRange(seen.Count, 1, BeatSegmentCapture.SegmentPoolCount);
    }

    [Fact]
    public void Capture_SuspensionFreezesNewWindowsLanesAndVersionButCompletesOpenOnes()
    {
        var capture = NewCapture();
        capture.SetSigmaAveraging(true); // Σ on: lane counts accumulate per beat

        // Open one window (A at 1000); the 400 ms window is still pending.
        Feed(capture, 0, 6000, spikes: null, AEvent(1000));
        capture.SetCaptureSuspended(true);

        // While suspended, further beats arrive and enough envelope flows for
        // everything to complete.
        Feed(capture, 6000, 6000, spikes: null, AEvent(7000), CEvent(9400));
        Feed(capture, 12000, WindowSamples + 1000);

        BeatSegmentsSnapshot? suspended = capture.CurrentSnapshot();
        Assert.NotNull(suspended);

        // Only the pre-suspension window completed; the suspended A opened
        // no new window.
        BeatSegment segment = Assert.Single(suspended!.Segments);
        Assert.Equal(
            (1000 - BeatSegmentCapture.PreEventMs * SamplesPerMs) / SampleRate,
            segment.StartTimeS, 6);

        // The lane counts froze with the pre-suspension beat.
        Assert.Equal(1, suspended.Average.Lane1Count + suspended.Average.Lane2Count);

        // More suspended beats and envelope: the snapshot version stays frozen
        // (same shared instance) - nothing opens, accumulates, or completes.
        ulong position = 12000UL + (ulong)(WindowSamples + 1000);
        Feed(capture, position, 6000, spikes: null,
            AEvent(position + 1000), CEvent(position + 3400));
        Feed(capture, position + 6000, WindowSamples + 1000);
        Assert.Same(suspended, capture.CurrentSnapshot());

        // Resume: the next A opens a fresh window and completes it.
        capture.SetCaptureSuspended(false);
        position += 6000UL + (ulong)(WindowSamples + 1000);
        Feed(capture, position, 6000, spikes: null, AEvent(position + 1000));
        Feed(capture, position + 6000, WindowSamples + 1000);

        BeatSegmentsSnapshot resumed = capture.CurrentSnapshot()!;
        Assert.NotSame(suspended, resumed);
        Assert.True(resumed.Version > suspended.Version);
        Assert.Equal(2, resumed.Segments.Count);
        Assert.Equal(
            (position + 1000 - BeatSegmentCapture.PreEventMs * SamplesPerMs) / SampleRate,
            resumed.Segments[^1].StartTimeS, 6);
    }

    [Fact]
    public void Capture_CEventArrivingWhileSuspendedDoesNotAttachToAStalePendingWindow()
    {
        // The pre-suspension window's own C was missed by the detector; the
        // first suspended-period C (its A was skipped, so its own window never
        // opened) sits inside the stale window's 400 ms range and would attach
        // to it without the gate - rendering a C-peak marker from another beat.
        var capture = NewCapture();
        Feed(capture, 0, 6000, spikes: null, AEvent(1000));
        capture.SetCaptureSuspended(true);

        Feed(capture, 6000, 6000, spikes: null, CEvent(8000));
        Feed(capture, 12000, WindowSamples + 1000);

        BeatSegment segment = Assert.Single(capture.CurrentSnapshot()!.Segments);
        Assert.False(segment.CPeakValid);
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
