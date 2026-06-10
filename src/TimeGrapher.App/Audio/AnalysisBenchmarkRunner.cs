using System.Globalization;
using TimeGrapher.App.Services;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Shared;
using TimeGrapher.Core.Sim;

namespace TimeGrapher.App.Audio;

internal static class AnalysisBenchmarkRunner
{
    private const int DefaultBph = 43200;
    private const int DefaultRate = 192000;
    private const int DefaultDurationMs = 10000;
    private const int BlockSize = 4096;

    public static int Run(string[] args, string? analysisLogPath)
    {
        int bph = AudioSmokeRunner.ParsePositiveOption(args, "--bph", DefaultBph);
        int sampleRate = AudioSmokeRunner.ParsePositiveOption(args, "--rate", DefaultRate);
        int durationMs = AudioSmokeRunner.ParsePositiveOption(args, "--duration-ms", DefaultDurationMs);

        var rawAudio = new MasterAudioBuffer(sampleRate);
        var worker = new AnalysisWorker(rawAudio, new AnalysisWorker.Config
        {
            SampleRate = sampleRate,
            LiftAngle = 52.0,
            AveragingPeriod = 2,
            AutoBph = true,
            ManualBph = 0,
            HpfCutoffHz = 0.0,
            SoundImageWidth = 1019,
            SoundImageHeight = 654,
            ScopeSnapshotPointBudget = 8000,
            SessionId = 1,
        });

        using AnalysisPerformanceLogger? logger = analysisLogPath is null
            ? null
            : new AnalysisPerformanceLogger(analysisLogPath);

        var summary = new BenchmarkSummary();
        worker.AnalysisFrameReady += frame =>
        {
            logger?.Observe(frame);
            summary.Observe(frame);
        };

        WatchSynthStreamConfig synthConfig = WatchSynthStreamConfig.Realistic();
        synthConfig.SampleRateHz = (uint)sampleRate;
        synthConfig.Bph = bph;
        synthConfig.PcmPeakAmplitude = 0.35;
        synthConfig.NoisePeakAmplitude = 0.0;

        var synth = new WatchSynthStream(synthConfig);
        var block = new float[BlockSize];
        int totalSamples = (int)Math.Ceiling(sampleRate * (durationMs / 1000.0));
        int remaining = totalSamples;
        while (remaining > 0)
        {
            int count = Math.Min(block.Length, remaining);
            Span<float> span = block.AsSpan(0, count);
            synth.Generate(span);
            rawAudio.WriteSamples(span);
            worker.HandleInputData();
            remaining -= count;
        }

        worker.CompleteInput(Timeout.InfiniteTimeSpan);
        worker.Dispose();
        summary.Print(bph, sampleRate, durationMs, analysisLogPath);
        return summary.FrameCount > 0 && summary.DetectedBph == bph ? 0 : 1;
    }

    private sealed class BenchmarkSummary
    {
        private readonly List<double> _processingElapsedMs = new();
        private double _processingTotalMs;

        public int FrameCount => _processingElapsedMs.Count;
        public int DetectedBph { get; private set; }
        public double MaxLagMs { get; private set; }
        public int MaxDeadlineLevel { get; private set; }

        public void Observe(AnalysisFrame frame)
        {
            _processingElapsedMs.Add(frame.ProcessingElapsedMs);
            _processingTotalMs += frame.ProcessingElapsedMs;

            int sampleRate = Math.Max(1, frame.SampleRate);
            MaxLagMs = Math.Max(MaxLagMs, frame.AnalysisLagSamples * 1000.0 / sampleRate);
            MaxDeadlineLevel = Math.Max(MaxDeadlineLevel, frame.DeadlineDegradationLevel);
            if (frame.MetricsHistory is { Bph: > 0 } history)
            {
                DetectedBph = history.Bph;
            }
            else if (frame.MetricsUpdate.BeatTimingSampleUpdated)
            {
                DetectedBph = frame.MetricsUpdate.BeatTimingSample.Bph;
            }
        }

        public void Print(int configuredBph, int sampleRate, int durationMs, string? analysisLogPath)
        {
            _processingElapsedMs.Sort();
            double average = FrameCount == 0 ? 0.0 : _processingTotalMs / FrameCount;
            double p95 = Percentile(0.95);
            double max = FrameCount == 0 ? 0.0 : _processingElapsedMs[^1];
            double audioDurationMs = durationMs;
            double processingRatio = audioDurationMs <= 0.0 ? 0.0 : _processingTotalMs / audioDurationMs;
            double beatPeriodMs = 3600.0 * 1000.0 / configuredBph;

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "analysis_benchmark bph={0} sample_rate={1} duration_ms={2} frames={3} detected_bph={4}",
                configuredBph,
                sampleRate,
                durationMs,
                FrameCount,
                DetectedBph));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "processing_ms avg={0:F3} p95={1:F3} max={2:F3} total={3:F3}",
                average,
                p95,
                max,
                _processingTotalMs));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "budget beat_period_ms={0:F3} processing_to_audio_ratio={1:P2} max_lag_ms={2:F3} max_deadline_level={3}",
                beatPeriodMs,
                processingRatio,
                MaxLagMs,
                MaxDeadlineLevel));

            if (analysisLogPath != null)
            {
                Console.WriteLine("analysis_log=" + analysisLogPath);
            }
        }

        private double Percentile(double percentile)
        {
            if (_processingElapsedMs.Count == 0)
            {
                return 0.0;
            }

            int index = (int)Math.Ceiling(percentile * _processingElapsedMs.Count) - 1;
            index = Math.Clamp(index, 0, _processingElapsedMs.Count - 1);
            return _processingElapsedMs[index];
        }
    }
}
