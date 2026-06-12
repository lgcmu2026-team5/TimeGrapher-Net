// Standing port-fidelity gate: streams every adverse scenario through two
// engines in the same process - DetectorOptions = null (the original
// pipeline) and an all-off TgDetectorOptions instance - and asserts
// block-for-block equality of the emitted events, threshold diagnostics, and
// metrics text. Complements the golden-master tests: the golden master pins
// the null arm against pre-change absolute values (catching always-on drift),
// while this check proves the all-off options path adds nothing, immune to
// per-platform libm differences because both arms run in one binary.

using System.Globalization;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Sim;

namespace TimeGrapher.Verify;

internal static class FidelityCheck
{
    private const int BlockSize = 4096;

    /// <summary>Runs the check over all adverse rows; false on any mismatch.</summary>
    internal static bool Run(TextWriter output)
    {
        bool allOk = true;
        foreach (AdverseScenario row in AdverseScenarios.Rows)
        {
            bool ok = RunRow(row, out int blocks, out int events);
            allOk &= ok;
            output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "FIDELITY {0}: identical={1} blocks={2} events={3}",
                row.Name, ok ? "true" : "FALSE", blocks, events));
        }
        return allOk;
    }

    private static bool RunRow(AdverseScenario row, out int blocks, out int events)
    {
        blocks = 0;
        events = 0;

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

        DetectorMetricsEngine NewEngine(TgDetectorOptions? options) =>
            new(new DetectorMetricsEngineConfig(
                SampleRate: row.SampleRate,
                LiftAngle: 52.0,
                AveragingPeriod: 2,
                UseCOnset: false,
                AutoBph: true,
                ManualBph: 0,
                HpfCutoffHz: 0.0,
                DetectorOptions: options));

        var synth = new WatchSynthStream(synthConfig);
        DetectorMetricsEngine engineNull = NewEngine(null);
        DetectorMetricsEngine engineAllOff = NewEngine(new TgDetectorOptions());

        var block = new float[BlockSize];
        long generated = 0;
        long totalSamples = (long)row.SampleRate * row.Seconds;
        long gainStepSample = row.GainStepAtS > 0.0
            ? (long)(row.GainStepAtS * row.SampleRate)
            : long.MaxValue;

        int silenceRemaining = row.SilenceLeadInSamples;
        while (silenceRemaining > 0)
        {
            int slice = Math.Min(block.Length, silenceRemaining);
            Array.Clear(block, 0, slice);
            if (!ProcessAndCompare(engineNull, engineAllOff, block.AsSpan(0, slice), ref blocks, ref events))
            {
                return false;
            }
            silenceRemaining -= slice;
        }

        while (generated < totalSamples)
        {
            int slice = (int)Math.Min(block.Length, totalSamples - generated);
            Span<float> span = block.AsSpan(0, slice);
            synth.Generate(span);
            for (int i = 0; i < slice; i++)
            {
                if (generated + i >= gainStepSample)
                {
                    span[i] *= (float)row.GainStepFactor;
                }
            }
            if (!ProcessAndCompare(engineNull, engineAllOff, span, ref blocks, ref events))
            {
                return false;
            }
            generated += slice;
        }

        /* End-of-stream drain: Flush routes through the engine's
         * endOfStream path, so the equality must hold there too. */
        DetectorMetricsBlockUpdate flushNull = engineNull.Flush();
        DetectorMetricsBlockUpdate flushAllOff = engineAllOff.Flush();
        blocks++;
        events += flushNull.Events.Count;
        return BlocksIdentical(flushNull, flushAllOff);
    }

    private static bool ProcessAndCompare(
        DetectorMetricsEngine engineNull, DetectorMetricsEngine engineAllOff,
        ReadOnlySpan<float> span, ref int blocks, ref int events)
    {
        DetectorMetricsBlockUpdate a = engineNull.Process(span);
        DetectorMetricsBlockUpdate b = engineAllOff.Process(span);
        blocks++;
        events += a.Events.Count;
        return BlocksIdentical(a, b);
    }

    private static bool BlocksIdentical(DetectorMetricsBlockUpdate a, DetectorMetricsBlockUpdate b)
    {
        DetectorResultSnapshot ra = a.Result;
        DetectorResultSnapshot rb = b.Result;
        if (ra.SyncStatus != rb.SyncStatus
            || ra.DetectedBph != rb.DetectedBph
            || ra.MeasuredPeriodS != rb.MeasuredPeriodS
            || ra.SyncLostEvent != rb.SyncLostEvent
            || ra.SyncAcquiredEvent != rb.SyncAcquiredEvent
            || ra.DetectorResetEvent != rb.DetectorResetEvent
            || ra.OnsetThreshold != rb.OnsetThreshold
            || ra.MinPeakThreshold != rb.MinPeakThreshold
            || ra.NoiseFloor != rb.NoiseFloor
            || ra.ReferencePeak != rb.ReferencePeak
            || ra.MissedBeats != rb.MissedBeats
            || ra.SyncLossCount != rb.SyncLossCount)
        {
            return false;
        }

        if (a.Events.Count != b.Events.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Events.Count; i++)
        {
            DetectedEventUpdate ea = a.Events[i];
            DetectedEventUpdate eb = b.Events[i];
            if (ea.EventSample != eb.EventSample
                || ea.Event.Type != eb.Event.Type
                || ea.Event.SampleIndex != eb.Event.SampleIndex
                || ea.Event.SubSampleOffset != eb.Event.SubSampleOffset
                || ea.Event.PeakValue != eb.Event.PeakValue
                || ea.Event.OnsetSampleIndex != eb.Event.OnsetSampleIndex
                || ea.Event.OnsetValid != eb.Event.OnsetValid
                || ea.MetricsUpdate.ResultsUpdated != eb.MetricsUpdate.ResultsUpdated
                || ea.MetricsUpdate.ResultsText != eb.MetricsUpdate.ResultsText)
            {
                return false;
            }
        }
        return true;
    }
}
