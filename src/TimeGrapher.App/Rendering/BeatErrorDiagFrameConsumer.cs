using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

internal sealed class BeatErrorDiagFrameConsumer : IAnalysisFrameConsumer, IThemedFrameConsumer
{
    private readonly BeatErrorDiagRenderer _renderer;

    public BeatErrorDiagFrameConsumer(BeatErrorDiagRenderer renderer)
    {
        _renderer = renderer;
    }

    public string TabId => InfoTabCatalog.BeatErrorDiagTabId;

    public void ApplyTheme(PlotThemePalette theme)
    {
        _renderer.ApplyTheme(theme);
    }

    public void Initialize(AnalysisTabResetContext context)
    {
        _renderer.CreateGraphs(context.RateErrorYScale, context.RateDataPoints);
    }

    public void Reset(AnalysisTabResetContext context)
    {
        _renderer.CreateGraphs(context.RateErrorYScale, context.RateDataPoints);
    }

    public void ObserveFrame(AnalysisFrame frame)
    {
        // Rate series and history snapshots are cumulative on the frame;
        // nothing to accumulate UI-side.
        _ = frame;
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        _renderer.RenderFrame(frame, context);
    }
}
