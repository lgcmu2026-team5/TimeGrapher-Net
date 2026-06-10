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

    public void Project(DetectorMetricsBlockUpdate update)
    {
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
