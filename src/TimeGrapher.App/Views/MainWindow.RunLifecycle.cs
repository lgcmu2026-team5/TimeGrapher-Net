using System;
using System.Globalization;
using System.Threading.Tasks;

using Avalonia.Threading;

using TimeGrapher.App.Audio;
using TimeGrapher.App.Services;
using TimeGrapher.Core.AudioIo;
using TimeGrapher.Core.Shared;
using TimeGrapher.Core.Sim;

namespace TimeGrapher.App.Views;

public partial class MainWindow
{
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // ~MainWindow: StopAnalysisThread(); plus stop any running input worker.
        mIsClosing = true;
        mViewModel.PropertyChanged -= mSelectionCoordinator.OnViewModelPropertyChanged;
        mViewModel.PropertyChanged -= OnRunControlPropertyChanged;
        mViewModel.PropertyChanged -= OnReviewCursorPropertyChanged;
        mRunSessionController.InvalidateRunSession();
        mRunSessionController.StopInputWorker("Input");
        mRunSessionController.StopAnalysisThread();
        if (!AudioCloseCheck() && mWavWriter != null)
        {
            // Final shutdown: the retry surface is gone with the window, so give the
            // recording one last bounded close attempt (Dispose re-runs Close with
            // its 5s join) and release it regardless.
            mWavWriter.Dispose();
            mWavWriter = null;
        }
    }

    private void InvalidateRunSession()
    {
        mRunSessionController.InvalidateRunSession();
    }

    private void StartAudioThread()
    {
        int deviceNumber = CurrentInputDeviceNumber();
        if (deviceNumber < 0)
        {
            throw new InvalidOperationException("No live audio device is selected.");
        }

        MasterAudioBuffer buffer = mRunSessionController.PrepareInputRun(mCurrentSamplesPerSecond, out ulong runSessionToken);

        ILiveAudioWorker audioWorker = LiveAudioBackend.CreateWorker(buffer);
        Action captureEndedHandler = () => OnLiveCaptureEnded(runSessionToken);
        audioWorker.CaptureEnded += captureEndedHandler;
        mRunSessionController.AttachInputWorker(audioWorker, runSessionToken, () => audioWorker.CaptureEnded -= captureEndedHandler);
        audioWorker.Start(deviceNumber, mCurrentSamplesPerSecond, (float)(mViewModel.Gain / 1000.0));
    }

    private void OnLiveCaptureEnded(ulong runSessionToken)
    {
        // Fires on the capture thread; marshal to the UI thread.
        Dispatcher.UIThread.Post(() => HandleLiveCaptureEnded(runSessionToken));
    }

    private void HandleLiveCaptureEnded(ulong runSessionToken)
    {
        if (!mRunSessionController.IsCurrentRunSession(runSessionToken))
        {
            return;
        }

        // The capture process/device died without a stop request: bring the run
        // down through the normal stop path, then surface what happened (after
        // StopRun so the message survives the "Stopped" status).
        StopRun();
        mViewModel.StatusText = "Live audio capture ended unexpectedly";
    }

    private RunSessionStopOutcome StopAudioThread()
    {
        // LocalStopAudio -> StopAudioRecording.
        return mRunSessionController.StopInputWorker("Audio");
    }

    private void StartPlaybackThread(string fileName)
    {
        MasterAudioBuffer buffer = mRunSessionController.PrepareInputRun(mCurrentSamplesPerSecond, out ulong runSessionToken);

        var playbackWorker = new PlaybackWorker(buffer, mCurrentSamplesPerSecond);
        Action<PlaybackCompletionReason> doneHandler = reason => OnPlaybackDoneReadingFile(runSessionToken, reason);
        playbackWorker.DoneReadingFile += doneHandler;
        mRunSessionController.AttachInputWorker(playbackWorker, runSessionToken, () => playbackWorker.DoneReadingFile -= doneHandler);
        if (!playbackWorker.Start(fileName))
        {
            throw new InvalidOperationException("Playback worker is already running.");
        }
    }

    private void StartSimThread(WatchSynthStreamConfig cfg)
    {
        MasterAudioBuffer buffer = mRunSessionController.PrepareInputRun(mCurrentSamplesPerSecond, out ulong runSessionToken);

        var simWorker = new SimWorker(buffer, mCurrentSamplesPerSecond);
        Action<SimCompletionReason> doneHandler = reason => OnSimDone(runSessionToken, reason);
        simWorker.SimDone += doneHandler;
        mRunSessionController.AttachInputWorker(simWorker, runSessionToken, () => simWorker.SimDone -= doneHandler);
        if (!simWorker.Start(cfg))
        {
            throw new InvalidOperationException("Sim worker is already running.");
        }
    }

    private RunSessionStopOutcome StopPlaybackThread()
    {
        // requestInterruption(): cancel; the worker reports completion via DoneReadingFile,
        // but on_StopPushButton_clicked also calls StopAnalysisThread()/AudioCloseCheck() directly.
        return mRunSessionController.StopInputWorker("Playback");
    }

    private RunSessionStopOutcome StopSimThread()
    {
        return mRunSessionController.StopInputWorker("Sim");
    }

    private void OnPlaybackDoneReadingFile(ulong runSessionToken, PlaybackCompletionReason reason)
    {
        // PlaybackDoneReadingFile fires on the playback thread; marshal to UI thread.
        Dispatcher.UIThread.Post(() => HandlePlaybackDoneReadingFile(runSessionToken, reason));
    }

    private void OnSimDone(ulong runSessionToken, SimCompletionReason reason)
    {
        Dispatcher.UIThread.Post(() => HandleSimDone(runSessionToken, reason));
    }

    private void HandlePlaybackDoneReadingFile(ulong runSessionToken, PlaybackCompletionReason reason)
    {
        CompletePlaybackOrSimulationRun(
            runSessionToken,
            shouldRestoreAudioState: CurrentMode() == RunCommandMode.Playback,
            stopInputWorker: () => mRunSessionController.StopInputWorker("Playback"),
            failureStatus: "Playback failed",
            failed: reason == PlaybackCompletionReason.Failed);
    }

    private void HandleSimDone(ulong runSessionToken, SimCompletionReason reason)
    {
        CompletePlaybackOrSimulationRun(
            runSessionToken,
            shouldRestoreAudioState: CurrentMode() == RunCommandMode.Simulation,
            stopInputWorker: () => mRunSessionController.StopInputWorker("Sim"),
            failureStatus: "Simulation failed",
            failed: reason == SimCompletionReason.Failed);
    }

    private void CompletePlaybackOrSimulationRun(
        ulong runSessionToken,
        bool shouldRestoreAudioState,
        Func<RunSessionStopOutcome> stopInputWorker,
        string failureStatus,
        bool failed)
    {
        if (!mRunSessionController.IsCurrentRunSession(runSessionToken))
        {
            return;
        }

        InvalidateRunSession();
        SetGuiStoppingMode();
        if (shouldRestoreAudioState)
        {
            RestorePlaybackOrSimulationAudioState();
        }

        RunSessionStopOutcome outcome = stopInputWorker();
        outcome = CombineStopOutcome(outcome, mRunSessionController.StopAnalysisThread(completeInput: true));
        bool audioClosed = outcome == RunSessionStopOutcome.Stopped && AudioCloseCheck();
        if (outcome != RunSessionStopOutcome.Stopped || !audioClosed)
        {
            SetGuiStoppingMode();
            return;
        }

        SetGuiStopMode();
        mViewModel.StatusText = failed ? failureStatus : "Stopped";
    }

    private async Task<bool> RecordSessionCheck()
    {
        // A writer left over from a failed close must never leak into a new run.
        if (!AudioCloseCheck())
        {
            return false;
        }

        RecordingSessionStartResult result = await mRecordingSessionService.TryStartAsync(mCurrentSamplesPerSecond);
        if (result.Writer != null)
        {
            mWavWriter = result.Writer;
        }

        return result.ShouldContinue;
    }

    private bool AudioCloseCheck()
    {
        if (mWavWriter != null)
        {
            ulong droppedBlocks = mWavWriter.DroppedBlocks;
            bool closed = mWavWriter.Close();
            if (!closed)
            {
                mViewModel.StatusText = "Failed to close WAV recording cleanly";
                if (mWavWriter.IsOpen)
                {
                    // Retryable: the writer thread has not finished yet; a Stop
                    // retry re-attempts the close.
                    return false;
                }

                // Terminal: the writer already tore down, so a retry has nothing
                // left to redo. Release it now so no stale writer can leak into a
                // later run; the failure still surfaces once via the status text.
                mWavWriter.Dispose();
                mWavWriter = null;
                return false;
            }

            mWavWriter.Dispose();
            mWavWriter = null;
            if (droppedBlocks != 0)
            {
                mViewModel.StatusText = "WAV recording dropped " +
                                     droppedBlocks.ToString(CultureInfo.InvariantCulture) +
                                     " block(s)";
            }
        }

        return true;
    }

    private void SetGuiRunMode()
    {
        mViewModel.SetRunning();
    }

    private void SetGuiStoppingMode()
    {
        mViewModel.SetStopping();
    }

    private void SetGuiStopMode()
    {
        RunCommandMode mode = CurrentMode();
        mViewModel.SetModeAllowsSampleRate(RunCommandModePolicies.AllowsSelectableSampleRate(mode));
        mViewModel.SetModeAllowsGain(RunCommandModePolicies.AllowsGain(mode));
        mViewModel.SetStopped();
    }

    private async Task<bool> LiveStart()
    {
        if (!await RecordSessionCheck())
        {
            return false;
        }

        try
        {
            StartAudioThread();
        }
        catch (Exception ex)
        {
            InvalidateRunSession();
            mRunSessionController.StopInputWorker("Audio");
            mRunSessionController.StopAnalysisThread();
            AudioCloseCheck();
            mViewModel.StatusText = "Failed to start live audio";
            await mDialogs.ShowErrorAsync("Error", "Failed to start live audio: " + ex.Message);
            return false;
        }

        SetGuiRunMode();
        mViewModel.StatusText = "Running";
        return true;
    }

    private async Task<bool> PlaybackStart()
    {
        PlaybackFileSelectionResult selection = await mPlaybackFileService.SelectPlaybackFileAsync(mCurrentDir);
        if (!selection.Selected || selection.FilePath == null)
        {
            if (!string.IsNullOrEmpty(selection.StatusMessage))
            {
                mViewModel.StatusText = selection.StatusMessage;
            }

            return false;
        }

        mCurrentDir = selection.CurrentDirectory;
        GetAudioRate(out mRateBeforePlaybackOrSim);
        GetAudioDevice(out mDeviceNameBeforePlaybackOrSim);
        if (!SetAudioDevice(PLAYBACK_SOURCE))
        {
            Console.Error.WriteLine("SetAudioDevice Failed");
        }

        if (!SetAudioRate(selection.SampleRate))
        {
            Console.Error.WriteLine("SetAudioRate Failed");
            RestorePlaybackOrSimulationAudioState();
            return false;
        }

        if (!await RecordSessionCheck())
        {
            RestorePlaybackOrSimulationAudioState();
            return false;
        }

        StartPlaybackThread(selection.FilePath);
        SetGuiRunMode();
        mViewModel.StatusText = "Running";
        return true;
    }

    private async Task<bool> SimStart()
    {
        // RealisticCheckBox -> realistic config; otherwise clean config
        // (MainWindow.cpp: watch_synth_stream_realistic_config / watch_synth_stream_clean_config).
        WatchSynthStreamConfig cfg = mViewModel.Realistic
            ? WatchSynthStreamConfig.Realistic()
            : WatchSynthStreamConfig.Clean();

        SimulationSelection selection = mRunSelectionResolver.GetSimulationSelection(mAvailableRates, mNumberOfRates);
        cfg.Bph = selection.Bph;
        cfg.SampleRateHz = (uint)selection.SampleRate;
        cfg.BeatErrorMs = -(double)mViewModel.SimBeatError;
        cfg.PcmPeakAmplitude = 0.40; // normalized float PCM digital output level
        cfg.WatchAmplitudeDegrees = (double)mViewModel.SimAmplitude;
        cfg.LiftAngleDegrees = (double)mViewModel.LiftAngle;
        cfg.RateErrorSPerDay = (double)mViewModel.SimErrorRate;

        if (!await RecordSessionCheck())
        {
            return false;
        }

        GetAudioRate(out mRateBeforePlaybackOrSim);
        GetAudioDevice(out mDeviceNameBeforePlaybackOrSim);
        if (!SetAudioDevice(SIMULATION_SOURCE))
        {
            Console.Error.WriteLine("SetAudioDevice Failed");
        }

        if (!SetAudioRate(mRateBeforePlaybackOrSim))
        {
            Console.Error.WriteLine("SetAudioRate Failed");
        }

        StartSimThread(cfg);
        SetGuiRunMode();
        mViewModel.StatusText = "Running";
        return true;
    }

    private async Task StartRunAsync()
    {
        await mRunCommandService.StartAsync();
    }

    private void TogglePauseRun()
    {
        mRunCommandService.TogglePause();
    }

    private void SetWorkersPaused(bool paused)
    {
        mRunSessionController.SetWorkersPaused(paused);
    }

    private void StopRun()
    {
        mRunCommandService.Stop();
    }
}
