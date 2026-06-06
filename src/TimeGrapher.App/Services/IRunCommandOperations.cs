namespace TimeGrapher.App.Services;

internal interface IRunCommandOperations
{
    bool IsClosing { get; }

    bool HasActiveWorker { get; }

    RunCommandMode CurrentMode { get; }

    void ConfigureLiveAudio();

    Task<bool> StartLiveAsync();

    Task<bool> StartPlaybackAsync();

    Task<bool> StartSimulationAsync();

    void SetWorkersPaused(bool paused);

    void CleanupFailedStart();

    Task ShowStartFailureAsync(Exception exception);

    RunCommandStopOutcome StopLive();

    RunCommandStopOutcome StopPlayback();

    RunCommandStopOutcome StopSimulation();

    bool CloseAudio();

    void InvalidateRunSession();

    void RestorePlaybackOrSimulationAudioState();
}
