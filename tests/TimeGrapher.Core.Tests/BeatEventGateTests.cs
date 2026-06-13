using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Detection.Scoring;
using TimeGrapher.Core.Sim;
using Xunit;

namespace TimeGrapher.Core.Tests;

/// <summary>
/// Contract tests for the engine-level event-gate host using a scriptable
/// gate double: inert pass-through equals the ungated engine, vetoes remove
/// events from the metrics/display streams (but never from the raw snapshot
/// and never from the PLL), a vetoed A pair-vetoes its C, windowed gates receive
/// correctly aligned envelope windows via delayed release, Flush
/// force-releases, and sync loss resets the gate.
/// </summary>
public sealed class BeatEventGateTests
{
    private sealed class ScriptedGate : IBeatEventGate
    {
        public Func<BeatCandidate, bool> AcceptFunc = _ => true;
        public double PreMs;
        public double PostMs;
        public int ResetCount;
        public readonly List<(int Length, int Offset, TgEventType Type)> Windows = new();

        public string Name => "scripted";
        public double WindowPreMs => PreMs;
        public double WindowPostMs => PostMs;

        public bool Accept(ReadOnlySpan<float> envelopeWindow, int eventOffsetInWindow,
                           double sampleRate, in BeatCandidate candidate)
        {
            Windows.Add((envelopeWindow.Length, eventOffsetInWindow, candidate.Event.Type));
            return AcceptFunc(candidate);
        }

        public void Reset() => ResetCount++;
    }

    private static DetectorMetricsEngine NewEngine(IBeatEventGate? gate) =>
        new(new DetectorMetricsEngineConfig(
            SampleRate: 48000,
            LiftAngle: 52.0,
            AveragingPeriod: 2,
            UseCOnset: false,
            AutoBph: true,
            ManualBph: 0,
            HpfCutoffHz: 0.0,
            EventGate: gate != null ? new BeatEventGateConfig(gate) : null));

    private static WatchSynthStreamConfig CleanStream() {
        WatchSynthStreamConfig cfg = WatchSynthStreamConfig.Clean();
        cfg.SampleRateHz = 48000;
        cfg.Bph = 21600;
        cfg.PcmPeakAmplitude = 0.40;
        cfg.NoisePeakAmplitude = 0.0;
        return cfg;
    }

    private sealed record RunResult(
        List<(TgEventType Type, ulong SampleIndex)> MetricsEvents,
        List<(TgEventType Type, ulong SampleIndex)> DisplayEvents,
        List<(TgEventType Type, ulong SampleIndex)> SnapshotEvents,
        DetectorResultSnapshot FinalSnapshot);

    private static RunResult Run(DetectorMetricsEngine engine, int seconds, int silenceTailSeconds = 0)
    {
        var synth = new WatchSynthStream(CleanStream());
        var metrics = new List<(TgEventType, ulong)>();
        var display = new List<(TgEventType, ulong)>();
        var snapshot = new List<(TgEventType, ulong)>();
        var block = new float[4096];
        DetectorMetricsBlockUpdate update = default!;

        void Capture(DetectorMetricsBlockUpdate u)
        {
            foreach (DetectedEventUpdate ev in u.MetricsEvents)
            {
                metrics.Add((ev.Event.Type, ev.Event.SampleIndex));
            }
            foreach (DetectedEventUpdate ev in u.DisplayEvents)
            {
                display.Add((ev.Event.Type, ev.Event.SampleIndex));
            }
            foreach (TgEvent ev in u.Result.Events)
            {
                snapshot.Add((ev.Type, ev.SampleIndex));
            }
        }

        int remaining = 48000 * seconds;
        while (remaining > 0)
        {
            int slice = Math.Min(block.Length, remaining);
            synth.Generate(block.AsSpan(0, slice));
            update = engine.Process(block.AsSpan(0, slice));
            Capture(update);
            remaining -= slice;
        }
        remaining = 48000 * silenceTailSeconds;
        while (remaining > 0)
        {
            int slice = Math.Min(block.Length, remaining);
            Array.Clear(block, 0, slice);
            update = engine.Process(block.AsSpan(0, slice));
            Capture(update);
            remaining -= slice;
        }
        update = engine.Flush();
        Capture(update);
        return new RunResult(metrics, display, snapshot, update.Result);
    }

    [Fact]
    public void PassThroughGate_RoutesExactlyWhatTheUngatedEngineRoutes()
    {
        RunResult ungated = Run(NewEngine(null), 8);
        RunResult passThrough = Run(NewEngine(new ScriptedGate()), 8);

        Assert.Equal(ungated.MetricsEvents, passThrough.MetricsEvents);
        Assert.Equal(ungated.DisplayEvents, passThrough.DisplayEvents);
        Assert.Equal(0UL, passThrough.FinalSnapshot.VetoedEvents);
    }

    [Fact]
    public void VetoedSyncedA_NeverReachesMetrics_AndPairVetoesItsC()
    {
        var gate = new ScriptedGate
        {
            // Veto every A once synced; Cs are nominally accepted, so any C
            // missing from the metrics stream got there via the pair veto.
            AcceptFunc = c => !(c.Synced && c.Event.Type == TgEventType.A),
        };
        RunResult result = Run(NewEngine(gate), 8);

        Assert.DoesNotContain(result.MetricsEvents, e => e.Type == TgEventType.A);
        Assert.DoesNotContain(result.MetricsEvents, e => e.Type == TgEventType.C);
        Assert.DoesNotContain(result.DisplayEvents, e => e.Type == TgEventType.A);
        Assert.DoesNotContain(result.DisplayEvents, e => e.Type == TgEventType.C);
        Assert.Equal(result.MetricsEvents, result.DisplayEvents);
        Assert.True(result.FinalSnapshot.VetoedEvents > 0);

        // The raw snapshot stream still carries everything for diagnostics.
        Assert.Contains(result.SnapshotEvents, e => e.Type == TgEventType.A);
        Assert.Contains(result.SnapshotEvents, e => e.Type == TgEventType.C);

        // Structural guarantee: the gate sits after BPH/PLL, so vetoing
        // every single event cannot break lock acquisition or hold.
        Assert.Equal(TgSyncStatus.Synced, result.FinalSnapshot.SyncStatus);
    }

    [Fact]
    public void WindowedGate_ReceivesAlignedWindows_AndRoutesTheSameEvents()
    {
        var gate = new ScriptedGate { PreMs = 20.0, PostMs = 10.0 };
        RunResult ungated = Run(NewEngine(null), 8);
        RunResult windowed = Run(NewEngine(gate), 8);

        // Delayed release may shift delivery into later blocks, but the
        // metrics event sequence must be identical.
        Assert.Equal(ungated.MetricsEvents, windowed.MetricsEvents);

        Assert.NotEmpty(gate.Windows);
        int preSamples = (int)(0.020 * 48000);
        int postSamples = (int)(0.010 * 48000);
        // Away from the stream head every window is full-size with the event
        // exactly at the pre-window offset.
        foreach ((int length, int offset, TgEventType _) in gate.Windows.Skip(4))
        {
            Assert.Equal(preSamples + postSamples + 1, length);
            Assert.Equal(preSamples, offset);
        }
    }

    [Fact]
    public void OversizedWindowRequest_StillReceivesFullWindowsWithTheEventInside()
    {
        // A post-window larger than the old fixed 0.5 s ring used to evict
        // the event before its window was ready, silently handing the gate
        // truncated windows with offset = -1 (the 'no window requested'
        // sentinel). The ring is now sized from the request.
        var gate = new ScriptedGate { PreMs = 20.0, PostMs = 600.0 };
        Run(NewEngine(gate), 8);

        Assert.NotEmpty(gate.Windows);
        int preSamples = (int)(0.020 * 48000);
        int postSamples = (int)(0.600 * 48000);
        foreach ((int length, int offset, TgEventType _) in gate.Windows.Skip(4).SkipLast(8))
        {
            Assert.Equal(preSamples + postSamples + 1, length);
            Assert.Equal(preSamples, offset);
        }
    }

    [Fact]
    public void WindowedGate_DisplayDeliveryLagStaysBoundedByTheWindowAndBlock()
    {
        const int blockSize = 4096;
        const double postMs = 600.0;
        var gate = new ScriptedGate { PreMs = 20.0, PostMs = postMs };
        var engine = NewEngine(gate);
        var synth = new WatchSynthStream(CleanStream());
        var block = new float[blockSize];
        int postSamples = (int)(postMs * 1e-3 * 48000);
        double maxAllowedLag = postSamples + blockSize;
        bool sawDisplayEvent = false;

        void AssertDisplayLag(DetectorMetricsBlockUpdate update)
        {
            if (update.Result.ProcessedPcmLen == 0)
            {
                return;
            }

            double publishedThroughExclusive =
                (double)update.Result.ProcessedPcmStartSample + update.Result.ProcessedPcmLen;
            foreach (DetectedEventUpdate ev in update.DisplayEvents)
            {
                sawDisplayEvent = true;
                double lag = publishedThroughExclusive - ev.EventSample;
                Assert.True(lag <= maxAllowedLag,
                    $"display lag {lag:F0} samples exceeds {maxAllowedLag:F0}");
            }
        }

        int remaining = 48000 * 8;
        while (remaining > 0)
        {
            int slice = Math.Min(block.Length, remaining);
            synth.Generate(block.AsSpan(0, slice));
            DetectorMetricsBlockUpdate update = engine.Process(block.AsSpan(0, slice));
            AssertDisplayLag(update);
            remaining -= slice;
        }

        AssertDisplayLag(engine.Flush());
        Assert.True(sawDisplayEvent);
    }

    [Fact]
    public void Flush_DeliversTheTailNothingStaysPending()
    {
        // Cut the stream right after a beat (t = 2.3833 s) whose burst is
        // still open and whose gate window cannot be covered yet; Flush must
        // deliver it (normally or force-released), so the gated engine ends
        // with exactly the ungated engine's routed sequence.
        List<(TgEventType, ulong)> RunCut(IBeatEventGate? gate)
        {
            var engine = NewEngine(gate);
            var synth = new WatchSynthStream(CleanStream());
            var routed = new List<(TgEventType, ulong)>();
            var block = new float[4096];
            int remaining = (int)(48000 * 2.40);
            while (remaining > 0)
            {
                int slice = Math.Min(block.Length, remaining);
                synth.Generate(block.AsSpan(0, slice));
                DetectorMetricsBlockUpdate u = engine.Process(block.AsSpan(0, slice));
                foreach (DetectedEventUpdate ev in u.MetricsEvents) routed.Add((ev.Event.Type, ev.Event.SampleIndex));
                remaining -= slice;
            }
            DetectorMetricsBlockUpdate fu = engine.Flush();
            foreach (DetectedEventUpdate ev in fu.MetricsEvents) routed.Add((ev.Event.Type, ev.Event.SampleIndex));
            return routed;
        }

        List<(TgEventType, ulong)> ungated = RunCut(null);
        List<(TgEventType, ulong)> windowed = RunCut(new ScriptedGate { PreMs = 20.0, PostMs = 10.0 });

        Assert.NotEmpty(ungated);
        Assert.Equal(ungated, windowed);
    }

    [Fact]
    public void SyncLoss_ResetsTheGate()
    {
        var gate = new ScriptedGate();
        RunResult result = Run(NewEngine(gate), 6, silenceTailSeconds: 5);

        Assert.True(gate.ResetCount >= 1,
            $"gate.Reset() was called {gate.ResetCount} times; expected >= 1 after sync loss");
        Assert.NotEqual(TgSyncStatus.Synced, result.FinalSnapshot.SyncStatus);
    }
}
