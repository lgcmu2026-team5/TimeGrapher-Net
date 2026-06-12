using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Sim;
using Xunit;

namespace TimeGrapher.Core.Tests;

/// <summary>
/// Golden-master pins for the unmodified detection pipeline (record/playback
/// testability tactic). Two deterministic synthetic fixtures are streamed
/// through TgDetector with the library-default config, and the absolute event
/// stream (first 40 events) plus the final threshold diagnostics are pinned to
/// values recorded BEFORE any opt-in robustness work landed. Any unconditional
/// (always-on) behavior change in the detection chain fails here, including
/// drift that same-binary A/B comparisons cannot see because it shifts both
/// arms identically.
///
/// Integer fields (Type, SampleIndex) are pinned exactly; sub-sample offsets
/// and float diagnostics use a 1e-6 absolute tolerance to absorb per-platform
/// libm ulp differences.
/// </summary>
public sealed class DetectorGoldenMasterTests
{
    private const double Tolerance = 1e-6;

    private sealed record GoldenRun(
        IReadOnlyList<TgEvent> Events,
        float OnsetThreshold,
        float MinPeakThreshold,
        float NoiseFloor,
        float ReferencePeak,
        int FlushEventCount,
        float FlushOnsetThreshold,
        float FlushMinPeakThreshold,
        float FlushNoiseFloor,
        float FlushReferencePeak);

    private static GoldenRun Run(bool clean)
    {
        WatchSynthStreamConfig synthConfig = clean
            ? WatchSynthStreamConfig.Clean()
            : WatchSynthStreamConfig.Realistic();
        synthConfig.SampleRateHz = 48000;
        synthConfig.Bph = 21600;
        synthConfig.PcmPeakAmplitude = clean ? 0.40 : 0.30;
        synthConfig.NoisePeakAmplitude = clean ? 0.0 : 0.01;

        var synth = new WatchSynthStream(synthConfig);
        var detector = new TgDetector(TgConfig.Default());
        var result = new TgResult();

        var events = new List<TgEvent>();
        float onsetThr = 0, minPeakThr = 0, noise = 0, refPeak = 0;

        var block = new float[4096];
        int remaining = 48000 * (clean ? 10 : 12);
        while (remaining > 0)
        {
            int slice = Math.Min(block.Length, remaining);
            Span<float> span = block.AsSpan(0, slice);
            synth.Generate(span);
            detector.Process(span, result);
            events.AddRange(result.Events);
            onsetThr = result.OnsetThreshold;
            minPeakThr = result.MinPeakThreshold;
            noise = result.NoiseFloor;
            refPeak = result.ReferencePeak;
            remaining -= slice;
        }

        /* End-of-stream drain: pinned separately so an always-on drift
         * confined to the flush tail also fails the golden master. */
        detector.Flush(result);

        return new GoldenRun(events, onsetThr, minPeakThr, noise, refPeak,
            result.Events.Count, result.OnsetThreshold, result.MinPeakThreshold,
            result.NoiseFloor, result.ReferencePeak);
    }

    private static void AssertGolden(
        GoldenRun run,
        int expectedTotalEvents,
        (TgEventType Type, ulong SampleIndex, double SubSampleOffset)[] expectedEvents,
        float onsetThr, float minPeakThr, float noiseFloor, float refPeak,
        float flushOnsetThr, float flushMinPeakThr, float flushNoiseFloor, float flushRefPeak)
    {
        Assert.Equal(0, run.FlushEventCount);
        Assert.True(Math.Abs(flushOnsetThr - run.FlushOnsetThreshold) <= Tolerance,
            $"FlushOnsetThreshold {run.FlushOnsetThreshold} != {flushOnsetThr}");
        Assert.True(Math.Abs(flushMinPeakThr - run.FlushMinPeakThreshold) <= Tolerance,
            $"FlushMinPeakThreshold {run.FlushMinPeakThreshold} != {flushMinPeakThr}");
        Assert.True(Math.Abs(flushNoiseFloor - run.FlushNoiseFloor) <= Tolerance,
            $"FlushNoiseFloor {run.FlushNoiseFloor} != {flushNoiseFloor}");
        Assert.True(Math.Abs(flushRefPeak - run.FlushReferencePeak) <= Tolerance,
            $"FlushReferencePeak {run.FlushReferencePeak} != {flushRefPeak}");

        Assert.Equal(expectedTotalEvents, run.Events.Count);
        for (int i = 0; i < expectedEvents.Length; i++)
        {
            TgEvent actual = run.Events[i];
            Assert.Equal(expectedEvents[i].Type, actual.Type);
            Assert.Equal(expectedEvents[i].SampleIndex, actual.SampleIndex);
            Assert.True(
                Math.Abs(expectedEvents[i].SubSampleOffset - actual.SubSampleOffset) <= Tolerance,
                $"event {i}: SubSampleOffset {actual.SubSampleOffset} != {expectedEvents[i].SubSampleOffset}");
        }

        Assert.True(Math.Abs(onsetThr - run.OnsetThreshold) <= Tolerance,
            $"OnsetThreshold {run.OnsetThreshold} != {onsetThr}");
        Assert.True(Math.Abs(minPeakThr - run.MinPeakThreshold) <= Tolerance,
            $"MinPeakThreshold {run.MinPeakThreshold} != {minPeakThr}");
        Assert.True(Math.Abs(noiseFloor - run.NoiseFloor) <= Tolerance,
            $"NoiseFloor {run.NoiseFloor} != {noiseFloor}");
        Assert.True(Math.Abs(refPeak - run.ReferencePeak) <= Tolerance,
            $"ReferencePeak {run.ReferencePeak} != {refPeak}");
    }

    [Fact]
    public void Clean21600_EventStreamMatchesGoldenMaster()
    {
        AssertGolden(
            Run(clean: true),
            expectedTotalEvents: 118,
            expectedEvents: new (TgEventType, ulong, double)[]
            {
                (TgEventType.A, 10402, 0.31943682848917854),
                (TgEventType.C, 10892, 0.3047595475464386),
                (TgEventType.A, 18402, 0.32478077500923935),
                (TgEventType.C, 18892, 0.31676012239867013),
                (TgEventType.A, 26402, 0.4305301649863323),
                (TgEventType.C, 26892, 0.3047595475464386),
                (TgEventType.A, 34402, 0.36310208971309016),
                (TgEventType.C, 34892, 0.31676012239867013),
                (TgEventType.A, 42402, 0.41014648651587154),
                (TgEventType.C, 42892, 0.3047595475464386),
                (TgEventType.A, 50402, 0.32478077500923935),
                (TgEventType.C, 50892, 0.31676012239867013),
                (TgEventType.A, 58402, 0.41014648651587154),
                (TgEventType.C, 58892, 0.3047595475464386),
                (TgEventType.A, 66402, 0.32478077500923935),
                (TgEventType.C, 66892, 0.31676012239867013),
                (TgEventType.A, 74402, 0.41014648651587154),
                (TgEventType.C, 74892, 0.3047595475464386),
                (TgEventType.A, 82402, 0.32478077500923935),
                (TgEventType.C, 82892, 0.31676012239867013),
                (TgEventType.A, 90402, 0.41014648651587154),
                (TgEventType.C, 90892, 0.3047595475464386),
                (TgEventType.A, 98402, 0.32478077500923935),
                (TgEventType.C, 98892, 0.31676012239867013),
                (TgEventType.A, 106402, 0.41014648651587154),
                (TgEventType.C, 106892, 0.3047595475464386),
                (TgEventType.A, 114402, 0.32478077500923935),
                (TgEventType.C, 114892, 0.31676012239867013),
                (TgEventType.A, 122402, 0.41014648651587154),
                (TgEventType.C, 122892, 0.3047595475464386),
                (TgEventType.A, 130402, 0.32478077500923935),
                (TgEventType.C, 130892, 0.31676012239867013),
                (TgEventType.A, 138402, 0.41014648651587154),
                (TgEventType.C, 138892, 0.3047595475464386),
                (TgEventType.A, 146402, 0.34394143236116476),
                (TgEventType.C, 146892, 0.31676012239867013),
                (TgEventType.A, 154402, 0.41014648651587154),
                (TgEventType.C, 154892, 0.3047595475464386),
                (TgEventType.A, 162402, 0.34394143236116476),
                (TgEventType.C, 162892, 0.31676012239867013),
            },
            onsetThr: 0.008158111f,
            minPeakThr: 0.0543874f,
            noiseFloor: 1E-09f,
            refPeak: 0.271937f,
            flushOnsetThr: 0.008158111f,
            flushMinPeakThr: 0.0543874f,
            flushNoiseFloor: 1E-09f,
            flushRefPeak: 0.271937f);
    }

    [Fact]
    public void RealisticNoisy21600_EventStreamMatchesGoldenMaster()
    {
        AssertGolden(
            Run(clean: false),
            expectedTotalEvents: 142,
            expectedEvents: new (TgEventType, ulong, double)[]
            {
                (TgEventType.A, 10405, -0.02874516844088426),
                (TgEventType.C, 10892, 0.31490978450952506),
                (TgEventType.A, 18404, -0.16422532771523957),
                (TgEventType.C, 18892, 0.3204142310098146),
                (TgEventType.A, 26404, 0.05792211193768833),
                (TgEventType.C, 26892, 0.31943267143568976),
                (TgEventType.A, 34404, 0.17346268222559252),
                (TgEventType.C, 34892, 0.33997430805113243),
                (TgEventType.A, 42405, 0.055863500617520684),
                (TgEventType.C, 42892, 0.3145286789267238),
                (TgEventType.A, 50404, -0.37367590412683427),
                (TgEventType.C, 50892, 0.33505421866463503),
                (TgEventType.A, 58404, 0.34539688333684443),
                (TgEventType.C, 58892, 0.338955873501607),
                (TgEventType.A, 66404, 0.37092408410011873),
                (TgEventType.C, 66892, 0.34878685744533466),
                (TgEventType.A, 74404, -0.14396260262218896),
                (TgEventType.C, 74892, 0.3336781826924478),
                (TgEventType.A, 82404, -0.45131624821928373),
                (TgEventType.C, 82892, 0.3252134616464911),
                (TgEventType.A, 90404, -0.08488215672403665),
                (TgEventType.C, 90892, 0.3232145047775259),
                (TgEventType.A, 98405, -0.043968194000709504),
                (TgEventType.C, 98892, 0.3429688274966608),
                (TgEventType.A, 106405, 0.2888258482182132),
                (TgEventType.C, 106892, 0.31320739294429717),
                (TgEventType.A, 114403, 0.42924449125945),
                (TgEventType.C, 114892, 0.3298976120764981),
                (TgEventType.A, 122405, -0.26762036629675756),
                (TgEventType.C, 122892, 0.3343442662254332),
                (TgEventType.A, 130403, 0.3585121003926717),
                (TgEventType.C, 130892, 0.3556777746442843),
                (TgEventType.A, 138406, -0.2089111237016884),
                (TgEventType.C, 138892, 0.32522570670027506),
                (TgEventType.A, 146404, 0.0753168833653367),
                (TgEventType.C, 146892, 0.3340881180186283),
                (TgEventType.A, 154404, 0.3650545451336901),
                (TgEventType.C, 154892, 0.31564193454614875),
                (TgEventType.A, 162404, -0.09609883603231428),
                (TgEventType.C, 162892, 0.32424573435749704),
            },
            onsetThr: 0.011252444f,
            minPeakThr: 0.047976665f,
            noiseFloor: 0.0047716987f,
            refPeak: 0.22079654f,
            flushOnsetThr: 0.01112763f,
            flushMinPeakThr: 0.047873728f,
            flushNoiseFloor: 0.004643025f,
            flushRefPeak: 0.22079654f);
    }
}
