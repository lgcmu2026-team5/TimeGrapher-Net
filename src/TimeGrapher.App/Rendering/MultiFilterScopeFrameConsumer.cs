using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

internal sealed class MultiFilterScopeFrameConsumer : IAnalysisFrameConsumer, IThemedFrameConsumer
{
    private readonly MultiFilterScopeRenderer _renderer;

    public MultiFilterScopeFrameConsumer(MultiFilterScopeRenderer renderer)
    {
        _renderer = renderer;
    }

    public string TabId => InfoTabCatalog.MultiFilterScopeTabId;

    public void ApplyTheme(PlotThemePalette theme)
    {
        _renderer.ApplyTheme(theme);
    }

    public void Initialize(AnalysisTabResetContext context)
    {
        _renderer.CreateGraphs();
    }

    public void Reset(AnalysisTabResetContext context)
    {
        _renderer.Reset();
    }

    public void ObserveFrame(AnalysisFrame frame)
    {
        // The filter windows accumulate in Core and ride the frame as replace
        // snapshots; nothing to accumulate UI-side.
        _ = frame;
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        _renderer.RenderFrame(frame, context);
    }
}
