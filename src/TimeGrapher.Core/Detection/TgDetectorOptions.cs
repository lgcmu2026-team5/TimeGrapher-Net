/* TgDetectorOptions.cs -- opt-in detection robustness options.
 *
 * NOT part of the original Qt/C++ port surface: TgTypes.cs is a frozen
 * contract, so these options enter through a TgDetector constructor overload
 * instead of new TgConfig fields. Every option defaults to OFF; with all
 * options off (or a null options reference) the pipeline is bit-identical to
 * the V5.x port, which the golden-master and parity tests pin.
 */

namespace TimeGrapher.Core.Detection;

/// <summary>
/// Opt-in robustness options for weak-signal / high-noise environments.
/// All defaults are off; <see cref="Robust"/> returns the measured preset.
/// </summary>
public sealed record TgDetectorOptions
{
    /* I-1 AdaptiveFloor: rejected-peak shadow statistics let the reference
     * peak adapt DOWN to a weak watch, and a time decay releases a reference
     * latched high by a loud episode. */
    public bool EnableAdaptiveFloor { get; init; }
    /// <summary>Rejected bursts enter the shadow ring only above this multiple of the noise floor.</summary>
    public double RejectedPeakMinSnr { get; init; } = 2.0;
    /// <summary>Shadow-ring fill required before the floor may adapt down.</summary>
    public int RejectedPeakMinCount { get; init; } = 8;
    /// <summary>Lower bound of the adapted floor, as a multiple of the noise floor.</summary>
    public double AdaptiveFloorMinMul { get; init; } = 3.0;
    /// <summary>Seconds without an accepted burst before the reference peak starts decaying.</summary>
    public double RefDecayAfterS { get; init; } = 2.0;
    /// <summary>Exponential time constant of the reference-peak decay, seconds.</summary>
    public double RefDecayTauS { get; init; } = 5.0;

    /* I-3 RegimeGuard: the instantaneous regime trip becomes a run-of-N
     * persistence counter so a single impulse cannot flush the detector. */
    public bool EnableRegimeGuard { get; init; }
    /// <summary>
    /// Consecutive qualifying peaks required to trip a regime reset. Valid
    /// range [1, 8]: the run counter cannot exceed the 8-entry regime ring
    /// (a sustained gain step floods the ring and stops qualifying after 8
    /// beats), so the applying constructor clamps to that range.
    /// </summary>
    public int RegimeTripBeats { get; init; } = 3;

    /* I-4 support: record the PLL phase-match verdict per emitted event so an
     * engine-level gate can veto unmatched events before they reach metrics.
     * Diagnostics only; detector output is unchanged. */
    public bool TrackEventPllMatch { get; init; }

    /// <summary>
    /// The robustness preset wired to the app toggle and the verifier's
    /// robust profile. Composition frozen by A/B measurement.
    /// </summary>
    public static TgDetectorOptions Robust() => new()
    {
        EnableAdaptiveFloor = true,
        EnableRegimeGuard = true,
    };
}
