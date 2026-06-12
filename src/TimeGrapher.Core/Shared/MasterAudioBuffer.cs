using System.Diagnostics;

namespace TimeGrapher.Core.Shared;

public readonly record struct MasterAudioBufferSnapshot(
    ulong TotalSamplesWritten,
    double Fps,
    double Spf,
    double Sps,
    int NumberOfAudioSamples);

public readonly record struct MasterAudioBufferReadResult(
    int SamplesCopied,
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

    private float[] _samples;
    private int _numberOfAudioSamples;

    private uint _writeIndex;
    private ulong _totalSamplesWritten;

    // C++: MainThrd_LastTotalSamplesWritten / MainThrd_LastWriteIndex
    // ("MainThrd" historically; owned by the analysis worker in this port.)
    private ulong _analysisLastTotalSamplesWritten;
    private uint _analysisLastWriteIndex;

    // Input-side throughput stats displayed in the status bar.
    private double _fps;
    private double _spf;
    private double _sps;

    // Capture-timestamp ring: one (sampleEnd, Stopwatch ticks) entry per write so
    // the analysis worker can compute capture-to-processing latency even under
    // backlog. 256 entries cover ~2.5 s of 10 ms input blocks; if a deeper backlog
    // evicts the entry that contained a sample, the lookup returns the oldest
    // surviving (newer) stamp and the reported latency is a lower bound.
    private const int CaptureStampCapacity = 256;
    private readonly ulong[] _stampSampleEnd = new ulong[CaptureStampCapacity];
    private readonly long[] _stampTicks = new long[CaptureStampCapacity];
    private int _stampHead;
    private int _stampCount;

    public MasterAudioBuffer(int sampleRate)
    {
        _numberOfAudioSamples = sampleRate * SecondsOfBuffer;
        _samples = new float[_numberOfAudioSamples];
    }

    /// <summary>Ring-write a block of mono float samples (input worker thread).</summary>
    public void WriteSamples(ReadOnlySpan<float> data)
    {
        WriteSamples(data, Stopwatch.GetTimestamp());
    }

    /// <summary>Timestamp-injectable overload for deterministic latency tests.</summary>
    internal void WriteSamples(ReadOnlySpan<float> data, long captureTicks)
    {
        lock (Lock)
        {
            // Segment copies instead of a per-sample modulo loop: the capture
            // callback blocks on this lock, so the hold must stay short.
            int offset = 0;
            int remaining = data.Length;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, _numberOfAudioSamples - (int)_writeIndex);
                data.Slice(offset, chunk).CopyTo(_samples.AsSpan((int)_writeIndex, chunk));
                _writeIndex = (uint)(((int)_writeIndex + chunk) % _numberOfAudioSamples);
                offset += chunk;
                remaining -= chunk;
            }
            _totalSamplesWritten += (ulong)data.Length;

            if (data.Length > 0)
            {
                _stampSampleEnd[_stampHead] = _totalSamplesWritten;
                _stampTicks[_stampHead] = captureTicks;
                _stampHead = (_stampHead + 1) % CaptureStampCapacity;
                if (_stampCount < CaptureStampCapacity)
                {
                    _stampCount++;
                }
            }
        }
    }

    /// <summary>
    /// Stopwatch timestamp of the write block that contained the given absolute
    /// sample index (analysis worker thread). False until the first write or when
    /// the index lies beyond everything written so far.
    /// <paramref name="isLowerBound"/> is true when the true entry may have been
    /// evicted (the match is the oldest stamp of a full ring): the returned
    /// timestamp is then newer than the real capture time, so a latency computed
    /// from it is a LOWER bound — exactly the deep-stall regime where honest
    /// worst-case reporting matters.
    /// </summary>
    public bool TryGetCaptureTimestamp(ulong sampleIndex, out long captureTicks, out bool isLowerBound)
    {
        lock (Lock)
        {
            for (int i = 0; i < _stampCount; i++)
            {
                int idx = (_stampHead - _stampCount + i + CaptureStampCapacity) % CaptureStampCapacity;
                if (_stampSampleEnd[idx] >= sampleIndex)
                {
                    captureTicks = _stampTicks[idx];
                    isLowerBound = i == 0 && _stampCount == CaptureStampCapacity;
                    return true;
                }
            }
        }

        captureTicks = 0;
        isLowerBound = false;
        return false;
    }

    /// <summary>Update input-side throughput stats (input worker thread).</summary>
    public void SetStats(double fps, double spf, double sps)
    {
        lock (Lock)
        {
            _fps = fps;
            _spf = spf;
            _sps = sps;
        }
    }

    /// <summary>Read writer-side counters/stats as one consistent lock-protected snapshot.</summary>
    public MasterAudioBufferSnapshot GetSnapshot()
    {
        lock (Lock)
        {
            return new MasterAudioBufferSnapshot(_totalSamplesWritten, _fps, _spf, _sps, _numberOfAudioSamples);
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
            ulong currentTotalSamplesWritten = _totalSamplesWritten;
            ulong targetSampleEnd = Math.Min(sourceSampleEnd, currentTotalSamplesWritten);
            ulong originalPendingSamples = targetSampleEnd > _analysisLastTotalSamplesWritten
                ? targetSampleEnd - _analysisLastTotalSamplesWritten
                : 0;

            bool inputOverrun = false;
            ulong inputSamplesDropped = 0;
            ulong retainedCapacity = (ulong)_numberOfAudioSamples;
            if (currentTotalSamplesWritten > _analysisLastTotalSamplesWritten &&
                currentTotalSamplesWritten - _analysisLastTotalSamplesWritten > retainedCapacity)
            {
                inputOverrun = true;
                inputSamplesDropped = currentTotalSamplesWritten - _analysisLastTotalSamplesWritten - retainedCapacity;
                _analysisLastTotalSamplesWritten = currentTotalSamplesWritten - retainedCapacity;
                _analysisLastWriteIndex = (uint)(_analysisLastTotalSamplesWritten % retainedCapacity);
            }

            int copyCount = 0;
            if (destination.Length > 0 && targetSampleEnd > _analysisLastTotalSamplesWritten)
            {
                ulong pendingSamples = targetSampleEnd - _analysisLastTotalSamplesWritten;
                copyCount = (int)Math.Min((ulong)destination.Length, pendingSamples);
                // At most two wrap segments (the destination never exceeds the ring).
                int copied = 0;
                while (copied < copyCount)
                {
                    int chunk = Math.Min(copyCount - copied,
                                         _numberOfAudioSamples - (int)_analysisLastWriteIndex);
                    _samples.AsSpan((int)_analysisLastWriteIndex, chunk)
                        .CopyTo(destination.Slice(copied, chunk));
                    _analysisLastWriteIndex =
                        (uint)(((int)_analysisLastWriteIndex + chunk) % _numberOfAudioSamples);
                    copied += chunk;
                }
                _analysisLastTotalSamplesWritten += (ulong)copyCount;
            }

            return new MasterAudioBufferReadResult(
                copyCount,
                originalPendingSamples,
                inputOverrun,
                inputSamplesDropped,
                _fps,
                _spf,
                _sps,
                _numberOfAudioSamples);
        }
    }

    /// <summary>Zero all counters and samples; called between sessions (UI thread, workers stopped).</summary>
    public void Reset()
    {
        int sampleRate;
        lock (Lock)
        {
            sampleRate = Math.Max(1, _numberOfAudioSamples / SecondsOfBuffer);
        }
        Reset(sampleRate);
    }

    /// <summary>Zero all counters and samples; called between sessions (UI thread, workers stopped).</summary>
    public void Reset(int sampleRate)
    {
        lock (Lock)
        {
            int wanted = sampleRate * SecondsOfBuffer;
            if (wanted != _numberOfAudioSamples)
            {
                _numberOfAudioSamples = wanted;
                _samples = new float[wanted];
            }
            else
            {
                Array.Clear(_samples);
            }
            _writeIndex = 0;
            _totalSamplesWritten = 0;
            _analysisLastTotalSamplesWritten = 0;
            _analysisLastWriteIndex = 0;
            _fps = _spf = _sps = 0.0;
            _stampHead = 0;
            _stampCount = 0;
        }
    }
}
