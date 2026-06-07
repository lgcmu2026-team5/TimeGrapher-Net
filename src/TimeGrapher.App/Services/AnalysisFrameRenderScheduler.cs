using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Services;

internal sealed class AnalysisFrameRenderScheduler
{
    private readonly Action<Action> _postToUi;
    private readonly Func<int> _refreshIntervalMs;
    private readonly Action<AnalysisFrame, ulong> _renderFrame;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, Task> _delayAsync;
    private readonly object _lock = new();

    private DateTime _nextRenderUtc = DateTime.MinValue;
    private AnalysisFrame? _pendingFrame;
    private bool _renderScheduled;
    private ulong _generation;
    private ulong _droppedFrames;

    public AnalysisFrameRenderScheduler(
        Action<Action> postToUi,
        Func<int> refreshIntervalMs,
        Action<AnalysisFrame, ulong> renderFrame,
        Func<DateTime>? utcNow = null,
        Func<TimeSpan, Task>? delayAsync = null)
    {
        _postToUi = postToUi;
        _refreshIntervalMs = refreshIntervalMs;
        _renderFrame = renderFrame;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public void Enqueue(AnalysisFrame frame)
    {
        ulong generation;
        lock (_lock)
        {
            if (_pendingFrame != null)
            {
                _droppedFrames++;
                MergeTransientSignals(_pendingFrame, frame);
            }

            _pendingFrame = frame;
            generation = _generation;
            if (_renderScheduled)
            {
                return;
            }

            _renderScheduled = true;
        }

        _postToUi(() => ProcessPendingFrame(generation));
    }

    /// <summary>One-shot signals on a displaced frame must survive coalescing.</summary>
    private static void MergeTransientSignals(AnalysisFrame displaced, AnalysisFrame replacement)
    {
        replacement.InputOverrun |= displaced.InputOverrun;
        replacement.InputSamplesDropped += displaced.InputSamplesDropped;
        if (displaced.SoundImageUpdated && !replacement.SoundImageUpdated)
        {
            replacement.SoundImageUpdated = true;
            replacement.SoundImage ??= displaced.SoundImage;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _pendingFrame = null;
            _droppedFrames = 0;
            _renderScheduled = false;
            _generation++;
            _nextRenderUtc = DateTime.MinValue;
        }
    }

    public void ResetTiming()
    {
        lock (_lock)
        {
            _nextRenderUtc = DateTime.MinValue;
        }
    }

    private async Task DelayPendingRender(TimeSpan delay, ulong generation)
    {
        await _delayAsync(delay);
        _postToUi(() => ProcessPendingFrame(generation));
    }

    private void ProcessPendingFrame(ulong generation)
    {
        if (generation != _generation)
        {
            return;
        }

        DateTime now = _utcNow();
        if (now < _nextRenderUtc)
        {
            _ = DelayPendingRender(_nextRenderUtc - now, generation);
            return;
        }

        AnalysisFrame? frame;
        ulong droppedFrames;
        lock (_lock)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            droppedFrames = _droppedFrames;
            _droppedFrames = 0;
        }

        if (frame != null)
        {
            _renderFrame(frame, droppedFrames);
            _nextRenderUtc = _utcNow().AddMilliseconds(_refreshIntervalMs());
        }

        lock (_lock)
        {
            if (_pendingFrame != null)
            {
                _ = DelayPendingRender(
                    TimeSpan.FromMilliseconds(_refreshIntervalMs()),
                    generation);
            }
            else
            {
                _renderScheduled = false;
            }
        }
    }
}
