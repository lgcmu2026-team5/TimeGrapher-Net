using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

internal sealed class VarioFrameConsumer : IAnalysisFrameConsumer, IThemedFrameConsumer
{
    private readonly VarioRenderer _renderer;

    public VarioFrameConsumer(VarioRenderer renderer)
    {
        _renderer = renderer;
    }

    public string TabId => InfoTabCatalog.VarioTabId;

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
        // Statistics ride the cumulative snapshot; nothing to accumulate UI-side.
        _ = frame;
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        _renderer.RenderFrame(frame, context);
    }
}
