using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

internal sealed class SoundPrintFrameConsumer : IAnalysisFrameConsumer
{
    private readonly SoundPrintRenderer _renderer;
    private PixelBuffer? _latestSoundImage;

    public SoundPrintFrameConsumer(SoundPrintRenderer renderer)
    {
        _renderer = renderer;
    }

    public string TabId => InfoTabCatalog.SoundPrintTabId;

    internal PixelBuffer? LatestSoundImage => _latestSoundImage;

    public void Initialize(AnalysisTabResetContext context)
    {
        _ = context;
    }

    public void Reset(AnalysisTabResetContext context)
    {
        _ = context;
        _latestSoundImage = null;
        _renderer.Reset();
    }

    public void ObserveFrame(AnalysisFrame frame)
    {
        if (frame.SoundImageUpdated && frame.SoundImage != null)
        {
            _latestSoundImage = frame.SoundImage;
        }
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        _ = context;
        ObserveFrame(frame);
        if (frame.SoundImageUpdated && frame.SoundImage != null)
        {
            _renderer.RenderFrame(frame);
        }
        else if (_latestSoundImage != null)
        {
            _renderer.RenderImage(_latestSoundImage);
        }
    }
}
