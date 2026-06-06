namespace TimeGrapher.Core.Shared;

public interface IAudioInputWorker : IDisposable
{
    event Action? DataReady;

    bool IsPaused { get; }

    void SetPaused(bool paused);

    bool TryStop(TimeSpan timeout);
}
