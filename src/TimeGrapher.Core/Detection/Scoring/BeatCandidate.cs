namespace TimeGrapher.Core.Detection.Scoring;

/// <summary>
/// Candidate-event context handed to an <see cref="IBeatEventGate"/>.
/// Captured at emission time (sync state, thresholds) so windowed gates that
/// decide after their post-window plus one analysis block still see the state
/// the event was born under. <see cref="PllMatched"/> is false only for events
/// that failed the PLL phase match while a lock was held; pre-lock and
/// post-loss events are always true, so a gate keyed on it can never starve
/// lock acquisition.
/// </summary>
public readonly record struct BeatCandidate(
    TgEvent Event,
    bool Synced,
    int DetectedBph,
    double BeatPeriodS,
    float NoiseFloor,
    float ReferencePeak,
    bool PllMatched);
