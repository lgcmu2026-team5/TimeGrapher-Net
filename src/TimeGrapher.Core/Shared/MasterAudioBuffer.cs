namespace TimeGrapher.Core.Shared;

public readonly record struct MasterAudioBufferSnapshot(
    ulong TotalSamplesWritten,
    double Fps,
    double Spf,
    double Sps,
    int NumberOfAudioSamples);

public readonly record struct MasterAudioBufferReadResult(
    int SamplesCopied,
    ulong SourceSampleEnd,
    ulong OriginalPendingSamples,
    bool InputOverrun,
    ulong InputSamplesDropped,
    double Fps,
    double Spf,
    double Sps,
    int NumberOfAudioSamples);

/// <summary>
/// Port of TMasterAudioDataRaw (SharedAudio.h): a 30-second mono float ring buffer
/// shared between exactly one active input worker (writer) and the analysis worker (reader).
/// Writers and analysis reads both use <see cref="Lock"/> so a block copy cannot race with
/// ring-buffer writes. Detector work is performed after the copy, outside the lock.
/// </summary>
public sealed class MasterAudioBuffer
{
    public const int Channels = 1;
    public const int SecondsOfBuffer = 30;

    public readonly object Lock = new();

    public float[] Samples;
    public int NumberOfAudioSamples;

    public uint WriteIndex;
    public ulong TotalSamplesWritten;

    // C++: MainThrd_LastTotalSamplesWritten / MainThrd_LastWriteIndex
    // ("MainThrd" historically; owned by the analysis worker in this port.)
    public ulong AnalysisLastTotalSamplesWritten;
    public uint AnalysisLastWriteIndex;

    // Input-side throughput stats displayed in the status bar.
    public double Fps;
    public double Spf;
    public double Sps;

    public MasterAudioBuffer(int sampleRate)
    {
        NumberOfAudioSamples = sampleRate * SecondsOfBuffer;
        Samples = new float[NumberOfAudioSamples];
    }

    /// <summary>Ring-write a block of mono float samples (input worker thread).</summary>
    public void WriteSamples(ReadOnlySpan<float> data)
    {
        lock (Lock)
        {
            for (int i = 0; i < data.Length; i++)
            {
                Samples[WriteIndex] = data[i];
                WriteIndex = (WriteIndex + 1) % (uint)NumberOfAudioSamples;
            }
            TotalSamplesWritten += (ulong)data.Length;
        }
    }

    /// <summary>Update input-side throughput stats (input worker thread).</summary>
    public void SetStats(double fps, double spf, double sps)
    {
        lock (Lock)
        {
            Fps = fps;
            Spf = spf;
            Sps = sps;
        }
    }

    /// <summary>Read writer-side counters/stats as one consistent lock-protected snapshot.</summary>
    public MasterAudioBufferSnapshot GetSnapshot()
    {
        lock (Lock)
        {
            return new MasterAudioBufferSnapshot(TotalSamplesWritten, Fps, Spf, Sps, NumberOfAudioSamples);
        }
    }

    /// <summary>
    /// Copy the next unread analysis block up to a fixed source snapshot. The analysis worker
    /// passes the snapshot's TotalSamplesWritten so each wake-up processes a bounded unit even
    /// while live capture continues writing.
    /// </summary>
    public MasterAudioBufferReadResult CopyAnalysisSamples(
        Span<float> destination,
        ulong sourceSampleEnd)
    {
        lock (Lock)
        {
            ulong currentTotalSamplesWritten = TotalSamplesWritten;
            ulong targetSampleEnd = Math.Min(sourceSampleEnd, currentTotalSamplesWritten);
            ulong originalPendingSamples = targetSampleEnd > AnalysisLastTotalSamplesWritten
                ? targetSampleEnd - AnalysisLastTotalSamplesWritten
                : 0;

            bool inputOverrun = false;
            ulong inputSamplesDropped = 0;
            ulong retainedCapacity = (ulong)NumberOfAudioSamples;
            if (currentTotalSamplesWritten > AnalysisLastTotalSamplesWritten &&
                currentTotalSamplesWritten - AnalysisLastTotalSamplesWritten > retainedCapacity)
            {
                inputOverrun = true;
                inputSamplesDropped = currentTotalSamplesWritten - AnalysisLastTotalSamplesWritten - retainedCapacity;
                AnalysisLastTotalSamplesWritten = currentTotalSamplesWritten - retainedCapacity;
                AnalysisLastWriteIndex = (uint)(AnalysisLastTotalSamplesWritten % retainedCapacity);
            }

            int copyCount = 0;
            if (destination.Length > 0 && targetSampleEnd > AnalysisLastTotalSamplesWritten)
            {
                ulong pendingSamples = targetSampleEnd - AnalysisLastTotalSamplesWritten;
                copyCount = (int)Math.Min((ulong)destination.Length, pendingSamples);
                for (int i = 0; i < copyCount; i++)
                {
                    destination[i] = Samples[AnalysisLastWriteIndex];
                    AnalysisLastWriteIndex = (AnalysisLastWriteIndex + 1) % (uint)NumberOfAudioSamples;
                }
                AnalysisLastTotalSamplesWritten += (ulong)copyCount;
            }

            return new MasterAudioBufferReadResult(
                copyCount,
                sourceSampleEnd,
                originalPendingSamples,
                inputOverrun,
                inputSamplesDropped,
                Fps,
                Spf,
                Sps,
                NumberOfAudioSamples);
        }
    }

    /// <summary>Zero all counters and samples; called between sessions (UI thread, workers stopped).</summary>
    public void Reset()
    {
        int sampleRate;
        lock (Lock)
        {
            sampleRate = Math.Max(1, NumberOfAudioSamples / SecondsOfBuffer);
        }
        Reset(sampleRate);
    }

    /// <summary>Zero all counters and samples; called between sessions (UI thread, workers stopped).</summary>
    public void Reset(int sampleRate)
    {
        lock (Lock)
        {
            int wanted = sampleRate * SecondsOfBuffer;
            if (wanted != NumberOfAudioSamples)
            {
                NumberOfAudioSamples = wanted;
                Samples = new float[wanted];
            }
            else
            {
                Array.Clear(Samples);
            }
            WriteIndex = 0;
            TotalSamplesWritten = 0;
            AnalysisLastTotalSamplesWritten = 0;
            AnalysisLastWriteIndex = 0;
            Fps = Spf = Sps = 0.0;
        }
    }
}
