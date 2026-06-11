// Headless verification harness: analyses sample WAV files through the ported
// detection/metrics pipeline (no threads) and checks the detected BPH against the
// filename. Mirrors TAnalysisWorker's per-event handling, run synchronously.
//
// Usage:
//   TimeGrapher.Verify <wav-or-dir> [<wav-or-dir> ...]
// A directory argument is expanded to its *.wav files.
//   TimeGrapher.Verify --generated --byte-fixtures
// Adds deterministic generated and byte-built WAV fixtures for CI.

using System.Globalization;
using System.Text.RegularExpressions;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.AudioIo;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Metrics;
using TimeGrapher.Core.Sim;

const int DetectorNumberOfSamples = 4096;

// Collect WAV paths: a directory argument expands to its *.wav files.
var files = new List<string>();
var generatedFiles = new List<string>();
foreach (string arg in args)
{
    if (arg == "--generated")
    {
        generatedFiles.AddRange(GenerateSyntheticFixtures());
        continue;
    }

    if (arg == "--byte-fixtures")
    {
        generatedFiles.AddRange(GenerateByteBuiltFixtures());
        continue;
    }

    if (Directory.Exists(arg))
    {
        files.AddRange(Directory.GetFiles(arg, "*.wav").OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }
    else
    {
        files.Add(arg);
    }
}
files.AddRange(generatedFiles);

if (files.Count == 0)
{
    Console.Error.WriteLine("TimeGrapher.Verify: no WAV files specified");
    return 1;
}

bool allMatch = true;

try
{
    foreach (string file in files)
    {
        WavData wav = WavFileReader.ReadMonoFloat(file, WavAcceptanceProfile.PlaybackFloatMonoStandardRates);

        var engine = new DetectorMetricsEngine(new DetectorMetricsEngineConfig(
            SampleRate: wav.SampleRate,
            LiftAngle: 52.0,
            AveragingPeriod: 2,
            UseCOnset: false,
            AutoBph: true,
            ManualBph: 0,
            HpfCutoffHz: 0.0));

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
            DetectorMetricsBlockUpdate update = engine.Process(block);
            DetectorResultSnapshot result = update.Result;

            detectedBph = result.DetectedBph;
            syncStatus = result.SyncStatus;

            for (int i = 0; i < update.Events.Count; i++)
            {
                if (update.Events[i].MetricsUpdate.ResultsUpdated)
                {
                    resultsText = update.Events[i].MetricsUpdate.ResultsText;
                }
            }

            offset += slice;
        }

        // Drain the envelope delay line at end-of-stream.
        DetectorMetricsBlockUpdate flushUpdate = engine.Flush();
        detectedBph = flushUpdate.Result.DetectedBph;
        syncStatus = flushUpdate.Result.SyncStatus;
        for (int i = 0; i < flushUpdate.Events.Count; i++)
        {
            if (flushUpdate.Events[i].MetricsUpdate.ResultsUpdated)
            {
                resultsText = flushUpdate.Events[i].MetricsUpdate.ResultsText;
            }
        }

        string name = Path.GetFileName(file);
        // BuildResults wraps live values in ValueSpanStart/End markers that the
        // GUI strips before display; this console report is a display surface too.
        string cleanResults = resultsText
            .Replace(WatchMetrics.ValueSpanStart.ToString(), "")
            .Replace(WatchMetrics.ValueSpanEnd.ToString(), "");
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0}: detected_bph={1} sync_status={2} results=[{3}]",
            name, detectedBph, syncStatus, cleanResults));

        if (syncStatus != TgSyncStatus.Synced)
        {
            allMatch = false;
            Console.Error.WriteLine("  MISMATCH: expected sync_status=Synced, detected " + syncStatus);
        }

        if (string.IsNullOrWhiteSpace(resultsText))
        {
            allMatch = false;
            Console.Error.WriteLine("  MISMATCH: no metrics result text was produced");
        }

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
            allMatch = false;
            Console.Error.WriteLine("  no expected BPH in filename: " + name);
        }
    }
}
finally
{
    foreach (string generatedFile in generatedFiles)
    {
        try
        {
            File.Delete(generatedFile);
        }
        catch (IOException)
        {
        }
    }

    // Each generator writes into its own unique temp directory; remove the
    // now-empty directories too (Delete is non-recursive, so a directory that
    // still holds an undeletable file is left behind with it).
    foreach (string? dir in generatedFiles.Select(Path.GetDirectoryName).Distinct())
    {
        if (dir == null)
        {
            continue;
        }

        try
        {
            Directory.Delete(dir);
        }
        catch (IOException)
        {
        }
    }
}

return allMatch ? 0 : 1;

static IEnumerable<string> GenerateSyntheticFixtures()
{
    string dir = Path.Combine(Path.GetTempPath(), "timegrapher-verify-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    (int Bph, int SampleRate, int Seconds, double PcmPeak, double NoisePeak, string Name)[] cases =
    {
        (18000, 48000, 10, 0.40, 0.00, "clean"),
        (21600, 48000, 10, 0.18, 0.02, "noisy-lowamp"),
        (28800, 96000, 8, 0.40, 0.00, "highrate"),
        (36000, 48000, 10, 0.35, 0.01, "edge"),
        (43200, 192000, 6, 0.35, 0.00, "max-standard-rate"),
    };

    foreach ((int bph, int sampleRate, int seconds, double pcmPeak, double noisePeak, string name) in cases)
    {
        string path = Path.Combine(dir, string.Format(
            CultureInfo.InvariantCulture,
            "{0}BPH_{1}_{2}Hz_generated.wav",
            bph,
            name,
            sampleRate));
        WriteSyntheticWav(path, bph, sampleRate, seconds, pcmPeak, noisePeak);
        yield return path;
    }

    (int Bph, int SampleRate, int Seconds, double PcmPeak, double NoisePeak, int SilenceLeadInSamples, bool Clip, bool Realistic, string Name)[] edgeCases =
    {
        (21600, 48000, 12, 0.30, 0.010, 0, false, true, "realistic-noisy"),
        (18000, 48000, 10, 0.95, 0.002, 0, true, false, "clipped"),
        (28800, 96000, 8, 0.35, 0.012, 0, false, true, "asymmetric-noisy"),
    };

    foreach ((int bph, int sampleRate, int seconds, double pcmPeak, double noisePeak, int silenceLeadInSamples, bool clip, bool realistic, string name) in edgeCases)
    {
        string path = Path.Combine(dir, string.Format(
            CultureInfo.InvariantCulture,
            "{0}BPH_{1}_{2}Hz_edge.wav",
            bph,
            name,
            sampleRate));
        WriteSyntheticWav(path, bph, sampleRate, seconds, pcmPeak, noisePeak, silenceLeadInSamples, clip, realistic);
        yield return path;
    }
}

static IEnumerable<string> GenerateByteBuiltFixtures()
{
    string dir = Path.Combine(Path.GetTempPath(), "timegrapher-verify-byte-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    (int Bph, int SampleRate, int Seconds, double PcmPeak, double NoisePeak, bool Extensible, bool OddJunk, bool ListChunk, string Name)[] cases =
    {
        (18000, 48000, 10, 0.40, 0.00, false, true, true, "riff-junk"),
        (21600, 48000, 10, 0.25, 0.01, true, true, false, "extensible"),
        (28800, 96000, 8, 0.35, 0.00, true, false, true, "extensible-list"),
    };

    foreach ((int bph, int sampleRate, int seconds, double pcmPeak, double noisePeak, bool extensible, bool oddJunk, bool listChunk, string name) in cases)
    {
        string path = Path.Combine(dir, string.Format(
            CultureInfo.InvariantCulture,
            "{0}BPH_{1}_{2}Hz_bytefixture.wav",
            bph,
            name,
            sampleRate));
        WriteByteBuiltSyntheticWav(path, bph, sampleRate, seconds, pcmPeak, noisePeak, extensible, oddJunk, listChunk);
        yield return path;
    }
}

static void WriteSyntheticWav(
    string path,
    int bph,
    int sampleRate,
    int seconds,
    double pcmPeak,
    double noisePeak,
    int silenceLeadInSamples = 0,
    bool hardClip = false,
    bool realistic = false)
{
    WatchSynthStreamConfig synthConfig = realistic
        ? WatchSynthStreamConfig.Realistic()
        : WatchSynthStreamConfig.Clean();
    synthConfig.SampleRateHz = (uint)sampleRate;
    synthConfig.Bph = bph;
    synthConfig.NoisePeakAmplitude = noisePeak;
    synthConfig.PcmPeakAmplitude = pcmPeak;

    var synth = new WatchSynthStream(synthConfig);
    using var writer = new WavStreamWriter();
    if (!writer.Open(path, sampleRate, channels: 1))
    {
        throw new IOException("Failed to open generated WAV file: " + path);
    }

    var block = new float[4096];
    int silenceRemaining = silenceLeadInSamples;
    while (silenceRemaining > 0)
    {
        int slice = Math.Min(block.Length, silenceRemaining);
        Span<float> span = block.AsSpan(0, slice);
        span.Clear();
        if (!writer.Write(span))
        {
            throw new IOException("Failed to write generated WAV file: " + path);
        }
        silenceRemaining -= slice;
    }

    int remaining = sampleRate * seconds;
    while (remaining > 0)
    {
        int slice = Math.Min(block.Length, remaining);
        Span<float> span = block.AsSpan(0, slice);
        synth.Generate(span);
        if (hardClip)
        {
            for (int i = 0; i < slice; i++)
            {
                span[i] = Math.Clamp(span[i], -0.35f, 0.35f);
            }
        }
        if (!writer.Write(span))
        {
            throw new IOException("Failed to write generated WAV file: " + path);
        }
        remaining -= slice;
    }

    if (!writer.Close())
    {
        throw new IOException("Failed to close generated WAV file: " + path);
    }
}

static void WriteByteBuiltSyntheticWav(
    string path,
    int bph,
    int sampleRate,
    int seconds,
    double pcmPeak,
    double noisePeak,
    bool extensible,
    bool oddJunk,
    bool listChunk)
{
    WatchSynthStreamConfig synthConfig = WatchSynthStreamConfig.Clean();
    synthConfig.SampleRateHz = (uint)sampleRate;
    synthConfig.Bph = bph;
    synthConfig.NoisePeakAmplitude = noisePeak;
    synthConfig.PcmPeakAmplitude = pcmPeak;

    var synth = new WatchSynthStream(synthConfig);
    int sampleCount = sampleRate * seconds;
    uint dataSize = checked((uint)(sampleCount * sizeof(float)));
    uint fmtSize = extensible ? 40u : 16u;
    uint junkPayloadSize = oddJunk ? 3u : 0u;
    uint junkChunkSize = oddJunk ? 8u + junkPayloadSize + 1u : 0u;
    uint listPayloadSize = listChunk ? 4u : 0u;
    uint listTotalSize = listChunk ? 8u + listPayloadSize : 0u;
    uint riffSize = 4u + 8u + fmtSize + junkChunkSize + listTotalSize + 8u + dataSize;

    using FileStream stream = File.Create(path);
    using var writer = new BinaryWriter(stream);

    WriteFourCc(writer, "RIFF");
    writer.Write(riffSize);
    WriteFourCc(writer, "WAVE");

    if (oddJunk)
    {
        WriteFourCc(writer, "JUNK");
        writer.Write(junkPayloadSize);
        writer.Write(new byte[] { 0x10, 0x20, 0x30 });
        writer.Write((byte)0);
    }

    WriteFourCc(writer, "fmt ");
    writer.Write(fmtSize);
    writer.Write(extensible ? WavProbe.WaveFormatExtensible : WavProbe.WaveFormatIeeeFloat);
    writer.Write((ushort)1);
    writer.Write((uint)sampleRate);
    writer.Write((uint)(sampleRate * sizeof(float)));
    writer.Write((ushort)sizeof(float));
    writer.Write((ushort)32);
    if (extensible)
    {
        writer.Write((ushort)22);
        writer.Write((ushort)32);
        writer.Write((uint)0);
        writer.Write(WavProbe.WaveFormatIeeeFloat);
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
    }

    if (listChunk)
    {
        WriteFourCc(writer, "LIST");
        writer.Write(listPayloadSize);
        WriteFourCc(writer, "INFO");
    }

    WriteFourCc(writer, "data");
    writer.Write(dataSize);

    var block = new float[4096];
    int remaining = sampleCount;
    while (remaining > 0)
    {
        int slice = Math.Min(block.Length, remaining);
        Span<float> span = block.AsSpan(0, slice);
        synth.Generate(span);
        for (int i = 0; i < slice; i++)
        {
            writer.Write(BitConverter.SingleToInt32Bits(span[i]));
        }
        remaining -= slice;
    }
}

static void WriteFourCc(BinaryWriter writer, string fourCc)
{
    writer.Write(new[] { (byte)fourCc[0], (byte)fourCc[1], (byte)fourCc[2], (byte)fourCc[3] });
}
