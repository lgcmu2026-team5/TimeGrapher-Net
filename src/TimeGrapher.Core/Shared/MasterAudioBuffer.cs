namespace TimeGrapher.Core.Shared;

/// <summary>
/// Port of TMasterAudioDataRaw (SharedAudio.h): a 30-second mono float ring buffer
/// shared between exactly one active input worker (writer) and the analysis worker (reader).
///
/// Concurrency contract (mirrors the C++ original):
///  - Writers append via <see cref="WriteSamples"/> which takes <see cref="Lock"/>.
///  - The analysis worker reads <see cref="TotalSamplesWritten"/> under <see cref="Lock"/>,
///    then reads <see cref="Samples"/> / advances <see cref="AnalysisLastWriteIndex"/> WITHOUT the
///    lock (the reader trails the writer by far less than the 30 s capacity).
///  - Stats (Fps/Spf/Sps) are written by the input worker under <see cref="Lock"/>.
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
