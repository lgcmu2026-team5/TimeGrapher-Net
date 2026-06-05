namespace TimeGrapher.Core.Shared;

/// <summary>
/// ARGB32 color helpers. Layout is 0xAARRGGBB, identical to Qt's QRgb / qRgba().
/// </summary>
public static class Argb
{
    public static uint Rgba(byte r, byte g, byte b, byte a = 255)
        => ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;

    public static byte A(uint argb) => (byte)(argb >> 24);
    public static byte R(uint argb) => (byte)(argb >> 16);
    public static byte G(uint argb) => (byte)(argb >> 8);
    public static byte B(uint argb) => (byte)argb;

    // Qt global color equivalents used by the original code.
    public const uint White = 0xFFFFFFFF;
    public const uint Black = 0xFF000000;
    public const uint Red   = 0xFFFF0000; // Qt::red
    public const uint Green = 0xFF00FF00; // Qt::green
    public const uint Blue  = 0xFF0000FF; // Qt::blue
}
