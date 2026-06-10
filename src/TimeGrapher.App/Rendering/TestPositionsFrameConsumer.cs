using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

internal sealed class TestPositionsFrameConsumer : IAnalysisFrameConsumer
{
    private readonly TestPositionsRenderer _positionRenderer;
    private readonly MultiPositionSeqRenderer _sequenceRenderer;

    public TestPositionsFrameConsumer(
        TestPositionsRenderer positionRenderer,
        MultiPositionSeqRenderer sequenceRenderer)
    {
        _positionRenderer = positionRenderer;
        _sequenceRenderer = sequenceRenderer;
    }

    public string TabId => InfoTabCatalog.TestPositionsTabId;

    public void Initialize(AnalysisTabResetContext context)
    {
        _positionRenderer.Reset();
        _sequenceRenderer.Reset();
    }

    public void Reset(AnalysisTabResetContext context)
    {
        _positionRenderer.Reset();
        _sequenceRenderer.Reset();
    }

    public void ObserveFrame(AnalysisFrame frame)
    {
        // History is cumulative on the frame; nothing to accumulate UI-side.
        _ = frame;
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        // No time axis on this tab, so the review-cursor contract does not apply.
        _ = context;
        _positionRenderer.RenderFrame(frame);
        _sequenceRenderer.RenderFrame(frame);
    }
}
