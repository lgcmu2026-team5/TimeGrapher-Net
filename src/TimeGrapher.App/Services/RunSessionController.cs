using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Services;

internal enum RunSessionStopOutcome
{
    Stopped,
    Stopping,
}

internal sealed class RunSessionController : IDisposable
{
    private const int WorkerStopTimeoutMs = 2000;

    private readonly Func<ulong, AnalysisWorker.Config> _createAnalysisConfig;
    private readonly Action _resetBeforeRun;
    private readonly Action _clearPendingFrames;
    private readonly Action _resetRenderTiming;
    private readonly Action<AnalysisFrame> _onAnalysisFrameReady;
    private readonly Action<string> _setStatus;

    private MasterAudioBuffer? _rawAudio;
    private AnalysisWorker? _analysisWorker;
    private IAudioInputWorker? _inputWorker;
    private Action? _inputDataReadyHandler;
    private Action? _inputCompletionDetach;
    private ulong _runSessionToken;
    private int? _sweepMultiple;
    private WatchPosition? _activePosition;
    private bool? _sigmaAveraging;

    public RunSessionController(
        Func<ulong, AnalysisWorker.Config> createAnalysisConfig,
        Action resetBeforeRun,
        Action clearPendingFrames,
        Action resetRenderTiming,
        Action<AnalysisFrame> onAnalysisFrameReady,
        Action<string> setStatus)
    {
        _createAnalysisConfig = createAnalysisConfig;
        _resetBeforeRun = resetBeforeRun;
        _clearPendingFrames = clearPendingFrames;
        _resetRenderTiming = resetRenderTiming;
        _onAnalysisFrameReady = onAnalysisFrameReady;
        _setStatus = setStatus;
    }

    public ulong AnalysisSessionId { get; private set; }

    public bool HasActiveInputWorker => _inputWorker != null;

    public MasterAudioBuffer PrepareInputRun(int sampleRate, out ulong runSessionToken)
    {
        runSessionToken = BeginRunSession();
        StopAnalysisThread();
        _resetBeforeRun();

        _rawAudio = new MasterAudioBuffer(sampleRate);
        StartAnalysisThread();

        return _rawAudio;
    }

    public void AttachInputWorker(
        IAudioInputWorker worker,
        ulong runSessionToken,
        Action? detachCompletion = null)
    {
        _inputWorker = worker;
        _inputCompletionDetach = detachCompletion;
        _inputDataReadyHandler = CreateDataReadyHandler(runSessionToken);
        worker.DataReady += _inputDataReadyHandler;
    }

    public void InvalidateRunSession()
    {
        _ = BeginRunSession();
    }

    public bool IsCurrentRunSession(ulong runSessionToken)
    {
        return runSessionToken == _runSessionToken;
    }

    public RunSessionStopOutcome StopInputWorker(string workerName)
    {
        IAudioInputWorker? worker = _inputWorker;
        if (worker != null)
        {
            if (_inputDataReadyHandler != null)
            {
                worker.DataReady -= _inputDataReadyHandler;
                _inputDataReadyHandler = null;
            }

            if (!worker.TryStop(TimeSpan.FromMilliseconds(WorkerStopTimeoutMs)))
            {
                _setStatus(workerName + " worker did not stop within timeout");
                return RunSessionStopOutcome.Stopping;
            }

            _inputCompletionDetach?.Invoke();
            _inputCompletionDetach = null;
            worker.Dispose();
            _inputWorker = null;
        }

        return RunSessionStopOutcome.Stopped;
    }

    public RunSessionStopOutcome StopAnalysisThread(bool completeInput = false)
    {
        if (_analysisWorker != null)
        {
            bool stopped = completeInput
                ? _analysisWorker.CompleteInput(TimeSpan.FromMilliseconds(WorkerStopTimeoutMs))
                : _analysisWorker.TryStop(TimeSpan.FromMilliseconds(WorkerStopTimeoutMs));
            if (stopped)
            {
                _analysisWorker.AnalysisFrameReady -= _onAnalysisFrameReady;
                _analysisWorker.Dispose();
                _analysisWorker = null;
                if (completeInput)
                {
                    _resetRenderTiming();
                }
                else
                {
                    AnalysisSessionId++;
                    _clearPendingFrames();
                }
            }
            else
            {
                _setStatus("Analysis worker did not stop within timeout");
                return RunSessionStopOutcome.Stopping;
            }
        }
        else
        {
            _clearPendingFrames();
        }

        return RunSessionStopOutcome.Stopped;
    }

    public void SetWorkersPaused(bool paused)
    {
        _inputWorker?.SetPaused(paused);
    }

    public void SetLiveInputVolume(float normalizedVolume)
    {
        if (_inputWorker is ILiveAudioWorker liveWorker)
        {
            liveWorker.SetVolume(normalizedVolume);
        }
    }

    /// <summary>Recolors the running analysis worker's sound print (no-op when idle).</summary>
    public void SetSoundBackgroundColor(uint backgroundColor)
    {
        _analysisWorker?.SetSoundBackgroundColor(backgroundColor);
    }

    /// <summary>
    /// Forwards the Scope Sweep window multiple to the running analysis worker
    /// and remembers it so later runs start with the user's selection.
    /// </summary>
    public void SetSweepMultiple(int sweepMultiple)
    {
        _sweepMultiple = sweepMultiple;
        _analysisWorker?.SetSweepMultiple(sweepMultiple);
    }

    /// <summary>
    /// Forwards the active watch test position to the running analysis worker
    /// and remembers it so later runs start with the user's selection.
    /// </summary>
    public void SetActivePosition(WatchPosition position)
    {
        _activePosition = position;
        _analysisWorker?.SetActivePosition(position);
    }

    /// <summary>
    /// Forwards the Beat-Noise Scope 2 Σ averaging mode to the running analysis
    /// worker and remembers it so later runs start with the user's selection.
    /// </summary>
    public void SetSigmaAveraging(bool enabled)
    {
        _sigmaAveraging = enabled;
        _analysisWorker?.SetSigmaAveraging(enabled);
    }

    /// <summary>
    /// Clears the running analysis worker's per-position aggregates so a new
    /// multi-position sequence starts (no-op when idle — a fresh run begins
    /// with empty aggregates anyway).
    /// </summary>
    public void ResetPositionAggregates()
    {
        _analysisWorker?.ResetPositionAggregates();
    }

    public void Dispose()
    {
        InvalidateRunSession();
        StopInputWorker("Input");
        StopAnalysisThread();
    }

    private ulong BeginRunSession()
    {
        unchecked
        {
            _runSessionToken++;
            if (_runSessionToken == 0)
            {
                _runSessionToken = 1;
            }

            return _runSessionToken;
        }
    }

    private void StartAnalysisThread()
    {
        AnalysisSessionId++;

        AnalysisWorker.Config analysisConfig = _createAnalysisConfig(AnalysisSessionId);

        _analysisWorker = new AnalysisWorker(_rawAudio!, analysisConfig);
        if (_sweepMultiple is int sweepMultiple)
        {
            _analysisWorker.SetSweepMultiple(sweepMultiple);
        }
        if (_activePosition is WatchPosition activePosition)
        {
            _analysisWorker.SetActivePosition(activePosition);
        }
        if (_sigmaAveraging is bool sigmaAveraging)
        {
            _analysisWorker.SetSigmaAveraging(sigmaAveraging);
        }
        _analysisWorker.AnalysisFrameReady += _onAnalysisFrameReady;
        _analysisWorker.Start();
    }

    private Action CreateDataReadyHandler(ulong runSessionToken)
    {
        return () =>
        {
            if (runSessionToken == _runSessionToken)
            {
                _analysisWorker?.NotifyDataReady();
            }
        };
    }
}
