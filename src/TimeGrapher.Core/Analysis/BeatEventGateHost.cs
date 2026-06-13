using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Detection.Scoring;

namespace TimeGrapher.Core.Analysis;

/// <summary>Engine-level event-gate configuration (carries the implementation).</summary>
public sealed record BeatEventGateConfig(IBeatEventGate Gate);

/// <summary>
/// Hosts an <see cref="IBeatEventGate"/> at the metrics choke point of
/// DetectorMetricsEngine. Zero-window gates decide immediately and add no
/// latency or copying. Window-requesting gates get an engine-owned ring sized
/// from the requested delayed-envelope window (ProcessedPcm) with FIFO delayed release: an
/// event is decided once the ring covers its post-window, structurally
/// guaranteeing window availability (the detector's internal 50 ms EnvRing
/// cannot make that guarantee against 4096-sample blocks). Event timestamps
/// are never altered; release latency defers metric/display delivery by the
/// requested post-window plus at most one analysis block.
///
/// A vetoed A also vetoes the immediately following C (one-shot pair veto)
/// so an orphaned C cannot pair into a bogus amplitude reading.
/// </summary>
internal sealed class BeatEventGateHost
{
    private readonly IBeatEventGate _gate;
    private readonly double _sampleRate;
    private readonly int _preSamples;
    private readonly int _postSamples;
    private readonly bool _windowed;

    /* Delayed-envelope ring (windowed gates only). */
    private readonly float[] _ring;
    private ulong _ringNewestAbs;
    private int _ringCount;
    private int _ringHead;
    private readonly float[] _windowScratch;

    internal readonly record struct ReleasedEvent(
        TgEvent Event, double EventSample, BeatCandidate Candidate, bool Accepted);

    private readonly record struct PendingEvent(
        TgEvent Event, double EventSample, BeatCandidate Candidate);

    private readonly Queue<PendingEvent> _pending = new();
    private readonly List<ReleasedEvent> _released = new();
    private bool _vetoFollowingC;

    /// <summary>Total events dropped by the gate (including pair-vetoed Cs).</summary>
    public ulong VetoedEvents { get; private set; }

    public BeatEventGateHost(IBeatEventGate gate, double sampleRate)
    {
        _gate = gate;
        _sampleRate = sampleRate;
        _preSamples = (int)(gate.WindowPreMs * 1e-3 * sampleRate);
        _postSamples = (int)(gate.WindowPostMs * 1e-3 * sampleRate);
        _windowed = _preSamples > 0 || _postSamples > 0;
        if (_windowed)
        {
            /* The ring must out-size the requested window plus one second of
             * block headroom (windowReady is checked at block granularity,
             * so an append can overshoot the post-window by up to a block):
             * a ring smaller than the request would evict the event before
             * its window is ever ready, silently feeding the gate truncated
             * windows with the offset = -1 'no window requested' sentinel. */
            int capacity = Math.Max((int)(0.5 * sampleRate),
                                    _preSamples + _postSamples + 1 + (int)sampleRate);
            _ring = new float[capacity];
            _windowScratch = new float[_preSamples + _postSamples + 1];
        }
        else
        {
            _ring = Array.Empty<float>();
            _windowScratch = Array.Empty<float>();
        }
    }

    /// <summary>Feeds the block's delayed envelope; no-op for zero-window gates.</summary>
    public void AppendEnvelope(float[] pcm, int len, ulong startAbs)
    {
        if (!_windowed || len == 0)
        {
            return;
        }
        for (int i = 0; i < len; i++)
        {
            _ring[_ringHead] = pcm[i];
            _ringHead = (_ringHead + 1) % _ring.Length;
        }
        _ringNewestAbs = startAbs + (ulong)len - 1;
        _ringCount = Math.Min(_ring.Length, _ringCount + len);
    }

    public void Submit(in TgEvent ev, double eventSample, in BeatCandidate candidate)
    {
        _pending.Enqueue(new PendingEvent(ev, eventSample, candidate));
    }

    /// <summary>
    /// Decides every pending event whose window is available (all of them
    /// for zero-window gates); <paramref name="force"/> releases the rest
    /// unscored (accepted) at stream/sync boundaries. Returns decisions in
    /// submission order; the list is reused across calls.
    /// </summary>
    public List<ReleasedEvent> Release(bool force)
    {
        _released.Clear();
        while (_pending.Count > 0)
        {
            PendingEvent pending = _pending.Peek();
            ulong eventIdx = pending.Event.SampleIndex;
            bool windowReady = !_windowed || _ringNewestAbs >= eventIdx + (ulong)_postSamples;
            if (!windowReady && !force)
            {
                break;
            }
            _pending.Dequeue();

            bool accepted = (!windowReady && force) || Decide(pending);

            if (_vetoFollowingC)
            {
                if (pending.Event.Type == TgEventType.C)
                {
                    accepted = false;
                }
                _vetoFollowingC = false;
            }
            if (!accepted)
            {
                VetoedEvents++;
                if (pending.Event.Type == TgEventType.A)
                {
                    _vetoFollowingC = true;
                }
            }

            _released.Add(new ReleasedEvent(pending.Event, pending.EventSample, pending.Candidate, accepted));
        }
        return _released;
    }

    /// <summary>Forwards sync-loss / regime-reset notifications to the gate.</summary>
    public void ResetGate()
    {
        _gate.Reset();
        _vetoFollowingC = false;
    }

    private bool Decide(in PendingEvent pending)
    {
        if (!_windowed)
        {
            return _gate.Accept(ReadOnlySpan<float>.Empty, -1, _sampleRate, pending.Candidate);
        }

        ulong eventIdx = pending.Event.SampleIndex;
        if (_ringCount == 0)
        {
            return _gate.Accept(ReadOnlySpan<float>.Empty, -1, _sampleRate, pending.Candidate);
        }

        ulong ringOldest = _ringNewestAbs - (ulong)(_ringCount - 1);
        ulong start = eventIdx >= (ulong)_preSamples ? eventIdx - (ulong)_preSamples : 0;
        if (start < ringOldest) start = ringOldest;
        ulong end = eventIdx + (ulong)_postSamples;
        if (end > _ringNewestAbs) end = _ringNewestAbs;
        if (end < start)
        {
            return _gate.Accept(ReadOnlySpan<float>.Empty, -1, _sampleRate, pending.Candidate);
        }

        int len = (int)(end - start + 1);
        for (int i = 0; i < len; i++)
        {
            _windowScratch[i] = RingAt(start + (ulong)i);
        }
        int offset = (eventIdx >= start && eventIdx <= end) ? (int)(eventIdx - start) : -1;
        return _gate.Accept(_windowScratch.AsSpan(0, len), offset, _sampleRate, pending.Candidate);
    }

    private float RingAt(ulong abs)
    {
        ulong age = _ringNewestAbs - abs;
        int newestSlot = (_ringHead + _ring.Length - 1) % _ring.Length;
        int idx = (int)(((ulong)newestSlot + (ulong)_ring.Length - age) % (ulong)_ring.Length);
        return _ring[idx];
    }
}
