using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

internal sealed class RateScopeFrameConsumer : IAnalysisFrameConsumer
{
    private readonly RateScopeRenderer _renderer;

    public RateScopeFrameConsumer(RateScopeRenderer renderer)
    {
        _renderer = renderer;
    }

    public string TabId => InfoTabCatalog.RateScopeTabId;

    public void Initialize(AnalysisTabResetContext context)
    {
        _renderer.CreateGraphs(context.RateErrorYScale, context.RateDataPoints);
    }

    public void Reset(AnalysisTabResetContext context)
    {
        _renderer.Reset(context.RateErrorYScale, context.RateDataPoints);
    }

    public void ObserveFrame(AnalysisFrame frame)
    {
        _ = frame;
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        _renderer.RenderFrame(frame, context);
    }
}
