using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NAudio.Wave;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Platform.WindowsAudio;

/// <summary>
/// Live audio capture worker. Port of TAudioWorker (AudioWorker.cpp) using NAudio's
/// <see cref="WaveInEvent"/> in place of Qt's QAudioSource. Captures mono IEEE-float at
/// the requested sample rate, applies a software volume multiply, ring-writes into the
/// shared <see cref="MasterAudioBuffer"/>, maintains the original 2-second FPS/SPS/SPF
/// statistics, and raises <see cref="DataReady"/> after each captured block.
/// </summary>
public sealed class AudioCaptureWorker : IAudioInputWorker
{
    private const int Channels = MasterAudioBuffer.Channels; // mono

    private readonly MasterAudioBuffer _rawAudio;

    private WaveInEvent? _audioInput;
    private float _volume = 1.0f;
    private volatile bool _paused;

    // 2-second statistics state (mirrors AudioWorker.cpp).
    private bool _timerStarted;
    private double _lastTime;
    private ulong _frameCount;
    private ulong _sampleCount;
    private readonly Stopwatch _timer = new();

    /// <summary>Raised on the capture callback thread after each block is written.</summary>
    public event Action? DataReady;

    public bool IsPaused => _paused;

    public AudioCaptureWorker(MasterAudioBuffer buffer)
    {
        _rawAudio = buffer;
        _rawAudio.Reset();
        _timerStarted = false;
        _lastTime = 0.0;
        _frameCount = 0;
        _sampleCount = 0;
    }

    /// <summary>
    /// Starts recording from the given WaveIn device at the requested sample rate.
    /// (StartAudioRecording). Volume is a software multiply in [0,1].
    /// </summary>
    public void Start(int deviceNumber, int sampleRate, float volume)
    {
        _volume = volume;
        _paused = false;

        if (_audioInput != null)
        {
            _audioInput.Dispose();
            _audioInput = null;
        }

        // IEEE float, mono, requested sample rate (winmm converts as needed).
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, Channels);

        _audioInput = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = format,
            BufferMilliseconds = 20,
        };
        _audioInput.DataAvailable += OnDataAvailable;
        _audioInput.StartRecording();
    }

    /// <summary>Sets the software input volume (0..1). (SetAudioInputVolume)</summary>
    public void SetVolume(float volume)
    {
        _volume = volume;
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_paused)
        {
            return;
        }

        if (!_timerStarted)
        {
            _timerStarted = true;
            _timer.Start();
        }

        // byte[] -> float, count = number of float samples available.
        int numberOfSamples = e.BytesRecorded / sizeof(float);
        if (numberOfSamples <= 0)
        {
            DataReady?.Invoke();
            return;
        }

        float vol = _volume;
        Span<float> block = numberOfSamples <= 4096
            ? stackalloc float[numberOfSamples]
            : new float[numberOfSamples];

        ReadOnlySpan<byte> src = e.Buffer.AsSpan(0, numberOfSamples * sizeof(float));
        for (int i = 0; i < numberOfSamples; i++)
        {
            int bits = src[i * 4] | (src[i * 4 + 1] << 8) | (src[i * 4 + 2] << 16) | (src[i * 4 + 3] << 24);
            block[i] = BitConverter.Int32BitsToSingle(bits) * vol;
        }

        // Ring-write into the shared buffer (locks internally).
        _rawAudio.WriteSamples(block);

        // ── 2-second statistics (AudioWorker.cpp::ProcessAudioInput) ──
        ++_frameCount;
        _sampleCount += (ulong)numberOfSamples;
        double currentTime = _timer.ElapsedMilliseconds / 1000.0;
        if (currentTime - _lastTime > 2) // average fps over 2 seconds
        {
            double fdelta = currentTime - _lastTime;
            double fps = _frameCount / fdelta;
            double sps = _sampleCount / fdelta;
            // Original: SampleCount/FrameCount with both uint64_t -> integer division.
            double spf = _sampleCount / _frameCount;
            _rawAudio.SetStats(fps, spf, sps);
            _lastTime = currentTime;
            _frameCount = 0;
            _sampleCount = 0;
        }

        DataReady?.Invoke();
    }

    /// <summary>Stops recording and tears down the device. (StopAudioRecording)</summary>
    public void Stop()
    {
        _ = TryStop(Timeout.InfiniteTimeSpan);
    }

    public bool TryStop(TimeSpan timeout)
    {
        _paused = false;
        WaveInEvent? audioInput = Interlocked.Exchange(ref _audioInput, null);
        if (audioInput == null)
        {
            return true;
        }

        // Detach the callback first so no further DataReady fires after Stop().
        audioInput.DataAvailable -= OnDataAvailable;

        if (timeout == Timeout.InfiniteTimeSpan)
        {
            StopAndDispose(audioInput);
            return true;
        }

        Exception? error = null;
        var stopThread = new Thread(() =>
        {
            try
            {
                StopAndDispose(audioInput);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            Name = "AudioCaptureStop",
            IsBackground = true,
        };
        stopThread.Start();

        if (!stopThread.Join(timeout))
        {
            return false;
        }

        if (error != null)
        {
            Console.Error.WriteLine("AudioCaptureWorker: stop failed: " + error.Message);
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        Stop();
    }

    /// <summary>WaveIn device names, indexed by device number.</summary>
    public static IReadOnlyList<string> EnumerateInputDevices()
    {
        int count = WaveInEvent.DeviceCount;
        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            names.Add(WaveInEvent.GetCapabilities(i).ProductName);
        }
        return names;
    }

    /// <summary>
    /// Returns the standard sample-rate candidates shown by the UI. NAudio/WinMM does not
    /// expose the same up-front per-format support probe that Qt used, so Start() remains
    /// the authoritative validation point for live capture.
    /// </summary>
    public static IReadOnlyList<int> GetCandidateSampleRates(int deviceNumber)
    {
        _ = deviceNumber;
        return AudioSampleRates.Standard;
    }

    [Obsolete("Use GetCandidateSampleRates; live capture support is validated by Start().")]
    public static IReadOnlyList<int> GetSupportedSampleRates(int deviceNumber) => GetCandidateSampleRates(deviceNumber);

    private static void StopAndDispose(WaveInEvent audioInput)
    {
        audioInput.StopRecording();
        audioInput.Dispose();
    }
}
