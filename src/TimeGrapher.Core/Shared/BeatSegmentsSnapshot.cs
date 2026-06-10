namespace TimeGrapher.Core.Shared;

/// <summary>
/// One captured beat-noise segment: the decimated envelope window around a
/// detected A event (a short pre-roll before A through the configured window
/// length), with the in-window marker offsets the Beat-Noise Scope displays.
/// <para>
/// <see cref="Samples"/> references a pooled buffer owned by
/// <see cref="TimeGrapher.Core.Analysis.BeatSegmentCapture"/> (the
/// SoundPrintFrameProjector publish-pool pattern): the contents are immutable
/// by contract until enough newer segments have completed to rotate the pool,
/// so consumers must read only the segments of the latest snapshot and never
/// cache a segment across snapshots.
/// </para>
/// </summary>
public sealed class BeatSegment
{
    /// <summary>Decimated envelope of the window (rectified, so values are non-negative).</summary>
    public ReadOnlyMemory<float> Samples { get; init; }

    /// <summary>Milliseconds covered by one sample point.</summary>
    public double MsPerPoint { get; init; }

    /// <summary>Stream time (s) of the window start (A minus the pre-roll).</summary>
    public double StartTimeS { get; init; }

    /// <summary>
    /// Alternating beat phase (the odd-beat-number "tic" convention of
    /// <see cref="BeatTimingSample.IsTic"/>) — a lane assignment, not a claim
    /// about which physical tick/tock noise this is.
    /// </summary>
    public bool IsTic { get; init; }

    /// <summary>A (unlock) event offset within the window (ms).</summary>
    public double AOffsetMs { get; init; }

    /// <summary>Envelope peak value of the A event.</summary>
    public float PeakValue { get; init; }

    /// <summary>C (drop/lock) peak offset within the window (ms); valid only when the C arrived inside the window.</summary>
    public bool CPeakValid { get; init; }
    public double CPeakOffsetMs { get; init; }

    /// <summary>C onset offset within the window (ms); valid only when the detector located the C cluster's rising edge.</summary>
    public bool COnsetValid { get; init; }
    public double COnsetOffsetMs { get; init; }
}

/// <summary>
/// Ring of the most recent completed beat segments, carried by every frame.
/// Cumulative by design: the render scheduler coalesces frames latest-wins, so
/// dropped intermediate frames lose nothing. Rebuilt only when a segment
/// completes; in between, frames share the same immutable instance (the
/// BeatMetricsHistorySnapshot sharing pattern).
/// </summary>
public sealed class BeatSegmentsSnapshot
{
    /// <summary>Increments whenever snapshot content changed; consumers can skip re-rendering on equal versions.</summary>
    public ulong Version { get; init; }

    /// <summary>Completed segments, oldest first (bounded by the capture's segment ring).</summary>
    public IReadOnlyList<BeatSegment> Segments { get; init; } = Array.Empty<BeatSegment>();

    /// <summary>Lift angle (deg) the producing analysis run was configured with.</summary>
    public double LiftAngleDeg { get; init; }
}
