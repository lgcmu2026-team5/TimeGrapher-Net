// Headless verification harness: analyses sample WAV files through the ported
// detection/metrics pipeline (no threads) and checks the detected BPH against the
// filename. Mirrors TAnalysisWorker's per-event handling, run synchronously.
//
// Usage:
//   TimeGrapher.Verify <wav-or-dir> [<wav-or-dir> ...]
// A directory argument is expanded to its *.wav files.

using System.Globalization;
using System.Text.RegularExpressions;
using TimeGrapher.Core.AudioIo;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Metrics;
using TimeGrapher.Core.Shared;

const int DetectorNumberOfSamples = 4096;

// Collect WAV paths: a directory argument expands to its *.wav files.
var files = new List<string>();
foreach (string arg in args)
{
    if (Directory.Exists(arg))
    {
        files.AddRange(Directory.GetFiles(arg, "*.wav"));
    }
    else
    {
        files.Add(arg);
    }
}

if (files.Count == 0)
{
    Console.Error.WriteLine("TimeGrapher.Verify: no WAV files specified");
    return 1;
}

bool allMatch = true;

foreach (string file in files)
{
    WavData wav = WavFileReader.ReadMonoFloat(file);

    var cfg = TgConfig.Default();
    cfg.SampleRate = wav.SampleRate;
    cfg.BphMode = TgBphMode.Auto;
    cfg.SuppressPreSyncEvents = true;

    var detector = new TgDetector(cfg);

    var metrics = new WatchMetrics(new WatchMetricsConfig
    {
        SampleRate = wav.SampleRate,
        LiftAngle = 52.0,
        AveragingPeriod = 2,
        MaxRateDataPoints = 250,
        RateErrorYScale = 10.0,
        RlsWindowInit = 100,
    });
    metrics.Reset();

    var result = new TgResult();
    int detectedBph = 0;
    var syncStatus = TgSyncStatus.NotSynced;
    string resultsText = "";

    float[] samples = wav.Samples;
    int total = samples.Length;
    int offset = 0;
    while (offset < total)
    {
        int slice = total - offset > DetectorNumberOfSamples
                        ? DetectorNumberOfSamples
                        : total - offset;

        var block = new ReadOnlySpan<float>(samples, offset, slice);
        detector.Process(block, result);

        detectedBph = result.DetectedBph;
        syncStatus = result.SyncStatus;

        bool synced = result.SyncStatus == TgSyncStatus.Synced;
        for (int i = 0; i < result.Events.Count; i++)
        {
            TgEvent ev = result.Events[i];
            if (ev.Type == TgEventType.A)
            {
                double value = ev.SampleIndex + ev.SubSampleOffset;
                WatchMetricsUpdate update = metrics.HandleAEvent(value, synced, detectedBph);
                if (update.ResultsUpdated)
                {
                    resultsText = update.ResultsText;
                }
            }
            else if (ev.Type == TgEventType.C)
            {
                double value = ev.SampleIndex + ev.SubSampleOffset;
                WatchMetricsUpdate update = metrics.HandleCEvent(value, synced, detectedBph);
                if (update.ResultsUpdated)
                {
                    resultsText = update.ResultsText;
                }
            }
        }

        offset += slice;
    }

    // Drain the envelope delay line at end-of-stream.
    detector.Flush(result);
    detectedBph = result.DetectedBph;
    syncStatus = result.SyncStatus;
    {
        bool synced = result.SyncStatus == TgSyncStatus.Synced;
        for (int i = 0; i < result.Events.Count; i++)
        {
            TgEvent ev = result.Events[i];
            if (ev.Type == TgEventType.A)
            {
                double value = ev.SampleIndex + ev.SubSampleOffset;
                WatchMetricsUpdate update = metrics.HandleAEvent(value, synced, detectedBph);
                if (update.ResultsUpdated)
                {
                    resultsText = update.ResultsText;
                }
            }
            else if (ev.Type == TgEventType.C)
            {
                double value = ev.SampleIndex + ev.SubSampleOffset;
                WatchMetricsUpdate update = metrics.HandleCEvent(value, synced, detectedBph);
                if (update.ResultsUpdated)
                {
                    resultsText = update.ResultsText;
                }
            }
        }
    }

    string name = Path.GetFileName(file);
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "{0}: detected_bph={1} sync_status={2} results=[{3}]",
        name, detectedBph, syncStatus, resultsText));

    // Expected BPH parsed from the filename, e.g. "21600BPH_*.wav" -> 21600.
    Match m = Regex.Match(name, @"(\d+)BPH");
    if (m.Success)
    {
        int expectedBph = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (expectedBph != detectedBph)
        {
            allMatch = false;
            Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  MISMATCH: expected {0}, detected {1}", expectedBph, detectedBph));
        }
    }
    else
    {
        Console.Error.WriteLine("  no expected BPH in filename: " + name);
    }
}

return allMatch ? 0 : 1;
