using Avalonia.Controls;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

internal sealed class SoundPrintRenderer
{
    private readonly Image _soundImage;

    public SoundPrintRenderer(Image soundImage)
    {
        _soundImage = soundImage;
    }

    public void Reset()
    {
        // Always drop the previous run's image — with the tab hidden the bounds are
        // zero and the blank repaint below is skipped, which would leave stale data.
        _soundImage.Source = null;

        int w = (int)_soundImage.Bounds.Width;
        int h = (int)_soundImage.Bounds.Height;
        if (w > 0 && h > 0)
        {
            var blank = new PixelBuffer(w, h);
            blank.Fill(PlotThemePalette.Current.ScopeBg);
            PixelBufferBitmap.UpdateImage(_soundImage, blank);
        }
    }

    public void RenderImage(PixelBuffer image)
    {
        PixelBufferBitmap.UpdateImage(_soundImage, image);
    }
}
