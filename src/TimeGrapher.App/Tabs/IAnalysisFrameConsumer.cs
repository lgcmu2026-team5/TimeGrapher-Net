using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Tabs;

public interface IAnalysisFrameConsumer
{
    string TabId { get; }

    void Initialize(AnalysisTabResetContext context);

    void Reset(AnalysisTabResetContext context);

    void ObserveFrame(AnalysisFrame frame);

    void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context);
}
