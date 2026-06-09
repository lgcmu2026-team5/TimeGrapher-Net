using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// No-op frame consumer for reserved placeholder tabs that have no analysis content yet.
/// Keeps the router/registry contracts satisfied until the real feature is implemented.
/// </summary>
internal sealed class PlaceholderFrameConsumer : IAnalysisFrameConsumer
{
    public PlaceholderFrameConsumer(string tabId)
    {
        TabId = tabId;
    }

    public string TabId { get; }

    public void Initialize(AnalysisTabResetContext context)
    {
    }

    public void Reset(AnalysisTabResetContext context)
    {
    }

    public void ObserveFrame(AnalysisFrame frame)
    {
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
    }
}
