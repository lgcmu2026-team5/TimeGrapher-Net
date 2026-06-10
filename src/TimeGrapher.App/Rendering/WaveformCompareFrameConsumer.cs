using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

internal sealed class WaveformCompareFrameConsumer : IAnalysisFrameConsumer, IThemedFrameConsumer
{
    private readonly WaveformCompareRenderer _renderer;

    public WaveformCompareFrameConsumer(WaveformCompareRenderer renderer)
    {
        _renderer = renderer;
    }

    public string TabId => InfoTabCatalog.WaveformCompareTabId;

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
        // Segments and history are cumulative on the frame; nothing to accumulate UI-side.
        _ = frame;
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        _renderer.RenderFrame(frame, context);
    }
}
