using Avalonia;
using Avalonia.Styling;
using AvaloniaColor = Avalonia.Media.Color;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Theme colors for the ScottPlot scopes. Values are NOT defined here — they are
/// read from the App.axaml ThemeDictionaries (the single source of truth) via
/// <see cref="FromResources"/>, so editing App.axaml recolors the graphs too.
/// </summary>
internal readonly record struct PlotThemePalette(
    uint SurfaceBg,
    uint ScopeBg,
    uint ScopeGrid,
    uint TextPrimary,
    uint TraceWave,
    uint TraceTick,
    uint TraceTock)
{
    /// <summary>Palette for the currently requested application theme variant.</summary>
    public static PlotThemePalette Current =>
        FromResources(Application.Current?.RequestedThemeVariant ?? ThemeVariant.Light);

    /// <summary>Builds the palette by reading the App.axaml color resources for <paramref name="theme"/>.</summary>
    public static PlotThemePalette FromResources(ThemeVariant theme) => new(
        SurfaceBg: Lookup("SurfaceColor", theme),
        ScopeBg: Lookup("ScopeBgColor", theme),
        ScopeGrid: Lookup("ScopeGridColor", theme),
        TextPrimary: Lookup("TextPrimaryColor", theme),
        TraceWave: Lookup("TraceWaveColor", theme),
        TraceTick: Lookup("TraceTickColor", theme),
        TraceTock: Lookup("TraceTockColor", theme));

    private static uint Lookup(string key, ThemeVariant theme)
    {
        if (Application.Current is { } app &&
            app.TryGetResource(key, theme, out object? value) &&
            value is AvaloniaColor color)
        {
            return ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        }

        // Defensive fallback if a resource is missing or looked up before app init.
        return 0xFF000000;
    }
}
