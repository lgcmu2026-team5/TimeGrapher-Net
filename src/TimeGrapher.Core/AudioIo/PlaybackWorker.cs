using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.AudioIo;

public enum PlaybackCompletionReason
{
    Eof,
    Cancelled,
    Failed,
}

/// <summary>
/// WAV file playback worker. Port of TPlaybackWorker (PlaybackWorker.cpp). Streams a
/// 32-bit float mono WAV in fixed-size blocks with real-time pacing, ring-writing into
/// the shared <see cref="MasterAudioBuffer"/>. Raises <see cref="DataReady"/> per block
/// and <see cref="DoneReadingFile"/> once when the file finishes, is cancelled, or fails.
/// Runs on a dedicated thread; <see cref="Stop"/> requests interruption and joins.
/// </summary>
public sealed class PlaybackWorker : IDisposable
{
    // Windows pacing constants (PlaybackWorker.cpp, Q_OS_WIN branch).
    private const int PlaybackSamplePeriodMsec = 10;
    private const int DelayFugeTimeMs = 1;

    private const int SampleSize = sizeof(float); // SAMPLE_SIZE

    private readonly MasterAudioBuffer _rawAudio;
    private readonly int _samplesPerSecond;

    // PLAYBACK_NUMBER_OF_SAMPLES = mSamplesPerSecond / (1000 / PLAYBACK_SAMPLE_PERIOD_MSEC)
    private readonly int _playbackNumberOfSamples;
    private readonly int _dataInSize;
    private readonly byte[] _dataIn;
    private readonly float[] _floatBlock; // reusable byte->float conversion buffer

    // 2-second statistics state.
    private bool _timerStarted;
    private double _lastTime;
    private ulong _frameCount;
    private ulong _sampleCount;
    private readonly Stopwatch _timer = new();

    private Thread? _thread;
    private volatile bool _interruptionRequested;

    /// <summary>Raised on the playback thread after each block is written.</summary>
    public event Action? DataReady;

    /// <summary>Raised on the playback thread once when playback finishes.</summary>
    public event Action<PlaybackCompletionReason>? DoneReadingFile;

    public PlaybackWorker(MasterAudioBuffer buffer, int samplesPerSecond)
    {
        _rawAudio = buffer;
        _rawAudio.Reset(samplesPerSecond);
        _timerStarted = false;
        _samplesPerSecond = samplesPerSecond;
        _lastTime = 0.0;
        _frameCount = 0;
        _sampleCount = 0;

        _playbackNumberOfSamples = _samplesPerSecond / (1000 / PlaybackSamplePeriodMsec);
        _dataInSize = SampleSize * _playbackNumberOfSamples;
        _dataIn = new byte[_dataInSize];
        _floatBlock = new float[_playbackNumberOfSamples];
    }

    /// <summary>Starts playback on a dedicated thread (real-time paced, same as the original).</summary>
    public bool Start(string fileName)
    {
        if (_thread?.IsAlive == true)
        {
            return false;
        }

        _interruptionRequested = false;
        _thread = new Thread(() => Run(fileName))
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest // matches QThread::TimeCriticalPriority intent
        };
        _thread.Start();
        return true;
    }

    /// <summary>Requests cancellation and joins the playback thread.</summary>
    public void Stop()
    {
        _ = TryStop(Timeout.InfiniteTimeSpan);
    }

    public bool TryStop(TimeSpan timeout)
    {
        _interruptionRequested = true;
        var t = _thread;
        if (t != null && t.IsAlive)
        {
            bool stopped = timeout == Timeout.InfiniteTimeSpan ? JoinInfinite(t) : t.Join(timeout);
            if (!stopped)
            {
                return false;
            }
        }
        _thread = null;
        return true;
    }

    public void Dispose()
    {
        Stop();
    }

    // ── Playback loop (PlaybackWorker.cpp::StartPlayback) ──────────────────────
    private void Run(string fileName)
    {
        PlaybackCompletionReason reason = PlaybackCompletionReason.Eof;

        if (!_timerStarted)
        {
            _timerStarted = true;
            _timer.Start();
        }

        if (!File.Exists(fileName))
        {
            DoneReadingFile?.Invoke(PlaybackCompletionReason.Failed);
            return;
        }

        if (!WavProbe.TryReadFormat(fileName, out WavFormatInfo format, out _) ||
            format.SampleRate != _samplesPerSecond ||
            !WavProbe.IsAccepted(format, WavAcceptanceProfile.PlaybackFloatMonoStandardRates))
        {
            DoneReadingFile?.Invoke(PlaybackCompletionReason.Failed);
            return;
        }

        FileStream? file = null;
        try
        {
            file = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch
        {
            DoneReadingFile?.Invoke(PlaybackCompletionReason.Failed);
            return;
        }

        using (file)
        {
            file.Position = format.DataOffset;
            long dataEnd = format.DataOffset + format.DataSize;

            int numSamples = (int)((dataEnd - format.DataOffset) / sizeof(float));

            while ((file.Position < dataEnd) && (numSamples > 0))
            {
                long start = _timer.ElapsedMilliseconds;

                int bytesToRead = (int)Math.Min(_dataInSize, dataEnd - file.Position);
                int bytesIn = file.Read(_dataIn, 0, bytesToRead);
                if (bytesIn < 0)
                {
                    Console.Error.WriteLine("Read Error =" + bytesIn);
                    reason = PlaybackCompletionReason.Failed;
                    break;
                }
                else if ((bytesIn % 4) != 0)
                {
                    Console.Error.WriteLine("Read Error not Modulus of 4");
                    reason = PlaybackCompletionReason.Failed;
                    break;
                }
                else if (bytesIn == 0)
                {
                    Console.Error.WriteLine("Read Error 0");
                    reason = PlaybackCompletionReason.Failed;
                    break;
                }
                else if (_interruptionRequested)
                {
                    reason = PlaybackCompletionReason.Cancelled;
                    break; // Exit loop early
                }

                int numberOfSamples = bytesIn / SampleSize;

                // byte[] -> float and ring-write (WriteSamples locks internally).
                Span<float> block = _floatBlock.AsSpan(0, numberOfSamples);
                for (int i = 0; i < numberOfSamples; i++)
                {
                    int bits = _dataIn[i * 4] | (_dataIn[i * 4 + 1] << 8) |
                               (_dataIn[i * 4 + 2] << 16) | (_dataIn[i * 4 + 3] << 24);
                    block[i] = BitConverter.Int32BitsToSingle(bits);
                }
                _rawAudio.WriteSamples(block);

                DataReady?.Invoke();

                ++_frameCount;
                _sampleCount += (ulong)numberOfSamples;
                numSamples -= numberOfSamples;
                double currentTime = _timer.ElapsedMilliseconds / 1000.0;
                if (currentTime - _lastTime > 2) // average fps over 2 seconds
                {
                    double fdelta = currentTime - _lastTime;
                    double fps = _frameCount / fdelta;
                    double sps = _sampleCount / fdelta;
                    // Original: mSampleCount/mFrameCount with both uint64_t -> integer division.
                    double spf = _sampleCount / _frameCount;
                    _rawAudio.SetStats(fps, spf, sps);
                    _lastTime = currentTime;
                    _frameCount = 0;
                    _sampleCount = 0;
                }

                long delta = (_timer.ElapsedMilliseconds - start) + DelayFugeTimeMs;
                long sleepTime = PlaybackSamplePeriodMsec - delta;
                if (sleepTime < 0) sleepTime = 0;
                Thread.Sleep((int)sleepTime);
            }
        }

        if (_interruptionRequested && reason == PlaybackCompletionReason.Eof)
        {
            reason = PlaybackCompletionReason.Cancelled;
        }

        DoneReadingFile?.Invoke(reason);
    }

    private static bool JoinInfinite(Thread thread)
    {
        thread.Join();
        return true;
    }
}
