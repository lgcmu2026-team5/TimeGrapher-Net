using System.Globalization;
using System.Text.RegularExpressions;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.AudioIo;
using TimeGrapher.Core.Shared;
using TimeGrapher.Core.Sim;

namespace TimeGrapher.App.Audio;

internal static class AnalysisBenchmarkRunner
{
    private const int DefaultBph = 43200;
    private const int DefaultRate = 192000;
    private const int DefaultDurationMs = 10000;
    private const int BlockSize = 4096;
    private delegate void FillBlock(Span<float> span);

    public static int Run(string[] args)
    {
        string? wavPath = ParseStringOption(args, "--wav");
        return wavPath == null
            ? RunSynthetic(args)
            : RunWav(args, wavPath);
    }

    private static int RunSynthetic(string[] args)
    {
        int bph = AudioSmokeRunner.ParsePositiveOption(args, "--bph", DefaultBph);
        int sampleRate = AudioSmokeRunner.ParsePositiveOption(args, "--rate", DefaultRate);
        int durationMs = AudioSmokeRunner.ParsePositiveOption(args, "--duration-ms", DefaultDurationMs);

        WatchSynthStreamConfig synthConfig = WatchSynthStreamConfig.Realistic();
        synthConfig.SampleRateHz = (uint)sampleRate;
        synthConfig.Bph = bph;
        synthConfig.PcmPeakAmplitude = 0.35;
        synthConfig.NoisePeakAmplitude = 0.0;

        var synth = new WatchSynthStream(synthConfig);
        int totalSamples = (int)Math.Ceiling(sampleRate * (durationMs / 1000.0));
        return RunBlocks(
            "synthetic",
            sampleRate,
            bph,
            totalSamples,
            span => synth.Generate(span));
    }

    private static int RunWav(string[] args, string wavPath)
    {
        WavData wav = WavFileReader.ReadMonoFloat(wavPath, WavAcceptanceProfile.PlaybackFloatMonoStandardRates);
        int? durationMs = ParsePositiveOptionOrNull(args, "--duration-ms");
        int sampleCount = wav.Samples.Length;
        if (durationMs is int limitMs)
        {
            sampleCount = Math.Min(sampleCount, (int)Math.Ceiling(wav.SampleRate * (limitMs / 1000.0)));
        }

        int expectedBph = ParsePositiveOptionOrNull(args, "--bph")
            ?? ParseBphFromFileName(wavPath)
            ?? 0;
        int offset = 0;

        return RunBlocks(
            Path.GetFileName(wavPath),
            wav.SampleRate,
            expectedBph,
            sampleCount,
            span =>
            {
                wav.Samples.AsSpan(offset, span.Length).CopyTo(span);
                offset += span.Length;
            });
    }

    private static int RunBlocks(
        string source,
        int sampleRate,
        int expectedBph,
        int totalSamples,
        FillBlock fillBlock)
    {
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

        var summary = new BenchmarkSummary();
        worker.AnalysisFrameReady += frame =>
        {
            summary.Observe(frame);
        };

        var block = new float[BlockSize];
        int remaining = totalSamples;
        while (remaining > 0)
        {
            int count = Math.Min(block.Length, remaining);
            Span<float> span = block.AsSpan(0, count);
            fillBlock(span);
            rawAudio.WriteSamples(span);
            worker.HandleInputData();
            remaining -= count;
        }

        worker.CompleteInput(Timeout.InfiniteTimeSpan);
        worker.Dispose();
        summary.Print(source, expectedBph, sampleRate, TotalDurationMs(totalSamples, sampleRate));
        return summary.FrameCount > 0 && (expectedBph <= 0 || summary.DetectedBph == expectedBph) ? 0 : 1;
    }

    private static string? ParseStringOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals(name, StringComparison.Ordinal))
            {
                return i + 1 < args.Length ? args[i + 1] : null;
            }

            string prefix = name + "=";
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                string value = arg[prefix.Length..];
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private static int? ParsePositiveOptionOrNull(string[] args, string name)
    {
        int sentinel = -1;
        int parsed = AudioSmokeRunner.ParsePositiveOption(args, name, sentinel);
        return parsed > 0 ? parsed : null;
    }

    private static int? ParseBphFromFileName(string path)
    {
        Match match = Regex.Match(Path.GetFileName(path), @"(\d+)BPH", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static int TotalDurationMs(int totalSamples, int sampleRate)
    {
        return (int)Math.Round(totalSamples * 1000.0 / Math.Max(1, sampleRate));
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

        public void Print(string source, int expectedBph, int sampleRate, int durationMs)
        {
            _processingElapsedMs.Sort();
            double average = FrameCount == 0 ? 0.0 : _processingTotalMs / FrameCount;
            double p95 = Percentile(0.95);
            double max = FrameCount == 0 ? 0.0 : _processingElapsedMs[^1];
            double audioDurationMs = durationMs;
            double processingRatio = audioDurationMs <= 0.0 ? 0.0 : _processingTotalMs / audioDurationMs;
            double beatPeriodMs = expectedBph > 0 ? 3600.0 * 1000.0 / expectedBph : 0.0;

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "analysis_benchmark source={0} expected_bph={1} sample_rate={2} duration_ms={3} frames={4} detected_bph={5}",
                source,
                expectedBph,
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
