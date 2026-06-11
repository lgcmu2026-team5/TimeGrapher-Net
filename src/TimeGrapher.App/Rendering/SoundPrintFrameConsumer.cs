using System.Diagnostics.CodeAnalysis;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

internal sealed class SoundPrintFrameConsumer : IAnalysisFrameConsumer, IThemedFrameConsumer
{
    private readonly SoundPrintRenderer _renderer;
    // The worker buffer that produced the displayed image. A theme toggle
    // replaces _latestSoundImage with a remapped copy while this keeps
    // pointing at the original, so a re-routed kept frame (same pooled
    // buffer) is recognized and cannot restore the old-background image.
    private PixelBuffer? _latestSourceImage;
    private PixelBuffer? _latestSoundImage;
    private uint _displayedBackground = PlotThemePalette.Current.ScopeBg;

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
        _latestSourceImage = null;
        _latestSoundImage = null;
        _renderer.Reset();
    }

    // After a stop there is no live worker to recolor and republish the sound
    // print (RunSessionController.SetSoundBackgroundColor is a no-op when
    // idle), so remap the kept image's background here. The worker fills the
    // background with exactly the palette ScopeBg, making the swap exact; the
    // remap writes into a copy because the published buffer belongs to the
    // worker's publish pool.
    public void ApplyTheme(PlotThemePalette theme)
    {
        if (TryRemapKeptImage(theme.ScopeBg, out PixelBuffer? remapped))
        {
            _renderer.RenderImage(remapped);
        }
    }

    // Internal seam: the remap state machine is testable without the blit,
    // which needs the Avalonia platform.
    internal bool TryRemapKeptImage(uint newBackground, [NotNullWhen(true)] out PixelBuffer? remapped)
    {
        uint oldBackground = _displayedBackground;
        _displayedBackground = newBackground;
        if (_latestSoundImage == null || oldBackground == newBackground)
        {
            remapped = null;
            return false;
        }

        remapped = RemapBackground(_latestSoundImage, oldBackground, newBackground);
        _latestSoundImage = remapped;
        return true;
    }

    internal static PixelBuffer RemapBackground(PixelBuffer source, uint oldBackground, uint newBackground)
    {
        var recolored = new PixelBuffer(source.Width, source.Height);
        uint[] sourcePixels = source.Pixels;
        uint[] targetPixels = recolored.Pixels;
        for (int i = 0; i < sourcePixels.Length; i++)
        {
            uint pixel = sourcePixels[i];
            targetPixels[i] = pixel == oldBackground ? newBackground : pixel;
        }

        return recolored;
    }

    public void ObserveFrame(AnalysisFrame frame)
    {
        // A re-routed kept frame carries the same pooled buffer reference
        // (the publish pool never repeats a reference on consecutive
        // publishes), so re-observing it must not overwrite a theme-remapped
        // copy with the old-background original.
        if (frame.SoundImageUpdated && frame.SoundImage != null &&
            !ReferenceEquals(frame.SoundImage, _latestSourceImage))
        {
            _latestSourceImage = frame.SoundImage;
            _latestSoundImage = frame.SoundImage;
        }
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        // The sound print is a Core-built pixel image (x = pixel columns of the
        // recent window, not stream time), so the review-cursor contract does
        // not apply; pause already freezes the image for inspection.
        _ = context;
        ObserveFrame(frame);
        if (_latestSoundImage != null)
        {
            _renderer.RenderImage(_latestSoundImage);
        }
    }
}
