using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Tabs;

public sealed class AnalysisFrameRouter
{
    private readonly Dictionary<string, IAnalysisFrameConsumer> _consumers;

    public AnalysisFrameRouter(IEnumerable<IAnalysisFrameConsumer> consumers)
    {
        _consumers = consumers.ToDictionary(consumer => consumer.TabId, StringComparer.Ordinal);
    }

    public bool HasConsumer(string tabId)
    {
        return _consumers.ContainsKey(tabId);
    }

    public void Route(AnalysisFrame frame, string activeTabId, AnalysisTabRenderContext context)
    {
        foreach (IAnalysisFrameConsumer consumer in _consumers.Values)
        {
            consumer.ObserveFrame(frame);
        }

        if (_consumers.TryGetValue(activeTabId, out IAnalysisFrameConsumer? activeConsumer))
        {
            activeConsumer.RenderFrame(frame, context);
        }
    }
}
