using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Helper that converts a <see cref="PixelBuffer"/> (ARGB32) into an Avalonia
/// <see cref="WriteableBitmap"/> and assigns it to an <see cref="Avalonia.Controls.Image"/>.
///
/// This replaces SoundImageWidget.SetImage()/DrawImage() from the Qt original, which copied
/// a QImage(Format_ARGB32) into the widget and called update().
///
/// Memory layout note: PixelBuffer stores 0xAARRGGBB uint values. On a little-endian machine
/// the in-memory byte order of each uint is B, G, R, A, which is exactly PixelFormat.Bgra8888.
/// So the uint[] can be blitted directly into a Bgra8888 framebuffer.
/// </summary>
public static class PixelBufferBitmap
{
    // Cached bitmap, reused while the dimensions stay the same (avoids per-frame allocation,
    // mirroring SoundImageWidget which kept a single QImage instance alive).
    private static WriteableBitmap? _bitmap;
    private static int _width;
    private static int _height;

    // Scratch int[] used for the blit. Marshal.Copy has no uint[] overload, so the uint[]
    // is bit-copied into an int[] (identical 32-bit layout) and then marshalled to the
    // framebuffer. Reused while dimensions stay the same.
    private static int[] _scratch = Array.Empty<int>();

    /// <summary>
    /// Copy <paramref name="buffer"/> into a Bgra8888 WriteableBitmap, set it as the
    /// <paramref name="target"/> image source and invalidate the control.
    /// UI thread only (same contract as the original DrawImage()).
    /// </summary>
    public static void UpdateImage(Avalonia.Controls.Image target, PixelBuffer buffer)
    {
        if (_bitmap is null || _width != buffer.Width || _height != buffer.Height)
        {
            // The source pixels are opaque (the renderer only writes solid colors), so the
            // alpha format is irrelevant; Opaque avoids any premultiplication round-trip.
            _bitmap = new WriteableBitmap(
                new PixelSize(buffer.Width, buffer.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);
            _width = buffer.Width;
            _height = buffer.Height;
        }

        int pixelCount = buffer.Pixels.Length;
        if (_scratch.Length != pixelCount)
        {
            _scratch = new int[pixelCount];
        }

        // Bit-for-bit reinterpret uint[] -> int[] (no value conversion).
        Buffer.BlockCopy(buffer.Pixels, 0, _scratch, 0, pixelCount * sizeof(uint));

        using (ILockedFramebuffer fb = _bitmap.Lock())
        {
            // PixelBuffer is tightly packed (RowBytes == Width * 4) and so is the framebuffer
            // when created with matching dimensions; copy the whole row-major buffer at once.
            Marshal.Copy(_scratch, 0, fb.Address, pixelCount);
        }

        target.Source = _bitmap;
        target.InvalidateVisual();
    }
}
