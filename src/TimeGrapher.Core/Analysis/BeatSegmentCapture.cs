using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Analysis;

/// <summary>
/// Captures one decimated envelope window per detected A event — from
/// <see cref="PreEventMs"/> before the A to <see cref="WindowMs"/> total — and
/// publishes the last <see cref="SegmentRingCount"/> completed windows as an
/// immutable <see cref="BeatSegmentsSnapshot"/> (Beat-Noise Scope; shared
/// infrastructure for any beat-aligned waveform view).
///
/// A fixed-size rolling envelope ring (~<see cref="EnvelopeRingSeconds"/> of
/// ProcessedPcm, sized from the sample rate at construction) lets a window span
/// detector block boundaries: a window opens on its A event and is filled from
/// the ring only once the envelope has advanced past the window end. Windows
/// overlap (beats are shorter than the window), so several can be pending at
/// once.
///
/// Segment buffers rotate through a fixed pool of <see cref="SegmentPoolCount"/>
/// float[<see cref="SegmentPoints"/>] arrays instead of allocating per beat
/// (the SoundPrintFrameProjector publish-pool pattern). Reuse is gated on
/// publication, not completion count: a buffer referenced by either of the two
/// most recently built snapshots — or still sitting in the completed ring — is
/// skipped by the acquire scan, because the UI renders at most one snapshot
/// behind the newest published one and a backlog catch-up pass can complete
/// many beats inside a single Project call (a completion-count margin alone
/// would let such a pass refill what a routed frame is still reading). Ring
/// (≤8) plus two snapshots (≤16) protect at most 24 of the 28 buffers, so the
/// scan always finds a free one.
///
/// The capture also drives the Scope 2 <see cref="BeatNoiseAverager"/>: the
/// first 20 ms of every window is decimated into a reused scratch trace and
/// accumulated into the phase-alternating lanes as soon as its envelope data
/// arrives.
///
/// Sibling of <see cref="ScopeRateFrameProjector"/>; Project/AppendSnapshot run
/// on the analysis thread only.
/// </summary>
public sealed class BeatSegmentCapture
{
    /// <summary>Window length (ms): covers the 400 ms maximum Beat-Noise Scope range.</summary>
    public const double WindowMs = 400.0;

    /// <summary>Pre-roll before the A event (ms), so the noise onset is visible.</summary>
    public const double PreEventMs = 5.0;

    /// <summary>Fixed decimated points per segment (0.25 ms/point at the 400 ms window).</summary>
    public const int SegmentPoints = 1600;

    public const double MsPerPoint = WindowMs / SegmentPoints;

    /// <summary>Completed segments kept (the strip lane shows this many recent beats).</summary>
    public const int SegmentRingCount = 8;

    /// <summary>
    /// Pooled segment buffers. Must exceed the worst-case protected set: the
    /// completed ring (8) plus the two most recently built snapshots (8 each,
    /// disjoint from the ring after a catch-up burst) = 24, leaving 4 free.
    /// </summary>
    public const int SegmentPoolCount = 28;

    private const double EnvelopeRingSeconds = 0.6;

    // Bounded backlog of open (not yet filled) windows. Windows complete after
    // WindowMs, so at most ceil(WindowMs / beat period) + 1 are open at once
    // (5 at the fastest standard 43200 BPH); overflow drops the oldest.
    private const int PendingSlots = 8;

    private struct PendingSegment
    {
        public ulong StartSample;
        public double ASample;
        public float PeakValue;
        public bool IsTic;
        public bool HasC;
        public double CPeakSample;
        public bool COnsetValid;
        public double COnsetSample;
        public bool LaneAccumulated;
    }

    private struct CompletedSegment
    {
        public float[] Buffer;
        public int PoolIndex;
        public double StartTimeS;
        public bool IsTic;
        public double AOffsetMs;
        public float PeakValue;
        public bool CPeakValid;
        public double CPeakOffsetMs;
        public bool COnsetValid;
        public double COnsetOffsetMs;
    }

    private readonly int _sampleRate;
    private readonly double _liftAngleDeg;
    private readonly float[] _envelopeRing;
    private ulong _envelopeEndSample;

    private readonly float[][] _segmentPool;
    private int _nextPoolBuffer;

    // Snapshot version that last published each pool buffer (0 = never).
    // Buffers published within the last two built snapshots are protected
    // from reuse; see the class doc.
    private readonly ulong[] _bufferPublishedVersion = new ulong[SegmentPoolCount];

    private readonly PendingSegment[] _pending = new PendingSegment[PendingSlots];
    private int _pendingHead;
    private int _pendingCount;
    private bool _lastIsTic;

    private readonly CompletedSegment[] _completed = new CompletedSegment[SegmentRingCount];
    private int _completedHead;
    private int _completedCount;

    // Scope 2 lane averaging over the first LaneWindowMs of each window,
    // decimated into a reused scratch buffer (no per-beat allocation). The Σ
    // request is written from any thread (UI toggle) and applied analysis-side
    // at the start of the next pass (the SweepFrameProjector knob pattern).
    private readonly BeatNoiseAverager _averager = new();
    private readonly float[] _laneScratch = new float[BeatNoiseAverager.LanePoints];
    private volatile bool _requestedSigma;

    private bool _dirty;
    private ulong _version;
    private BeatSegmentsSnapshot? _snapshot;

    public BeatSegmentCapture(int sampleRate, double liftAngleDeg)
    {
        _sampleRate = sampleRate;
        _liftAngleDeg = liftAngleDeg;
        _envelopeRing = new float[Math.Max(1, (int)(EnvelopeRingSeconds * sampleRate))];
        _segmentPool = new float[SegmentPoolCount][];
        for (int i = 0; i < SegmentPoolCount; i++)
        {
            _segmentPool[i] = new float[SegmentPoints];
        }
    }

    /// <summary>
    /// Requests the Scope 2 Σ averaging mode. Applied on the analysis thread at
    /// the start of the next pass; a change resets the averaging cycle.
    /// Callable from any thread.
    /// </summary>
    public void SetSigmaAveraging(bool enabled)
    {
        _requestedSigma = enabled;
    }

    public void Project(DetectorMetricsBlockUpdate update)
    {
        if (_averager.SetSigmaEnabled(_requestedSigma))
        {
            _dirty = true;
        }

        AppendEnvelope(update.Result);

        foreach (DetectedEventUpdate eventUpdate in update.Events)
        {
            if (eventUpdate.Event.Type == TgEventType.A)
            {
                OpenSegment(eventUpdate);
            }
            else if (eventUpdate.Event.Type == TgEventType.C)
            {
                AttachCEvent(eventUpdate.Event);
            }
        }

        // Lanes first: a window that is already fully complete in this pass
        // must still contribute its 20 ms lane trace before leaving the
        // pending queue.
        AccumulateReadyLanes();
        CompleteReadySegments();
    }

    /// <summary>
    /// Latest snapshot, rebuilt only when a segment completed since the last
    /// build; in between, the same shared instance reattaches to every frame
    /// (the BeatMetricsHistory pattern — the per-beat rebuild allocates only
    /// the small segment descriptors, never the sample buffers).
    /// Null until the first completed segment.
    /// </summary>
    public void AppendSnapshot(AnalysisFrame frame)
    {
        frame.BeatSegments = CurrentSnapshot();
    }

    public BeatSegmentsSnapshot? CurrentSnapshot()
    {
        if (!_dirty)
        {
            return _snapshot;
        }

        _version++;
        var segments = new List<BeatSegment>(_completedCount);
        for (int i = 0; i < _completedCount; i++)
        {
            CompletedSegment completed = _completed[(_completedHead + i) % SegmentRingCount];
            _bufferPublishedVersion[completed.PoolIndex] = _version;
            segments.Add(new BeatSegment
            {
                Samples = completed.Buffer,
                MsPerPoint = MsPerPoint,
                StartTimeS = completed.StartTimeS,
                IsTic = completed.IsTic,
                AOffsetMs = completed.AOffsetMs,
                PeakValue = completed.PeakValue,
                CPeakValid = completed.CPeakValid,
                CPeakOffsetMs = completed.CPeakOffsetMs,
                COnsetValid = completed.COnsetValid,
                COnsetOffsetMs = completed.COnsetOffsetMs,
            });
        }

        _snapshot = new BeatSegmentsSnapshot
        {
            Version = _version,
            Segments = segments,
            LiftAngleDeg = _liftAngleDeg,
            Average = _averager.Snapshot(),
        };
        _dirty = false;
        return _snapshot;
    }

    private void AppendEnvelope(DetectorResultSnapshot result)
    {
        int length = result.ProcessedPcmLen;
        if (length <= 0)
        {
            return;
        }

        // Wrap-aware block copy (at most two segments), not per-sample writes.
        int ringLength = _envelopeRing.Length;
        ulong start = result.ProcessedPcmStartSample;
        int copied = 0;
        while (copied < length)
        {
            int destination = (int)((start + (ulong)copied) % (ulong)ringLength);
            int chunk = Math.Min(length - copied, ringLength - destination);
            Array.Copy(result.ProcessedPcm, copied, _envelopeRing, destination, chunk);
            copied += chunk;
        }

        _envelopeEndSample = start + (ulong)length;
    }

    private void OpenSegment(DetectedEventUpdate eventUpdate)
    {
        double aSample = eventUpdate.EventSample;
        double preSamples = PreEventMs / 1000.0 * _sampleRate;
        ulong startSample = aSample > preSamples ? (ulong)(aSample - preSamples) : 0UL;

        // Phase from the metrics sample when it was emitted on this A event
        // (synced); otherwise keep the alternation going locally.
        bool isTic = eventUpdate.MetricsUpdate.BeatTimingSampleUpdated
            ? eventUpdate.MetricsUpdate.BeatTimingSample.IsTic
            : !_lastIsTic;
        _lastIsTic = isTic;

        if (_pendingCount == _pending.Length)
        {
            _pendingHead = (_pendingHead + 1) % _pending.Length;
            _pendingCount--;
        }

        int slot = (_pendingHead + _pendingCount) % _pending.Length;
        _pending[slot] = new PendingSegment
        {
            StartSample = startSample,
            ASample = aSample,
            PeakValue = eventUpdate.Event.PeakValue,
            IsTic = isTic,
        };
        _pendingCount++;
    }

    private void AttachCEvent(TgEvent cEvent)
    {
        double cPeakSample = cEvent.SampleIndex + cEvent.SubSampleOffset;

        // The C belongs to the newest window whose A precedes it (windows
        // overlap, so older windows also contain this C — but it is not their
        // beat's C).
        for (int i = _pendingCount - 1; i >= 0; i--)
        {
            int index = (_pendingHead + i) % _pending.Length;
            ref PendingSegment pending = ref _pending[index];
            if (cPeakSample <= pending.ASample)
            {
                continue;
            }

            if (!pending.HasC)
            {
                pending.HasC = true;
                pending.CPeakSample = cPeakSample;
                pending.COnsetValid = cEvent.OnsetValid;
                pending.COnsetSample = cEvent.OnsetSampleIndex + cEvent.OnsetSubSampleOffset;
            }

            break;
        }
    }

    /// <summary>
    /// Feeds the averager the first <see cref="BeatNoiseAverager.LaneWindowMs"/>
    /// of every pending window whose envelope data has arrived (well before the
    /// full window completes, so Scope 2 progress leads Scope 1 strips).
    /// </summary>
    private void AccumulateReadyLanes()
    {
        ulong laneSamples = (ulong)Math.Ceiling(BeatNoiseAverager.LaneWindowMs / 1000.0 * _sampleRate);
        for (int i = 0; i < _pendingCount; i++)
        {
            int index = (_pendingHead + i) % _pending.Length;
            ref PendingSegment pending = ref _pending[index];
            if (pending.LaneAccumulated)
            {
                continue;
            }

            if (_envelopeEndSample < pending.StartSample + laneSamples)
            {
                // Pending windows open in stream order, so none after this one
                // can be ready either.
                break;
            }

            DecimateWindow(pending.StartSample, BeatNoiseAverager.LaneWindowMs, _laneScratch);
            if (_averager.Add(pending.IsTic, _laneScratch))
            {
                _dirty = true;
            }

            pending.LaneAccumulated = true;
        }
    }

    private void CompleteReadySegments()
    {
        ulong windowSamples = (ulong)Math.Ceiling(WindowMs / 1000.0 * _sampleRate);
        while (_pendingCount > 0)
        {
            ref PendingSegment oldest = ref _pending[_pendingHead];
            if (_envelopeEndSample < oldest.StartSample + windowSamples)
            {
                // Pending windows open in stream order, so none after this one
                // can be complete either.
                break;
            }

            CompleteSegment(in oldest);
            _pendingHead = (_pendingHead + 1) % _pending.Length;
            _pendingCount--;
        }
    }

    private void CompleteSegment(in PendingSegment pending)
    {
        float[] buffer = AcquireSegmentBuffer(out int poolIndex);
        DecimateWindow(pending.StartSample, WindowMs, buffer);

        double samplesToMs = 1000.0 / _sampleRate;
        double cPeakOffsetMs = pending.HasC
            ? (pending.CPeakSample - pending.StartSample) * samplesToMs
            : 0.0;
        double cOnsetOffsetMs = pending.HasC && pending.COnsetValid
            ? (pending.COnsetSample - pending.StartSample) * samplesToMs
            : 0.0;

        int slot;
        if (_completedCount == SegmentRingCount)
        {
            slot = _completedHead;
            _completedHead = (_completedHead + 1) % SegmentRingCount;
        }
        else
        {
            slot = (_completedHead + _completedCount) % SegmentRingCount;
            _completedCount++;
        }

        _completed[slot] = new CompletedSegment
        {
            Buffer = buffer,
            PoolIndex = poolIndex,
            StartTimeS = pending.StartSample / (double)_sampleRate,
            IsTic = pending.IsTic,
            AOffsetMs = (pending.ASample - pending.StartSample) * samplesToMs,
            PeakValue = pending.PeakValue,
            CPeakValid = pending.HasC && cPeakOffsetMs < WindowMs,
            CPeakOffsetMs = cPeakOffsetMs,
            COnsetValid = pending.HasC && pending.COnsetValid && cOnsetOffsetMs is >= 0.0 and < WindowMs,
            COnsetOffsetMs = cOnsetOffsetMs,
        };
        _dirty = true;
    }

    /// <summary>
    /// Next reusable pool buffer, skipping every protected one (in the
    /// completed ring or published by one of the two most recent snapshots).
    /// The protected set is at most 24 of the 28 buffers (see the class doc),
    /// so the scan always succeeds; the trailing fallback is unreachable and
    /// only keeps the method total.
    /// </summary>
    private float[] AcquireSegmentBuffer(out int poolIndex)
    {
        for (int probe = 0; probe < SegmentPoolCount; probe++)
        {
            int candidate = (_nextPoolBuffer + probe) % SegmentPoolCount;
            if (IsBufferProtected(candidate))
            {
                continue;
            }

            _nextPoolBuffer = (candidate + 1) % SegmentPoolCount;
            poolIndex = candidate;
            return _segmentPool[candidate];
        }

        poolIndex = _nextPoolBuffer;
        _nextPoolBuffer = (_nextPoolBuffer + 1) % SegmentPoolCount;
        return _segmentPool[poolIndex];
    }

    private bool IsBufferProtected(int poolIndex)
    {
        ulong published = _bufferPublishedVersion[poolIndex];
        if (published != 0 && _version - published <= 1)
        {
            return true;
        }

        for (int i = 0; i < _completedCount; i++)
        {
            if (_completed[(_completedHead + i) % SegmentRingCount].PoolIndex == poolIndex)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fills <paramref name="target"/> with the envelope maximum of each
    /// equal-width bucket across the window (envelope-preserving decimation,
    /// the SweepFrameProjector bin policy).
    /// </summary>
    private void DecimateWindow(ulong startSample, double windowMs, float[] target)
    {
        int ringLength = _envelopeRing.Length;
        double samplesPerPoint = windowMs / 1000.0 * _sampleRate / target.Length;
        for (int point = 0; point < target.Length; point++)
        {
            ulong from = startSample + (ulong)(point * samplesPerPoint);
            ulong to = startSample + (ulong)((point + 1) * samplesPerPoint);
            if (to <= from)
            {
                to = from + 1;
            }

            float max = _envelopeRing[(int)(from % (ulong)ringLength)];
            for (ulong sample = from + 1; sample < to; sample++)
            {
                float value = _envelopeRing[(int)(sample % (ulong)ringLength)];
                if (value > max)
                {
                    max = value;
                }
            }

            target[point] = max;
        }
    }
}
