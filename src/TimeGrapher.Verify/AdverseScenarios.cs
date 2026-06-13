// In-memory adverse-condition scenario rows for the headless verifier.
// Each row streams a deterministic synthetic watch (weak signal, heavy noise,
// impulse storms, gain steps) straight into the shared detector/metrics engine
// (no WAV round-trip; the RIFF parsing path stays covered by the legacy
// fixtures) and scores the detected A events against the FillF32 ground-truth
// side channel. Gated rows assert current detector quality; rows still under
// investigation report INFO-only quality numbers.

using System.Globalization;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Detection.Scoring;
using TimeGrapher.Core.Sim;

namespace TimeGrapher.Verify;

/// <summary>
/// One verification arm: default detector plus optional PLL veto gate.
/// </summary>
internal sealed record ArmSpec(string Name, bool UsePllGate)
{
    internal static ArmSpec Default { get; } = new("default", false);
    internal static ArmSpec PllGate { get; } = new("pll-gate", true);
}

/// <summary>
/// Gate set for one scenario arm. Unset members are not gated.
/// </summary>
internal sealed record AdverseGates(
    bool? MustSync = null,
    double MinRecall = double.NaN,
    double MaxRecall = double.NaN,
    double MinPrecision = double.NaN,
    int MinResets = -1,
    int MaxResets = -1,
    bool InfoOnly = false);

internal sealed record AdverseScenario(
    string Name,
    int Bph,
    int SampleRate,
    int Seconds,
    double PcmPeak,
    double NoisePeak,
    bool Realistic,
    double ImpulseRate = 0.0,
    double ImpulseAmp = 0.0,
    int SilenceLeadInSamples = 0,
    double GainStepAtS = 0.0,
    double GainStepFactor = 1.0,
    double EvalStartS = 2.0,
    AdverseGates? Default = null,
    AdverseGates? PllGate = null);

internal static class AdverseScenarios
{
    private const int BlockSize = 4096;

    /// <summary>
    /// Scenario table. Gate values are calibrated against the fixed-seed
    /// streams (see the commit body for the measured numbers); rows whose
    /// behavior sits on a knife edge stay INFO-only.
    /// </summary>
    internal static readonly AdverseScenario[] Rows =
    {
        // Weak signal (W-1/W-2 territory).
        new("weak-1", Bph: 21600, SampleRate: 48000, Seconds: 14,
            PcmPeak: 0.06, NoisePeak: 0.008, Realistic: true,
            Default: new AdverseGates(MustSync: true)),
        // Precision rises with the PLL gate but on single-digit matched
        // counts, too fragile to gate: INFO.
        new("weak-2", Bph: 18000, SampleRate: 48000, Seconds: 16,
            PcmPeak: 0.035, NoisePeak: 0.010, Realistic: false,
            Default: new AdverseGates(InfoOnly: true)),
        // Sustained broadband noise (W-7). The PLL phase can be dragged by
        // noise, so the optional PLL gate is measured separately.
        new("noisy-1", Bph: 21600, SampleRate: 48000, Seconds: 14,
            PcmPeak: 0.25, NoisePeak: 0.08, Realistic: true,
            Default: new AdverseGates(InfoOnly: true)),
        new("noisy-2", Bph: 28800, SampleRate: 48000, Seconds: 14,
            PcmPeak: 0.20, NoisePeak: 0.12, Realistic: false,
            Default: new AdverseGates(InfoOnly: true)),
        // Impulse storms (W-3 regime-reset DoS, W-5/W-8 contamination).
        // Mirrored in-process by tests/TimeGrapher.Core.Tests/
        // DetectorStressScenarioTests.cs - keep parameters and gates in sync
        // when recalibrating.
        new("impulse-dos", Bph: 21600, SampleRate: 48000, Seconds: 16,
            PcmPeak: 0.03, NoisePeak: 0.004, Realistic: false,
            ImpulseRate: 1.0, ImpulseAmp: 0.95,
            Default: new AdverseGates(MustSync: true, MaxResets: 1, MinRecall: 0.15)),
        // The optional PLL gate can raise precision here; the default detector
        // should still retain the watch.
        new("impulse-storm", Bph: 28800, SampleRate: 48000, Seconds: 16,
            PcmPeak: 0.25, NoisePeak: 0.02, Realistic: false,
            ImpulseRate: 3.0, ImpulseAmp: 0.6,
            Default: new AdverseGates(MustSync: true, MinRecall: 0.80, MinPrecision: 0.80)),
        // Loud-to-quiet gain step (W-4(b) latch-up).
        // Mirrored in-process by tests/TimeGrapher.Core.Tests/
        // DetectorStressScenarioTests.cs - keep parameters and gates in sync
        // when recalibrating.
        new("quiet-step", Bph: 21600, SampleRate: 48000, Seconds: 16,
            PcmPeak: 0.60, NoisePeak: 0.01, Realistic: false,
            GainStepAtS: 6.0, GainStepFactor: 0.13, EvalStartS: 10.0,
            Default: new AdverseGates(MustSync: true, MinRecall: 0.50)),
        // Bootstrap behind a silent lead-in (W-2/W-13 bootstrap paths). A
        // silence-collapsed noise floor may still trip the regime detector,
        // but pre-lock trips must not flush the BPH acquisition history.
        new("leadin-quiet", Bph: 21600, SampleRate: 48000, Seconds: 12,
            PcmPeak: 0.30, NoisePeak: 0.01, Realistic: false,
            SilenceLeadInSamples: 96000,
            Default: new AdverseGates(MustSync: true, MinRecall: 0.80, MaxResets: 0)),
        // No watch at all: the detector must NOT lock onto noise (false-lock
        // guard).
        new("noise-only", Bph: 21600, SampleRate: 48000, Seconds: 12,
            PcmPeak: 0.0, NoisePeak: 0.05, Realistic: false,
            Default: new AdverseGates(MustSync: false),
            PllGate: new AdverseGates(MustSync: false)),
    };

    internal sealed record RowResult(
        string Name,
        string Profile,
        TgSyncStatus SyncStatus,
        int DetectedBph,
        DetectionScorer.Score Score,
        ulong MissedBeats,
        uint SyncLossCount,
        int Resets,
        ulong Vetoed,
        string Verdict);

    /// <summary>Runs every row under one arm; false when any gated row fails.</summary>
    internal static bool Run(TextWriter output, ArmSpec arm)
    {
        bool allOk = true;
        foreach (AdverseScenario row in Rows)
        {
            RowResult result = RunRow(row, arm);
            allOk &= result.Verdict != "FAIL";
            output.WriteLine(Format(result));
        }
        return allOk;
    }

    internal static RowResult RunRow(AdverseScenario row, ArmSpec arm)
    {
        WatchSynthStreamConfig synthConfig = row.Realistic
            ? WatchSynthStreamConfig.Realistic()
            : WatchSynthStreamConfig.Clean();
        synthConfig.SampleRateHz = (uint)row.SampleRate;
        synthConfig.Bph = row.Bph;
        synthConfig.PcmPeakAmplitude = row.PcmPeak;
        synthConfig.NoisePeakAmplitude = row.NoisePeak;
        if (row.ImpulseRate > 0.0)
        {
            synthConfig.ImpulseNoiseRatePerSecond = row.ImpulseRate;
            synthConfig.ImpulseNoisePeakAmplitude = row.ImpulseAmp;
        }

        var synth = new WatchSynthStream(synthConfig);
        var engine = new DetectorMetricsEngine(new DetectorMetricsEngineConfig(
            SampleRate: row.SampleRate,
            LiftAngle: 52.0,
            AveragingPeriod: 2,
            UseCOnset: false,
            AutoBph: true,
            ManualBph: 0,
            HpfCutoffHz: 0.0,
            EventGate: arm.UsePllGate ? new BeatEventGateConfig(new PllMatchGate()) : null));

        double leadInS = (double)row.SilenceLeadInSamples / row.SampleRate;
        var truthTimes = new List<double>();
        var detectedATimes = new List<double>();
        int resets = 0;

        var block = new float[BlockSize];
        var eventBuf = new WatchSynthStreamEvent[64];
        DetectorMetricsBlockUpdate update;

        int silenceRemaining = row.SilenceLeadInSamples;
        while (silenceRemaining > 0)
        {
            int slice = Math.Min(block.Length, silenceRemaining);
            Array.Clear(block, 0, slice);
            update = engine.Process(block.AsSpan(0, slice));
            Collect(update, row.SampleRate, detectedATimes, ref resets);
            silenceRemaining -= slice;
        }

        long generated = 0;
        long totalSamples = (long)row.SampleRate * row.Seconds;
        long gainStepSample = row.GainStepAtS > 0.0
            ? (long)(row.GainStepAtS * row.SampleRate)
            : long.MaxValue;
        while (generated < totalSamples)
        {
            int slice = (int)Math.Min(block.Length, totalSamples - generated);
            Span<float> span = block.AsSpan(0, slice);
            WatchSynthStreamFillResult fill = synth.FillF32(span, eventBuf);
            for (int i = 0; i < fill.EventsWritten; i++)
            {
                truthTimes.Add(leadInS + eventBuf[i].TimeS);
            }
            for (int i = 0; i < slice; i++)
            {
                if (generated + i >= gainStepSample)
                {
                    span[i] *= (float)row.GainStepFactor;
                }
            }
            update = engine.Process(span);
            Collect(update, row.SampleRate, detectedATimes, ref resets);
            generated += slice;
        }

        update = engine.Flush();
        Collect(update, row.SampleRate, detectedATimes, ref resets);

        DetectionScorer.Score score = DetectionScorer.Match(
            truthTimes.ToArray(), detectedATimes.ToArray(),
            toleranceS: 0.005, evalStartS: leadInS + row.EvalStartS);

        DetectorResultSnapshot snapshot = update.Result;
        AdverseGates gates = arm.UsePllGate
            ? row.PllGate ?? new AdverseGates(InfoOnly: true)
            : row.Default ?? new AdverseGates(InfoOnly: true);
        string verdict = Evaluate(gates, snapshot, score, resets);

        return new RowResult(
            row.Name, arm.Name, snapshot.SyncStatus, snapshot.DetectedBph, score,
            snapshot.MissedBeats, snapshot.SyncLossCount, resets,
            snapshot.VetoedEvents, verdict);
    }

    private static void Collect(
        DetectorMetricsBlockUpdate update, int sampleRate,
        List<double> detectedATimes, ref int resets)
    {
        if (update.Result.DetectorResetEvent)
        {
            resets++;
        }
        foreach (DetectedEventUpdate ev in update.Events)
        {
            if (ev.Event.Type == TgEventType.A)
            {
                detectedATimes.Add(ev.EventSample / sampleRate);
            }
        }
    }

    private static string Evaluate(
        AdverseGates gates, DetectorResultSnapshot snapshot,
        DetectionScorer.Score score, int resets)
    {
        if (gates.InfoOnly)
        {
            return "INFO";
        }

        bool ok = true;
        if (gates.MustSync.HasValue)
        {
            ok &= gates.MustSync.Value == (snapshot.SyncStatus == TgSyncStatus.Synced);
        }
        if (!double.IsNaN(gates.MinRecall))
        {
            ok &= score.Recall >= gates.MinRecall;
        }
        if (!double.IsNaN(gates.MaxRecall))
        {
            ok &= score.Recall <= gates.MaxRecall;
        }
        if (!double.IsNaN(gates.MinPrecision))
        {
            ok &= score.Precision >= gates.MinPrecision;
        }
        if (gates.MinResets >= 0)
        {
            ok &= resets >= gates.MinResets;
        }
        if (gates.MaxResets >= 0)
        {
            ok &= resets <= gates.MaxResets;
        }
        return ok ? "PASS" : "FAIL";
    }

    private static string Format(RowResult r)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "ADV {0}: profile={1} sync={2} bph={3} recall={4:F3} precision={5:F3} " +
            "a_bias_ms={6:F3} a_rms_ms={7:F3} missed={8} sync_losses={9} resets={10} vetoed={11} verdict={12}",
            r.Name, r.Profile, r.SyncStatus, r.DetectedBph, r.Score.Recall, r.Score.Precision,
            r.Score.MedianOffsetMs, r.Score.RmsAfterOffsetMs,
            r.MissedBeats, r.SyncLossCount, r.Resets, r.Vetoed, r.Verdict);
    }
}
