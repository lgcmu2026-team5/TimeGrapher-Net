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
public sealed class AudioCaptureWorker : ILiveAudioWorker
{
    private const int Channels = MasterAudioBuffer.Channels; // mono

    private readonly MasterAudioBuffer _rawAudio;

    private WaveInEvent? _audioInput;
    // Teardown thread from a timed-out stop attempt; retries must join this same
    // teardown instead of reporting success on the already-cleared _audioInput.
    // Start/Stop are serialized on the UI thread, so a plain field suffices.
    private Thread? _pendingStopThread;
    // Test seam (mirrors LinuxLiveAudioWorker.StartCaptureProcessForTests):
    // replaces the blocking StopAndDispose teardown so the TryStop
    // pending-thread contract can be exercised without an audio device.
    private Action? _teardownOverride;
    private float _volume = 1.0f;
    private volatile bool _paused;
    private volatile bool _stopRequested;

    // 2-second statistics state (mirrors AudioWorker.cpp).
    private bool _timerStarted;
    private double _lastTime;
    private ulong _frameCount;
    private ulong _sampleCount;
    private readonly Stopwatch _timer = new();

    /// <summary>Raised on the capture callback thread after each block is written.</summary>
    public event Action? DataReady;

    /// <summary>Raised when capture ends without a stop request (device error/unplug).</summary>
    public event Action? CaptureEnded;

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
        // Suppress CaptureEnded from any replaced device while swapping inputs.
        _stopRequested = true;

        if (_audioInput != null)
        {
            // Mirror TryStop's discipline: detach before disposing, because
            // WaveInEvent raises RecordingStopped asynchronously and the flag
            // above is re-armed to false below — possibly before the replaced
            // device's callback runs, which would tear down the new run.
            _audioInput.DataAvailable -= OnDataAvailable;
            _audioInput.RecordingStopped -= OnRecordingStopped;
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
        _audioInput.RecordingStopped += OnRecordingStopped;
        _stopRequested = false;
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

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Console.Error.WriteLine("AudioCaptureWorker: recording stopped: " + e.Exception.Message);
        }

        if (!_stopRequested)
        {
            CaptureEnded?.Invoke();
        }
    }

    public bool TryStop(TimeSpan timeout)
    {
        _stopRequested = true;
        _paused = false;

        // A previous stop attempt timed out: wait for that same teardown to finish.
        Thread? pendingStop = _pendingStopThread;
        if (pendingStop != null)
        {
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                pendingStop.Join();
            }
            else if (!pendingStop.Join(timeout))
            {
                return false;
            }

            _pendingStopThread = null;
            // Fall through: also stop any capture started since the timed-out stop.
        }

        WaveInEvent? audioInput = Interlocked.Exchange(ref _audioInput, null);
        if (audioInput == null)
        {
            return true;
        }

        // Detach the callbacks first so no further DataReady/CaptureEnded fires after Stop().
        audioInput.DataAvailable -= OnDataAvailable;
        audioInput.RecordingStopped -= OnRecordingStopped;

        if (timeout == Timeout.InfiniteTimeSpan)
        {
            TeardownCapture(audioInput);
            return true;
        }

        // The result is purely join-based: a teardown exception is logged on the
        // stop thread (the callback is already detached, so there is nothing a
        // retry could do beyond waiting for the thread to finish).
        var stopThread = new Thread(() =>
        {
            try
            {
                TeardownCapture(audioInput);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("AudioCaptureWorker: stop failed: " + ex.Message);
            }
        })
        {
            Name = "AudioCaptureStop",
            IsBackground = true,
        };
        stopThread.Start();

        if (!stopThread.Join(timeout))
        {
            _pendingStopThread = stopThread;
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        Stop();
    }

    private void TeardownCapture(WaveInEvent audioInput)
    {
        Action? teardownOverride = _teardownOverride;
        if (teardownOverride != null)
        {
            teardownOverride();
            return;
        }

        StopAndDispose(audioInput);
    }

    /// <summary>
    /// Installs a fake active capture whose teardown is the given action, so
    /// tests can drive the TryStop timeout/retry/fall-through contract with a
    /// controllable blocking delegate instead of a real device.
    /// </summary>
    internal void InstallCaptureForTests(Action teardown)
    {
        _audioInput = new WaveInEvent();
        _teardownOverride = teardown;
    }

    /// <summary>WaveIn devices; the device number is the WaveIn index.</summary>
    public static IReadOnlyList<LiveAudioDevice> EnumerateInputDevices()
    {
        int count = WaveInEvent.DeviceCount;
        var devices = new List<LiveAudioDevice>(count);
        for (int i = 0; i < count; i++)
        {
            devices.Add(new LiveAudioDevice(i, WaveInEvent.GetCapabilities(i).ProductName));
        }
        return devices;
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

    private static void StopAndDispose(WaveInEvent audioInput)
    {
        try
        {
            audioInput.StopRecording();
        }
        finally
        {
            audioInput.Dispose();
        }
    }
}
