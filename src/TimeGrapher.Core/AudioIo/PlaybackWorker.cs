using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.AudioIo;

/// <summary>
/// WAV file playback worker. Port of TPlaybackWorker (PlaybackWorker.cpp). Streams a
/// 32-bit float mono WAV in fixed-size blocks with real-time pacing, ring-writing into
/// the shared <see cref="MasterAudioBuffer"/>. Raises <see cref="DataReady"/> per block
/// and <see cref="DoneReadingFile"/> once when the file finishes (not on cancel).
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

    /// <summary>Raised on the playback thread once when end-of-file is reached (not on cancel).</summary>
    public event Action? DoneReadingFile;

    public PlaybackWorker(MasterAudioBuffer buffer, int samplesPerSecond)
    {
        _rawAudio = buffer;
        _rawAudio.TotalSamplesWritten = 0;
        _rawAudio.WriteIndex = 0;
        _rawAudio.AnalysisLastTotalSamplesWritten = 0;
        _rawAudio.AnalysisLastWriteIndex = 0;
        _rawAudio.Fps = 0.0;
        _rawAudio.Spf = 0.0;
        _rawAudio.Sps = 0.0;
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
    public void Start(string fileName)
    {
        _interruptionRequested = false;
        _thread = new Thread(() => Run(fileName))
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest // matches QThread::TimeCriticalPriority intent
        };
        _thread.Start();
    }

    /// <summary>Requests cancellation and joins the playback thread.</summary>
    public void Stop()
    {
        _interruptionRequested = true;
        var t = _thread;
        if (t != null && t.IsAlive)
        {
            t.Join();
        }
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
    }

    // ── Playback loop (PlaybackWorker.cpp::StartPlayback) ──────────────────────
    private void Run(string fileName)
    {
        if (!_timerStarted)
        {
            _timerStarted = true;
            _timer.Start();
        }

        if (!File.Exists(fileName))
        {
            DoneReadingFile?.Invoke();
            return;
        }

        FileStream? file = null;
        try
        {
            file = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch
        {
            DoneReadingFile?.Invoke();
            return;
        }

        using (file)
        {
            // ── Parse WAV header (WAV is little-endian) ──
            var header = new WaveHeader();
            var br = new BinaryReader(file);

            header.RiffId = ReadFourCc(file);
            header.FileSize = br.ReadUInt32();
            header.WaveId = ReadFourCc(file);
            header.FmtId = ReadFourCc(file);
            header.FmtSize = br.ReadUInt32();
            header.AudioFormat = br.ReadUInt16();
            header.NumChannels = br.ReadUInt16();
            header.SampleRate = br.ReadUInt32();
            header.ByteRate = br.ReadUInt32();
            header.BlockAlign = br.ReadUInt16();
            header.BitsPerSample = br.ReadUInt16();

            // Skip any extra fmt bytes if fmtSize > 16.
            if (header.FmtSize > 16)
                file.Seek((header.FmtSize - 16), SeekOrigin.Current);

            // Look for "data" chunk (it might not be immediately after fmt).
            while (file.Position < file.Length)
            {
                string chunkId = ReadFourCc(file);
                if (file.Position + 4 > file.Length) break;
                uint chunkSize = br.ReadUInt32();
                if (chunkId == "data")
                {
                    header.DataSize = chunkSize;
                    break;
                }
                file.Seek(chunkSize, SeekOrigin.Current);
            }

            if (header.RiffId != "RIFF" || (header.SampleRate != _samplesPerSecond) ||
                (header.NumChannels != 1) || (header.BitsPerSample != 32) ||
                (header.AudioFormat != 3))
            {
                // Original emits PlaybackDoneReadingFile twice here; once is sufficient
                // for the C# event contract (single notification on completion path).
                DoneReadingFile?.Invoke();
                return;
            }

            int numSamples = (int)(header.DataSize / sizeof(float));

            while ((file.Position < file.Length) && (numSamples > 0))
            {
                long start = _timer.ElapsedMilliseconds;

                int bytesIn = file.Read(_dataIn, 0, _dataInSize);
                if (bytesIn < 0)
                {
                    Console.Error.WriteLine("Read Error =" + bytesIn);
                    break;
                }
                else if ((bytesIn % 4) != 0)
                {
                    Console.Error.WriteLine("Read Error not Modulus of 4");
                    break;
                }
                else if (bytesIn == 0)
                {
                    Console.Error.WriteLine("Read Error 0");
                    break;
                }
                else if (_interruptionRequested)
                {
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

        DoneReadingFile?.Invoke();
    }

    private static string ReadFourCc(FileStream file)
    {
        Span<byte> b = stackalloc byte[4];
        int n = file.Read(b);
        if (n < 4) return string.Empty;
        return new string(new[] { (char)b[0], (char)b[1], (char)b[2], (char)b[3] });
    }

    // Port of TWaveHeader (WaveHeader.h).
    private sealed class WaveHeader
    {
        public string RiffId = "";       // "RIFF"
        public uint FileSize;            // Size of file - 8 bytes
        public string WaveId = "";       // "WAVE"
        public string FmtId = "";        // "fmt "
        public uint FmtSize;             // Usually 16 for PCM
        public ushort AudioFormat;       // 1 for PCM, 3 for IEEE Float
        public ushort NumChannels;
        public uint SampleRate;
        public uint ByteRate;
        public ushort BlockAlign;
        public ushort BitsPerSample;     // 32 for float
        public uint DataSize;
    }
}
