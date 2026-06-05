namespace TimeGrapher.Core.Shared;

/// <summary>
/// Minimal ARGB32 framebuffer replacing QImage(Format_ARGB32) in the port.
/// Pixels are row-major, 0xAARRGGBB (see <see cref="Argb"/>).
/// The UI layer converts this to an Avalonia WriteableBitmap for display.
/// </summary>
public sealed class PixelBuffer
{
    public int Width { get; }
    public int Height { get; }
    public uint[] Pixels { get; }

    public PixelBuffer(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "PixelBuffer dimensions must be positive");
        Width = width;
        Height = height;
        Pixels = new uint[width * height];
    }

    public void Fill(uint argb) => Array.Fill(Pixels, argb);

    public void SetPixel(int x, int y, uint argb)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        Pixels[y * Width + x] = argb;
    }

    public uint GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
        return Pixels[y * Width + x];
    }

    /// <summary>Deep copy (used to hand a stable snapshot to the UI thread).</summary>
    public PixelBuffer Clone()
    {
        var copy = new PixelBuffer(Width, Height);
        Array.Copy(Pixels, copy.Pixels, Pixels.Length);
        return copy;
    }
}
