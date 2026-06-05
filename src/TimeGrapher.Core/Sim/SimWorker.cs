using System.Diagnostics;
using System.Globalization;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Sim;

/// <summary>
/// Port of TSimWorker (SimWorker.h / SimWorker.cpp).
///
/// Runs a synthetic watch stream on a dedicated thread, ring-writing blocks into the
/// shared <see cref="MasterAudioBuffer"/> with real-time pacing and 2-second throughput
/// statistics, exactly like the original Qt worker.
/// </summary>
public sealed class SimWorker : IDisposable
{
    // Windows timing constants (Q_OS_WIN branch of SimWorker.cpp).
    private const int SimSamplePeriodMsec = 10;
    private const int DelayFugeTimeMs = 1;

    private readonly MasterAudioBuffer _rawAudio;
    private readonly int _samplesPerSecond;

    // SIM_NUMBER_OF_SAMPLES = mSamplesPerSecond/(1000/SIM_SAMPLE_PERIOD_MSEC)
    private readonly int _dataInSize;
    private readonly float[] _dataIn;

    private bool _timerStarted; // mTimerStarted
    private double _lastTime;   // mLastTime
    private ulong _frameCount;  // mFrameCount
    private ulong _sampleCount; // mSampleCount
    private readonly Stopwatch _timer = new(); // mTimer (QElapsedTimer)

    private Thread? _thread;
    private volatile bool _interruptionRequested; // QThread::isInterruptionRequested()

    /// <summary>Notify-only: new audio is available. Raised from the sim thread.</summary>
    public event Action? DataReady; // SimDataReady

    /// <summary>Raised once when the sim loop finishes/stops. Raised from the sim thread.</summary>
    public event Action? SimDone;   // SimDone

    public SimWorker(MasterAudioBuffer buffer, int samplesPerSecond)
    {
        _rawAudio = buffer;
        // Mirror the TSimWorker constructor zeroing of the shared buffer.
        _rawAudio.TotalSamplesWritten = 0;
        _rawAudio.WriteIndex = 0;
        _rawAudio.AnalysisLastTotalSamplesWritten = 0; // MainThrd_LastTotalSamplesWritten
        _rawAudio.AnalysisLastWriteIndex = 0;          // MainThrd_LastWriteIndex
        _rawAudio.Fps = 0.0;
        _rawAudio.Spf = 0.0;
        _rawAudio.Sps = 0.0;
        _timerStarted = false;
        _samplesPerSecond = samplesPerSecond;
        _lastTime = 0.0;
        _frameCount = 0;
        _sampleCount = 0;
        _dataInSize = _samplesPerSecond / (1000 / SimSamplePeriodMsec);
        _dataIn = new float[_dataInSize];
    }

    /// <summary>Start the sim on a dedicated thread (StartSim slot via worker thread).</summary>
    public void Start(WatchSynthStreamConfig cfg)
    {
        _interruptionRequested = false;
        var cfgCopy = cfg.Clone(); // original receives cfg by value
        _thread = new Thread(() => StartSim(cfgCopy))
        {
            IsBackground = true,
            Name = "SimWorker"
        };
        _thread.Start();
    }

    /// <summary>Request interruption and join the sim thread (Qt quit+wait equivalent).</summary>
    public void Stop()
    {
        _interruptionRequested = true;
        Thread? t = _thread;
        if (t != null && t.IsAlive)
            t.Join();
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        // TSimWorker destructor: "delete [] mDataIn; qInfo() << SimWorker Destructor"
        Console.Error.WriteLine("SimWorker Destructor");
    }

    private void StartSim(WatchSynthStreamConfig cfg)
    {
        long start, delta, sleepTime;
        double currentTime;
        var events = new WatchSynthStreamEvent[16];
        WatchSynthStreamFillResult r;
        cfg.SampleRateHz = (uint)_samplesPerSecond;

        WatchSynthStream stream;
        try
        {
            // watch_synth_stream_init: validation failure path.
            stream = new WatchSynthStream(cfg);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("init failed: " + ex.Message);
            SimDone?.Invoke();
            return;
        }

        if (!_timerStarted)
        {
            _timerStarted = true;
            _timer.Restart();
        }

        while (true)
        {
            start = _timer.ElapsedMilliseconds;

            r = stream.FillF32(_dataIn.AsSpan(0, _dataInSize), events.AsSpan(0, 16));
            if (r.SamplesWritten != _dataInSize)
            {
                Console.Error.WriteLine("short fill");
                break;
            }
            if (_interruptionRequested)
            {
                break; // Exit loop early
            }
            int numberOfSamples = r.SamplesWritten;

            // Ring-write the block into the shared buffer (memcpy + rollover in the
            // original; WriteSamples produces the identical WriteIndex/TotalSamplesWritten
            // state under the buffer Lock).
            _rawAudio.WriteSamples(_dataIn.AsSpan(0, numberOfSamples));
            DataReady?.Invoke(); // Emit data to the main thread

            ++_frameCount;
            _sampleCount += (ulong)numberOfSamples;
            currentTime = _timer.ElapsedMilliseconds / 1000.0;
            if (currentTime - _lastTime > 2) // average fps over 2 seconds
            {
                double fdelta = currentTime - _lastTime;
                double fps = _frameCount / fdelta;            // uint64_t / double
                double sps = _sampleCount / fdelta;           // uint64_t / double
                double spf = _sampleCount / _frameCount;      // uint64_t / uint64_t = integer division, then -> double
                _rawAudio.SetStats(fps, spf, sps);
                _lastTime = currentTime;
                _frameCount = 0;
                _sampleCount = 0;
            }
            delta = (_timer.ElapsedMilliseconds - start) + DelayFugeTimeMs;
            sleepTime = SimSamplePeriodMsec - delta;
            if (sleepTime < 0) sleepTime = 0;
            Thread.Sleep((int)sleepTime); // QThread::msleep(SleepTime); 0 yields the timeslice
        }
        SimDone?.Invoke();
        Console.Error.WriteLine("After Finish");
    }
}
