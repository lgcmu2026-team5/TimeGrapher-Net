namespace TimeGrapher.Core.Detection.Scoring;

/// <summary>
/// Veto-only gate over detected beat events, applied at the metrics choke
/// point in DetectorMetricsEngine. A gate can only DROP candidate events; it
/// cannot create or re-time them, so a misbehaving implementation cannot
/// break detection timing. BPH detection and the sync PLL always see the
/// full raw event stream - the gate sits strictly between detection and the
/// metrics/display consumers, so it cannot break lock acquisition either
/// (structural guarantee, not policy).
///
/// This interface is the TinyML socket: the classical reference
/// implementation is <see cref="PllMatchGate"/> (ships now); a future ONNX
/// tick/noise classifier in a leaf inference project implements the same
/// interface and becomes A/B-comparable through the same verifier harness.
/// Core stays dependency-free: implementations needing ML runtimes live
/// outside this assembly and are injected at the composition root.
/// </summary>
public interface IBeatEventGate
{
    /// <summary>Short identifier used by the verifier's reporting.</summary>
    string Name { get; }

    /// <summary>
    /// Requested envelope context around the event, in milliseconds. When
    /// both are zero the gate is called immediately with an empty window and
    /// adds zero latency; otherwise the engine buffers the delayed envelope
    /// and calls <see cref="Accept"/> once the post-window is available
    /// (post-window plus at most one analysis block later; event timestamps
    /// are unaffected).
    /// </summary>
    double WindowPreMs { get; }
    double WindowPostMs { get; }

    /// <summary>
    /// Returns false to drop the candidate before it reaches metrics.
    /// <paramref name="envelopeWindow"/> is a slice of the delayed envelope
    /// (ProcessedPcm domain); it can be shorter than requested near stream
    /// boundaries. Gates that requested no window receive an empty span and
    /// <paramref name="eventOffsetInWindow"/> = -1.
    /// </summary>
    bool Accept(ReadOnlySpan<float> envelopeWindow, int eventOffsetInWindow,
                double sampleRate, in BeatCandidate candidate);

    /// <summary>Called on sync loss and detector regime reset.</summary>
    void Reset();
}
