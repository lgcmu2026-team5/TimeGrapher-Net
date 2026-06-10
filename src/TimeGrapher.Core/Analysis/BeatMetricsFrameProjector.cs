using TimeGrapher.Core.Metrics;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Analysis;

/// <summary>
/// Projects the per-event metrics updates of each analysis pass into the
/// cumulative <see cref="BeatMetricsHistory"/> and attaches its snapshot to the
/// outgoing frame. Sibling of <see cref="ScopeRateFrameProjector"/>; runs on the
/// analysis thread only.
/// </summary>
public sealed class BeatMetricsFrameProjector
{
    private readonly BeatMetricsHistory _history = new();

    // Written from any thread (UI position buttons), applied analysis-side at
    // the start of the next Project pass (the SweepFrameProjector knob pattern).
    private volatile int _requestedPosition = (int)WatchPosition.CH;
    private volatile bool _positionAggregateResetRequested;

    /// <summary>
    /// Requests the watch test position subsequent beats are tagged with.
    /// Thread-safe; applied on the analysis thread before the next events are
    /// recorded, so a beat is never tagged with a half-applied position.
    /// </summary>
    public void SetActivePosition(WatchPosition position)
    {
        _requestedPosition = (int)position;
    }

    /// <summary>
    /// Requests a multi-position sequence restart: the per-position aggregates
    /// clear on the analysis thread at the start of the next Project pass (the
    /// SetActivePosition knob flow), so the clear never races a beat being
    /// recorded. Thread-safe.
    /// </summary>
    public void ResetPositionAggregates()
    {
        _positionAggregateResetRequested = true;
    }

    public void Project(DetectorMetricsBlockUpdate update)
    {
        if (_positionAggregateResetRequested)
        {
            _positionAggregateResetRequested = false;
            _history.ResetPositionAggregates();
        }

        _history.SetActivePosition((WatchPosition)_requestedPosition);
        foreach (DetectedEventUpdate eventUpdate in update.Events)
        {
            _history.Record(eventUpdate.MetricsUpdate);
        }
    }

    public void AppendSnapshot(AnalysisFrame frame)
    {
        frame.MetricsHistory = _history.CurrentSnapshot();
    }
}
