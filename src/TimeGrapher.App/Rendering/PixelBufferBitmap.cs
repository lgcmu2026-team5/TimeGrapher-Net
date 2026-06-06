using System.Runtime.CompilerServices;
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
internal static class PixelBufferBitmap
{
    private sealed class ImageCache
    {
        public WriteableBitmap? Bitmap;
        public int Width;
        public int Height;
        public int[] Scratch = Array.Empty<int>();
    }

    // Per-image cache, reused while dimensions stay the same. This keeps the allocation
    // benefit of the Qt-era persistent image without sharing mutable bitmap state between
    // windows or image controls.
    private static readonly ConditionalWeakTable<Avalonia.Controls.Image, ImageCache> Caches = new();

    /// <summary>
    /// Copy <paramref name="buffer"/> into a Bgra8888 WriteableBitmap, set it as the
    /// <paramref name="target"/> image source and invalidate the control.
    /// UI thread only (same contract as the original DrawImage()).
    /// </summary>
    public static void UpdateImage(Avalonia.Controls.Image target, PixelBuffer buffer)
    {
        ImageCache cache = Caches.GetValue(target, _ => new ImageCache());
        if (cache.Bitmap is null || cache.Width != buffer.Width || cache.Height != buffer.Height)
        {
            // The source pixels are opaque (the renderer only writes solid colors), so the
            // alpha format is irrelevant; Opaque avoids any premultiplication round-trip.
            cache.Bitmap = new WriteableBitmap(
                new PixelSize(buffer.Width, buffer.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);
            cache.Width = buffer.Width;
            cache.Height = buffer.Height;
        }

        int pixelCount = buffer.Pixels.Length;
        if (cache.Scratch.Length != pixelCount)
        {
            cache.Scratch = new int[pixelCount];
        }

        // Bit-for-bit reinterpret uint[] -> int[] (no value conversion).
        Buffer.BlockCopy(buffer.Pixels, 0, cache.Scratch, 0, pixelCount * sizeof(uint));

        using (ILockedFramebuffer fb = cache.Bitmap.Lock())
        {
            // PixelBuffer is tightly packed (RowBytes == Width * 4) and so is the framebuffer
            // when created with matching dimensions; copy the whole row-major buffer at once.
            Marshal.Copy(cache.Scratch, 0, fb.Address, pixelCount);
        }

        target.Source = cache.Bitmap;
        target.InvalidateVisual();
    }
}
