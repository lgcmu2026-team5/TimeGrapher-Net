namespace TimeGrapher.Core.Shared;

public sealed class WorkerPauseGate : IDisposable
{
    private readonly ManualResetEventSlim _gate = new(initialState: true);
    private int _isPaused;

    public bool IsPaused => Volatile.Read(ref _isPaused) != 0;

    public void SetPaused(bool paused)
    {
        Volatile.Write(ref _isPaused, paused ? 1 : 0);
        if (paused)
        {
            _gate.Reset();
        }
        else
        {
            _gate.Set();
        }
    }

    public bool WaitWhilePaused(Func<bool> isCancellationRequested)
    {
        while (IsPaused)
        {
            if (isCancellationRequested())
            {
                return false;
            }

            _gate.Wait(TimeSpan.FromMilliseconds(50));
        }

        return !isCancellationRequested();
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
