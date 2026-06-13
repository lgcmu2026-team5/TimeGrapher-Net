/* TgTypes.cs -- public detection-layer types.
 *
 * Originally ported from the public enums / structs / config in
 * Timegrapher.h (the comments below preserve the load-bearing parts of the
 * original field-by-field documentation). The provisional C++ source is not
 * authoritative; this surface evolves on its own merits, with behavior
 * drift guarded by the golden-master / parity / --fidelity-check tripwires.
 */

namespace TimeGrapher.Core.Detection;

/// <summary>tg_bph_mode_t.</summary>
public enum TgBphMode
{
    Auto = 0,
    Manual = 1,
}

/// <summary>tg_sync_status_t.</summary>
public enum TgSyncStatus
{
    NotSynced = 0,
    Synced = 1,
    Mismatch = 2, // manual BPH set but signal doesn't match
}

/// <summary>tg_event_type_t.</summary>
public enum TgEventType
{
    Unknown = 0,
    A = 1, // unlock
    C = 2, // drop / lock
}

/// <summary>
/// tg_c_placement_t. Controls which timing (peak vs. onset) fills the
/// primary fields of a C event. Peak is the V5.0-V5.3 default behaviour.
/// </summary>
public enum TgCPlacement
{
    Peak = 0,
    Onset = 1,
}

/// <summary>tg_config_t.</summary>
public sealed class TgConfig
{
    /* Required */
    public double SampleRate;            // Hz, e.g. 48000
    public TgBphMode BphMode;
    public int ManualBph;                // used when BphMode == Manual

    /* Optional - 0 = library default */
    public double HpfCutoffHz;           // default 200.0
    public double EnvelopeSmoothMs;      // default 0.15
    public double SyncTolerancePct;      // default 3.0
    public double AutoDetectSeconds;     // default 1.5
    public int SyncLossMisses;           // default 12
    public double PllPeriodGain;         // default 0.01
    public double PllAcGain;             // default 0.05

    /* Detector threshold tuning (init-time only; the runtime tg_get/set_*
     * surface was removed as dead parity API).
     * Both 0 -> use built-in defaults: 0.03 onset, 0.20 min-peak. */
    public double OnsetFractionInit;
    public double MinPeakFractionInit;

    /* If true, drop events emitted before BPH lock from the output. */
    public bool SuppressPreSyncEvents;

    /* V5.4: C-event timing placement. Default Peak (backward compatible
     * with V5.3 and earlier). */
    public TgCPlacement CPlacement;

    /// <summary>tg_config_default: populate with the library defaults.</summary>
    public static TgConfig Default()
    {
        return new TgConfig
        {
            SampleRate = 48000.0,
            BphMode = TgBphMode.Auto,
            ManualBph = 0,
            HpfCutoffHz = 200.0,
            EnvelopeSmoothMs = 0.15,
            SyncTolerancePct = 3.0,
            AutoDetectSeconds = 1.5,
            SyncLossMisses = 12,
            PllPeriodGain = 0.01,
            PllAcGain = 0.05,
            OnsetFractionInit = 0.0,
            MinPeakFractionInit = 0.0,
            SuppressPreSyncEvents = false,
            CPlacement = TgCPlacement.Peak, // V5.4 default
        };
    }
}

/// <summary>tg_event_t.</summary>
public struct TgEvent
{
    /* Primary timing (chosen per c_placement for C events; for A
     * events this is always the onset). */
    public double TimeSeconds;        // sub-sample accurate
    public ulong SampleIndex;         // integer absolute sample index
    public double SubSampleOffset;    // in [-0.5, +0.5]
    public float PeakValue;           // envelope value at peak
    public TgEventType Type;
    public bool IsPreSync;            // true if before BPH lock

    /* V5.4: C-event onset metadata. Populated for C events when the
     * backward-walk algorithm successfully locates the C cluster's
     * rising edge. Zero / 0 for A events and for C events where onset
     * detection failed. */
    public ulong OnsetSampleIndex;
    public double OnsetSubSampleOffset;
    public double OnsetTimeSeconds;
    public bool OnsetValid;           // true if onset fields are populated
}

/// <summary>
/// tg_result_t. Process/Flush fill its contents; the instance is reused
/// across calls (Events is cleared, ProcessedPcm reallocated as needed).
/// </summary>
public sealed class TgResult
{
    /* Sync state */
    public TgSyncStatus SyncStatus;
    public int DetectedBph;
    public double MeasuredPeriodS;       // nominal locked beat period (3600/bph), 0 while unsynced

    /* Events emitted in THIS call. */
    public List<TgEvent> Events = new();

    /* Envelope, delayed for alignment with events. ProcessedPcm[0]
     * corresponds to absolute input sample ProcessedPcmStartSample. */
    public float[] ProcessedPcm = Array.Empty<float>();
    public int ProcessedPcmLen;
    public ulong ProcessedPcmStartSample;

    /* One-shot edge flags */
    public bool SyncLostEvent;
    public bool SyncAcquiredEvent;
    public bool DetectorResetEvent; // V5.6: large amplitude jump flush

    /* Detector state (instantaneous) for diagnostics / UI */
    public float OnsetThreshold;
    public float MinPeakThreshold;
    public float NoiseFloor;
    public float ReferencePeak;
}
