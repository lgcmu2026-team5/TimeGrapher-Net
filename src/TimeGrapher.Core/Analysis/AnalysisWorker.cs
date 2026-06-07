using System.Diagnostics;
using TimeGrapher.Core.AudioIo;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Analysis;

/// <summary>
/// Port of TAnalysisWorker (AnalysisWorker.cpp). Owns the detector / sound-image
/// renderer / watch-metrics and turns new audio in the shared ring buffer into a
/// single <see cref="AnalysisFrame"/> per <see cref="HandleInputData"/> pass.
///
/// Threading: the original relied on Qt's moveToThread + queued
/// AudioDataReady -> HandleInputData. This port owns a dedicated thread that waits
/// on an <see cref="AutoResetEvent"/> (signalled by <see cref="NotifyDataReady"/>)
/// and runs HandleInputData once per wake-up.
/// </summary>
public sealed class AnalysisWorker : IDisposable
{
    public sealed class Config
    {
        public int SampleRate = 48000;
        public double LiftAngle = 52.0;
        public int AveragingPeriod = 2;
        public bool UseCOnset = false;
        public ulong SessionId = 0;
        public bool AutoBph = true;
        public int ManualBph = 0;
        public double HpfCutoffHz = 0.0;
        public int SoundImageWidth = 0;
        public int SoundImageHeight = 0;
        public int ScopeSnapshotPointBudget = 8000;
        public ISampleWriter? SampleWriter = null;
    }

    private const uint DetectorNumberOfSamples = 4096u;
    private readonly MasterAudioBuffer _rawAudio;
    private readonly Config _config;
    private readonly DetectorMetricsEngine _pipeline;
    private readonly ScopeRateFrameProjector _scopeRateProjector;
    private readonly SoundPrintFrameProjector _soundPrintProjector;
    private readonly float[] _inputBlock;

    private ulong _nextFrameSourceId = 1;
    private bool _foregroundTimerStarted = false;
    private readonly Stopwatch _foregroundTimer = new();
    private double _foregroundLastTime = 0.0;
    private ulong _foregroundFrameCount = 0;
    private ulong _foregroundSampleCount = 0;

    // Thread loop state (port-only; replaces Qt moveToThread).
    private Thread? _thread;
    private readonly AutoResetEvent _wakeup = new(false);
    private volatile bool _stopRequested = false;
    private volatile bool _completionRequested = false;

    /// <summary>Raised on the analysis thread when a frame is ready.</summary>
    public event Action<AnalysisFrame>? AnalysisFrameReady;

    public AnalysisWorker(MasterAudioBuffer buffer, Config config)
    {
        _rawAudio = buffer;
        _config = config;
        _pipeline = new DetectorMetricsEngine(new DetectorMetricsEngineConfig(
            config.SampleRate,
            config.LiftAngle,
            config.AveragingPeriod,
            config.UseCOnset,
            config.AutoBph,
            config.ManualBph,
            config.HpfCutoffHz));

        _inputBlock = new float[DetectorNumberOfSamples];
        _scopeRateProjector = new ScopeRateFrameProjector(
            config.SampleRate,
            config.UseCOnset,
            config.ScopeSnapshotPointBudget);
        _soundPrintProjector = new SoundPrintFrameProjector(
            config.SampleRate,
            config.SoundImageWidth,
            config.SoundImageHeight);
    }

    /// <summary>Starts the analysis thread (AutoResetEvent wait loop).</summary>
    public void Start()
    {
        if (_thread != null)
        {
            return;
        }
        _stopRequested = false;
        _completionRequested = false;
        _thread = new Thread(ThreadLoop)
        {
            Name = "AnalysisWorker",
            IsBackground = true,
            Priority = ThreadPriority.Highest, // QThread::TimeCriticalPriority
        };
        _thread.Start();
    }

    /// <summary>Signal that new audio is available. Callable from any thread.</summary>
    public void NotifyDataReady()
    {
        _wakeup.Set();
    }

    /// <summary>Stops the analysis thread and joins it.</summary>
    public void Stop()
    {
        _ = TryStop(Timeout.InfiniteTimeSpan);
    }

    public bool TryStop(TimeSpan timeout)
    {
        if (_thread == null)
        {
            return true;
        }

        _stopRequested = true;
        _wakeup.Set();
        Thread thread = _thread;
        bool stopped = JoinThread(thread, timeout);
        if (stopped)
        {
            _thread = null;
        }

        return stopped;
    }

    public bool CompleteInput(TimeSpan timeout)
    {
        if (_thread == null)
        {
            DrainAndFlushInput();
            return true;
        }

        _completionRequested = true;
        _wakeup.Set();
        Thread thread = _thread;
        bool stopped = JoinThread(thread, timeout);
        if (stopped)
        {
            _thread = null;
        }

        return stopped;
    }

    private void ThreadLoop()
    {
        while (true)
        {
            _wakeup.WaitOne();
            if (_completionRequested)
            {
                DrainAndFlushInput();
                break;
            }
            if (_stopRequested)
            {
                break;
            }
            HandleInputData();
        }
    }

    public void HandleInputData()
    {
        HandleInputDataCore(stopInterruptible: true);
    }

    private void HandleInputDataCore(bool stopInterruptible)
    {
        var processingTimer = Stopwatch.StartNew();
        MasterAudioBufferSnapshot snapshot = _rawAudio.GetSnapshot();
        ulong sourceSampleEnd = snapshot.TotalSamplesWritten;
        AnalysisFrame? frame = null;

        if (!_foregroundTimerStarted)
        {
            _foregroundTimer.Restart();
            _foregroundTimerStarted = true;
            _foregroundLastTime = 0.0;
            _foregroundFrameCount = 0;
            _foregroundSampleCount = 0;
        }

        while (!stopInterruptible || !_stopRequested)
        {
            MasterAudioBufferReadResult read = _rawAudio.CopyAnalysisSamples(_inputBlock, sourceSampleEnd);
            if (read.SamplesCopied <= 0)
            {
                break;
            }

            frame ??= new AnalysisFrame
            {
                SessionId = _config.SessionId,
                SourceId = _nextFrameSourceId++,
                SourceSampleEnd = sourceSampleEnd,
                SampleRate = _config.SampleRate,
                PendingSamples = read.OriginalPendingSamples,
                BackgroundFps = read.Fps,
                BackgroundSps = read.Sps,
                BackgroundSpf = read.Spf,
            };

            if (read.InputOverrun)
            {
                frame.InputOverrun = true;
                frame.InputSamplesDropped += read.InputSamplesDropped;
            }

            var block = new ReadOnlySpan<float>(_inputBlock, 0, read.SamplesCopied);

            _config.SampleWriter?.Write(block);

            _soundPrintProjector.ProcessSamples(block);

            DetectorMetricsBlockUpdate pipelineUpdate = _pipeline.Process(block);
            _scopeRateProjector.Project(pipelineUpdate, frame);
            _soundPrintProjector.Project(pipelineUpdate);
            UpdateForegroundStats(read.SamplesCopied, frame);
        }

        if (frame == null)
        {
            return;
        }

        _scopeRateProjector.AppendSnapshot(frame);
        _soundPrintProjector.AppendSnapshot(frame);

        MasterAudioBufferSnapshot endSnapshot = _rawAudio.GetSnapshot();
        frame.AnalysisLagSamples = endSnapshot.TotalSamplesWritten > sourceSampleEnd
            ? endSnapshot.TotalSamplesWritten - sourceSampleEnd
            : 0;
        frame.ProcessingElapsedMs = processingTimer.Elapsed.TotalMilliseconds;

        AnalysisFrameReady?.Invoke(frame);
    }

    private void DrainAndFlushInput()
    {
        HandleInputDataCore(stopInterruptible: false);

        var processingTimer = Stopwatch.StartNew();
        MasterAudioBufferSnapshot snapshot = _rawAudio.GetSnapshot();
        var frame = new AnalysisFrame
        {
            SessionId = _config.SessionId,
            SourceId = _nextFrameSourceId++,
            SourceSampleEnd = snapshot.TotalSamplesWritten,
            SampleRate = _config.SampleRate,
            PendingSamples = 0,
            BackgroundFps = snapshot.Fps,
            BackgroundSps = snapshot.Sps,
            BackgroundSpf = snapshot.Spf,
        };

        DetectorMetricsBlockUpdate flushUpdate = _pipeline.Flush();
        _scopeRateProjector.Project(flushUpdate, frame);
        _soundPrintProjector.Project(flushUpdate);
        _scopeRateProjector.AppendSnapshot(frame);
        _soundPrintProjector.AppendSnapshot(frame, force: true);
        frame.ProcessingElapsedMs = processingTimer.Elapsed.TotalMilliseconds;

        AnalysisFrameReady?.Invoke(frame);
    }

    private void UpdateForegroundStats(int slice, AnalysisFrame frame)
    {
        _foregroundSampleCount += (ulong)slice;
        _foregroundFrameCount++;

        double currentTime = _foregroundTimer.ElapsedMilliseconds / 1000.0;
        if (currentTime - _foregroundLastTime > 2)
        {
            double fdelta = currentTime - _foregroundLastTime;
            frame.ForegroundFps = _foregroundFrameCount / fdelta;
            frame.ForegroundSps = _foregroundSampleCount / fdelta;
            frame.ForegroundSpf = (double)_foregroundSampleCount / _foregroundFrameCount;
            frame.ForegroundStatsUpdated = true;

            _foregroundLastTime = currentTime;
            _foregroundFrameCount = 0;
            _foregroundSampleCount = 0;
        }
    }

    public void Dispose()
    {
        Stop();
        _wakeup.Dispose();
    }

    private static bool JoinThread(Thread thread, TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            thread.Join();
            return true;
        }

        return thread.Join(timeout);
    }
}
