using System.Diagnostics;
using TimeGrapher.Core.AudioIo;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Detection.Scoring;
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
        /// <summary>Opt-in veto gate at the metrics choke point; null = no gate.</summary>
        public IBeatEventGate? EventGate = null;
        public int SoundImageWidth = 0;
        public int SoundImageHeight = 0;
        public int ScopeSnapshotPointBudget = 8000;
        public uint SoundImageBackgroundColor = 0xFFFFFFFFu;
        public ISampleWriter? SampleWriter = null;
    }

    private const uint DetectorNumberOfSamples = 4096u;
    private readonly MasterAudioBuffer _rawAudio;
    private readonly Config _config;
    private readonly DetectorMetricsEngine _pipeline;
    private readonly ScopeRateFrameProjector _scopeRateProjector;
    private readonly SoundPrintFrameProjector _soundPrintProjector;
    private readonly SpectrogramFrameProjector _spectrogramProjector;
    private readonly BeatMetricsFrameProjector _beatMetricsProjector = new();
    private readonly BeatSegmentCapture _beatSegmentCapture;
    private readonly SweepFrameProjector _sweepProjector;
    private readonly MultiFilterFrameProjector _multiFilterProjector;
    private readonly AnalysisDeadlineMonitor _deadlineMonitor = new();
    private readonly float[] _inputBlock;

    // Nominal locked beat period (3600/bph) captured while synced; the deadline
    // monitor falls back to the 28800-BPH default (125 ms) before the first lock.
    private double _latestBeatPeriodS;

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

    // Theme recolor request from another thread (e.g. UI theme toggle); applied on
    // the analysis thread between frames so it never races the pixel buffer.
    private readonly object _recolorLock = new();
    private uint? _pendingSoundBackground;

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
            config.HpfCutoffHz,
            config.EventGate != null ? new BeatEventGateConfig(config.EventGate) : null));

        _inputBlock = new float[DetectorNumberOfSamples];
        _scopeRateProjector = new ScopeRateFrameProjector(
            config.SampleRate,
            config.UseCOnset,
            config.ScopeSnapshotPointBudget);
        _soundPrintProjector = new SoundPrintFrameProjector(
            config.SampleRate,
            config.SoundImageWidth,
            config.SoundImageHeight,
            config.SoundImageBackgroundColor);
        _spectrogramProjector = new SpectrogramFrameProjector(config.SampleRate);
        _beatSegmentCapture = new BeatSegmentCapture(
            config.SampleRate,
            config.LiftAngle,
            config.EventGate?.WindowPostMs ?? 0.0,
            (int)DetectorNumberOfSamples);
        _sweepProjector = new SweepFrameProjector(config.SampleRate);
        _multiFilterProjector = new MultiFilterFrameProjector(config.SampleRate);
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

    /// <summary>
    /// Request a new Scope Mode sweep window length (multiple of the beat
    /// period). Stored in a volatile knob and applied on the analysis thread at
    /// the start of the next pass. Callable from any thread.
    /// </summary>
    public void SetSweepMultiple(int sweepMultiple)
    {
        _sweepProjector.SetSweepMultiple(sweepMultiple);
    }

    /// <summary>
    /// Request the watch test position (NIHS 95-10 / ISO 3158) new measurements
    /// are tagged with. Stored in the projector's volatile knob and applied on
    /// the analysis thread at the start of the next pass (the SetSweepMultiple
    /// flow). Callable from any thread.
    /// </summary>
    public void SetActivePosition(WatchPosition position)
    {
        _beatMetricsProjector.SetActivePosition(position);
    }

    /// <summary>
    /// Request a multi-position sequence restart: the per-position aggregates
    /// are cleared on the analysis thread at the start of the next pass (the
    /// SetActivePosition flow); the live series and overall statistics keep
    /// accumulating. Callable from any thread.
    /// </summary>
    public void ResetPositionAggregates()
    {
        _beatMetricsProjector.ResetPositionAggregates();
    }

    /// <summary>
    /// Request the Beat-Noise Scope 2 Σ averaging mode. Stored in the capture's
    /// volatile knob and applied on the analysis thread at the start of the
    /// next pass (the SetSweepMultiple flow); a change resets the averaging
    /// cycle. Callable from any thread.
    /// </summary>
    public void SetSigmaAveraging(bool enabled)
    {
        _beatSegmentCapture.SetSigmaAveraging(enabled);
    }

    /// <summary>
    /// Request a sound-print background recolor (e.g. on UI theme toggle). The change
    /// is applied on the analysis thread and an updated image frame is published.
    /// Callable from any thread.
    /// </summary>
    public void SetSoundBackgroundColor(uint backgroundColor)
    {
        lock (_recolorLock)
        {
            _pendingSoundBackground = backgroundColor;
        }
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
            ApplyPendingRecolor();
            HandleInputData();
        }
    }

    private void ApplyPendingRecolor()
    {
        uint background;
        lock (_recolorLock)
        {
            if (_pendingSoundBackground is not uint pending)
            {
                return;
            }
            background = pending;
            _pendingSoundBackground = null;
        }

        // Re-tint on the analysis thread (safe: same thread that writes the pixel
        // buffer) and flag the image for republish. The next regular frame carries
        // the new colors — within ~one frame while streaming. We deliberately do NOT
        // publish a standalone frame here: a frame with no scope series / zero stats
        // would become the "last frame" and blank the rate/scope plots on tab switch.
        _soundPrintProjector.SetBackgroundColor(background);
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
        DetectorMetricsBlockUpdate? lastPipelineUpdate = null;

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
            _spectrogramProjector.ProcessSamples(block);
            _multiFilterProjector.ProcessSamples(block);

            DetectorMetricsBlockUpdate pipelineUpdate = _pipeline.Process(block);
            lastPipelineUpdate = pipelineUpdate;
            if (pipelineUpdate.Result.SyncStatus == TgSyncStatus.Synced &&
                pipelineUpdate.Result.MeasuredPeriodS > 0.0)
            {
                _latestBeatPeriodS = pipelineUpdate.Result.MeasuredPeriodS;
            }
            _scopeRateProjector.Project(pipelineUpdate, frame);
            _soundPrintProjector.Project(pipelineUpdate);
            _beatMetricsProjector.Project(pipelineUpdate);
            _beatSegmentCapture.Project(pipelineUpdate);
            _sweepProjector.Project(pipelineUpdate);
            _foregroundSampleCount += (ulong)read.SamplesCopied;
        }

        if (frame == null)
        {
            return;
        }

        UpdateForegroundStats(frame);

        _scopeRateProjector.AppendSnapshot(frame);
        _soundPrintProjector.AppendSnapshot(frame);
        _spectrogramProjector.AppendSnapshot(frame);
        _beatMetricsProjector.AppendSnapshot(frame);
        _beatSegmentCapture.AppendSnapshot(frame);
        _sweepProjector.AppendSnapshot(frame);
        _multiFilterProjector.AppendSnapshot(frame);

        MasterAudioBufferSnapshot endSnapshot = _rawAudio.GetSnapshot();
        frame.AnalysisLagSamples = endSnapshot.TotalSamplesWritten > sourceSampleEnd
            ? endSnapshot.TotalSamplesWritten - sourceSampleEnd
            : 0;
        frame.ProcessingElapsedMs = processingTimer.Elapsed.TotalMilliseconds;

        if (_deadlineMonitor.Observe(frame.AnalysisLagSamples, _config.SampleRate, _latestBeatPeriodS))
        {
            ApplyDeadlineLevel(_deadlineMonitor.Level);
        }
        frame.DeadlineDegradationLevel = _deadlineMonitor.Level;

        StampDiagnostics(frame, lastPipelineUpdate, sourceSampleEnd);
        AnalysisFrameReady?.Invoke(frame);
    }

    /// <summary>
    /// Latency / missed-beat instrumentation (QA: report capture-to-processing,
    /// processing-to-display and end-to-end latency plus dropped-block and
    /// missed-beat counts). Capture time comes from the ring buffer's write-stamp
    /// ring keyed by the frame's newest sample; the display leg is stamped by the
    /// UI when it renders the frame.
    /// </summary>
    private void StampDiagnostics(AnalysisFrame frame, DetectorMetricsBlockUpdate? lastPipelineUpdate, ulong sourceSampleEnd)
    {
        if (lastPipelineUpdate != null)
        {
            frame.MissedBeats = lastPipelineUpdate.Result.MissedBeats;
            frame.SyncLossCount = lastPipelineUpdate.Result.SyncLossCount;
        }

        if (_rawAudio.TryGetCaptureTimestamp(sourceSampleEnd, out long captureTicks, out bool isLowerBound))
        {
            frame.CaptureTimestamp = captureTicks;
            frame.CaptureTimestampIsLowerBound = isLowerBound;
        }

        frame.ProcessingCompletedTimestamp = Stopwatch.GetTimestamp();
    }

    /*
        ApplyDeadlineLevel()
        --------------------
        Graceful-degradation ladder, cheapest visual cost first:
            level 1: stop redrawing the in-progress sound-print column and the
                     spectrogram live-edge cursor
            level 2: stretch the sound-print and spectrogram publish intervals
                     100 ms -> 400 ms, and the sweep / multi-filter series
                     publish floors 50 ms -> 400 ms (the floor must exceed the
                     per-pass stream advance of a sustained 2-beat breach,
                     >= 250 ms at 28800 BPH, or it never gates during the
                     breach and only sheds in recovery)
            level 3: coarsen the scope decimation stride 2x and suspend new
                     beat-segment windows (the Beat-Noise tab stops advancing)
        Idempotent per level; de-escalation restores the knobs the same way.
        Runs on the analysis thread between frames.
    */
    private void ApplyDeadlineLevel(int level)
    {
        _soundPrintProjector.SetLivePreviewEnabled(level < 1);
        _spectrogramProjector.SetLivePreviewEnabled(level < 1);
        _soundPrintProjector.SetPublishIntervalScale(level < 2 ? 1 : 4);
        _spectrogramProjector.SetPublishIntervalScale(level < 2 ? 1 : 4);
        _sweepProjector.SetPublishIntervalScale(level < 2 ? 1 : 8);
        _multiFilterProjector.SetPublishIntervalScale(level < 2 ? 1 : 8);
        _scopeRateProjector.SetScopeStrideScale(level < 3 ? 1 : 2);
        _beatSegmentCapture.SetCaptureSuspended(level >= 3);
    }

    private void DrainAndFlushInput()
    {
        // Mirror the steady-state pass order: a recolor requested just before
        // completion must reach the force-published final frame. Both drain
        // entry points run on the thread that owns the pixel buffers.
        ApplyPendingRecolor();
        HandleInputDataCore(stopInterruptible: false);

        var processingTimer = Stopwatch.StartNew();
        MasterAudioBufferSnapshot snapshot = _rawAudio.GetSnapshot();
        var frame = new AnalysisFrame
        {
            SessionId = _config.SessionId,
            SourceId = _nextFrameSourceId++,
            SampleRate = _config.SampleRate,
            PendingSamples = 0,
            BackgroundFps = snapshot.Fps,
            BackgroundSps = snapshot.Sps,
            BackgroundSpf = snapshot.Spf,
        };

        DetectorMetricsBlockUpdate flushUpdate = _pipeline.Flush();
        _scopeRateProjector.Project(flushUpdate, frame);
        _soundPrintProjector.Project(flushUpdate);
        _beatMetricsProjector.Project(flushUpdate);
        _beatSegmentCapture.Project(flushUpdate);
        _sweepProjector.Project(flushUpdate);
        _scopeRateProjector.AppendSnapshot(frame);
        _soundPrintProjector.AppendSnapshot(frame, force: true);
        _spectrogramProjector.AppendSnapshot(frame, force: true);
        _beatMetricsProjector.AppendSnapshot(frame);
        _beatSegmentCapture.AppendSnapshot(frame);
        _sweepProjector.AppendSnapshot(frame, force: true);
        // The flush pass has no new raw block to filter (the drain above already
        // consumed it); republish the latest filter window on the final frame.
        _multiFilterProjector.AppendSnapshot(frame, force: true);
        frame.ProcessingElapsedMs = processingTimer.Elapsed.TotalMilliseconds;
        frame.DeadlineDegradationLevel = _deadlineMonitor.Level;

        StampDiagnostics(frame, flushUpdate, snapshot.TotalSamplesWritten);
        AnalysisFrameReady?.Invoke(frame);
    }

    // The original accumulates samples inside the slice loop but counts the
    // frame and evaluates the 2-second window once per handler pass
    // (MainWindow.cpp ProcessSamples): FPS = passes/s, SPF = samples/pass.
    private void UpdateForegroundStats(AnalysisFrame frame)
    {
        _foregroundFrameCount++;

        double currentTime = _foregroundTimer.ElapsedMilliseconds / 1000.0;
        if (currentTime - _foregroundLastTime > 2)
        {
            double fdelta = currentTime - _foregroundLastTime;
            frame.ForegroundFps = _foregroundFrameCount / fdelta;
            frame.ForegroundSps = _foregroundSampleCount / fdelta;
            // Original: mForegroundSampleCount/mForegroundFrameCount with both
            // uint64_t -> integer division.
            frame.ForegroundSpf = _foregroundSampleCount / _foregroundFrameCount;
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
