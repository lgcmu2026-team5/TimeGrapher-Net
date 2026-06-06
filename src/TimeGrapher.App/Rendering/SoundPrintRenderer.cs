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
        int w = (int)_soundImage.Bounds.Width;
        int h = (int)_soundImage.Bounds.Height;
        if (w > 0 && h > 0)
        {
            var blank = new PixelBuffer(w, h);
            blank.Fill(Argb.White);
            PixelBufferBitmap.UpdateImage(_soundImage, blank);
        }
    }

    public void RenderFrame(AnalysisFrame frame)
    {
        if (frame.SoundImageUpdated && frame.SoundImage != null)
        {
            RenderImage(frame.SoundImage);
        }
    }

    public void RenderImage(PixelBuffer image)
    {
        PixelBufferBitmap.UpdateImage(_soundImage, image);
    }
}
