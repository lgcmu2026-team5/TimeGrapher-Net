using Avalonia.Controls;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Small UI-thread facade for shared analysis UI state. Tab-specific rendering lives
/// in RateScopeRenderer and SoundPrintRenderer.
/// </summary>
public sealed class GraphFrameRenderer
{
    private readonly IReadOnlyList<IAnalysisFrameConsumer> _consumers;
    private readonly TextBlock _results;

    public GraphFrameRenderer(
        IEnumerable<IAnalysisFrameConsumer> consumers,
        TextBlock resultsText)
    {
        _consumers = consumers.ToArray();
        _results = resultsText;
    }

    public void Initialize(AnalysisTabResetContext context)
    {
        foreach (IAnalysisFrameConsumer consumer in _consumers)
        {
            consumer.Initialize(context);
        }
    }

    public void Reset(AnalysisTabResetContext context)
    {
        foreach (IAnalysisFrameConsumer consumer in _consumers)
        {
            consumer.Reset(context);
        }
        _results.Text = "RATE ------ s/d   AMPLITUDE ---   BEAT ERROR ---- ms   BEAT ----- bph";
    }

    public void UpdateResults(AnalysisFrame frame)
    {
        if (frame.MetricsUpdate.ResultsUpdated)
        {
            _results.Text = frame.MetricsUpdate.ResultsText;
        }
    }
}
