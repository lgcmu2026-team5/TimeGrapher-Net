using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.Wave;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.AudioIo;

/// <summary>
/// Live audio capture worker. Port of TAudioWorker (AudioWorker.cpp) using NAudio's
/// <see cref="WaveInEvent"/> in place of Qt's QAudioSource. Captures mono IEEE-float at
/// the requested sample rate, applies a software volume multiply, ring-writes into the
/// shared <see cref="MasterAudioBuffer"/>, maintains the original 2-second FPS/SPS/SPF
/// statistics, and raises <see cref="DataReady"/> after each captured block.
/// </summary>
public sealed class AudioCaptureWorker : IDisposable
{
    private const int Channels = MasterAudioBuffer.Channels; // mono

    private readonly MasterAudioBuffer _rawAudio;

    private WaveInEvent? _audioInput;
    private float _volume = 1.0f;

    // 2-second statistics state (mirrors AudioWorker.cpp).
    private bool _timerStarted;
    private double _lastTime;
    private ulong _frameCount;
    private ulong _sampleCount;
    private readonly Stopwatch _timer = new();

    /// <summary>Raised on the capture callback thread after each block is written.</summary>
    public event Action? DataReady;

    public AudioCaptureWorker(MasterAudioBuffer buffer)
    {
        _rawAudio = buffer;
        // Match the constructor reset of the original TAudioWorker.
        _rawAudio.TotalSamplesWritten = 0;
        _rawAudio.WriteIndex = 0;
        _rawAudio.AnalysisLastTotalSamplesWritten = 0;
        _rawAudio.AnalysisLastWriteIndex = 0;
        _rawAudio.Fps = 0.0;
        _rawAudio.Spf = 0.0;
        _rawAudio.Sps = 0.0;
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
            WaveFormat = format
        };
        _audioInput.DataAvailable += OnDataAvailable;
        _audioInput.StartRecording();
    }

    /// <summary>Sets the software input volume (0..1). (SetAudioInputVolume)</summary>
    public void SetVolume(float volume)
    {
        _volume = volume;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
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
        if (_audioInput != null)
        {
            // Detach the callback first so no further DataReady fires after Stop().
            _audioInput.DataAvailable -= OnDataAvailable;
            _audioInput.StopRecording();
            _audioInput.Dispose();
            _audioInput = null;
        }
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
    /// Returns the standard sample rates supported by the device (mirrors
    /// MainWindow::PopulateSampleRates standard rate probing). Up to 5 entries,
    /// 48000 first. NAudio cannot probe arbitrary rates, so this reports the
    /// well-known set; 48000 is always included.
    /// </summary>
    public static IReadOnlyList<int> GetSupportedSampleRates(int deviceNumber)
    {
        // Original standard rates list: {48000, 96000, 192000, 384000}.
        return new[] { 48000, 96000, 192000, 384000 };
    }
}
