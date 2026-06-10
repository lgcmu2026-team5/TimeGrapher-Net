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
/// (the SoundPrintFrameProjector publish-pool pattern): a published buffer is
/// refilled again only after SegmentPoolCount-1 newer completions, and the
/// render scheduler's latest-wins delivery keeps the UI within one snapshot of
/// the newest data, so on-screen reads never touch a buffer being recycled.
/// Published segments are immutable by contract until rotated out.
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

    /// <summary>Pooled segment buffers; must exceed the ring so published buffers recycle late.</summary>
    public const int SegmentPoolCount = 16;

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
    }

    private struct CompletedSegment
    {
        public float[] Buffer;
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

    private readonly PendingSegment[] _pending = new PendingSegment[PendingSlots];
    private int _pendingHead;
    private int _pendingCount;
    private bool _lastIsTic;

    private readonly CompletedSegment[] _completed = new CompletedSegment[SegmentRingCount];
    private int _completedHead;
    private int _completedCount;

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

    public void Project(DetectorMetricsBlockUpdate update)
    {
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
        float[] buffer = _segmentPool[_nextPoolBuffer];
        _nextPoolBuffer = (_nextPoolBuffer + 1) % SegmentPoolCount;
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
