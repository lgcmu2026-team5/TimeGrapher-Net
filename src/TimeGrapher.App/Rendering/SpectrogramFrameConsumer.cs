using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

internal sealed class SpectrogramFrameConsumer : IAnalysisFrameConsumer
{
    private readonly SpectrogramRenderer _renderer;
    private PixelBuffer? _latestSpectrogramImage;

    public SpectrogramFrameConsumer(SpectrogramRenderer renderer)
    {
        _renderer = renderer;
    }

    public string TabId => InfoTabCatalog.SpectrogramTabId;

    internal PixelBuffer? LatestSpectrogramImage => _latestSpectrogramImage;

    public void Initialize(AnalysisTabResetContext context)
    {
        _ = context;
        _renderer.InitializeLegend();
    }

    public void Reset(AnalysisTabResetContext context)
    {
        _ = context;
        _latestSpectrogramImage = null;
        _renderer.Reset();
    }

    public void ObserveFrame(AnalysisFrame frame)
    {
        if (frame.SpectrogramImageUpdated && frame.SpectrogramImage != null)
        {
            _latestSpectrogramImage = frame.SpectrogramImage;
        }
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        // The spectrogram is a Core-built pixel image (x = pixel columns of the
        // recent window, not stream time), so the review-cursor contract does
        // not apply; pause already freezes the image for inspection.
        _ = context;
        ObserveFrame(frame);
        if (frame.SpectrogramImageUpdated && frame.SpectrogramImage != null)
        {
            _renderer.RenderFrame(frame);
        }
        else if (_latestSpectrogramImage != null)
        {
            _renderer.RenderImage(_latestSpectrogramImage);
        }
    }
}
