using ScottPlot;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Shared ScottPlot chrome theming (backgrounds, axes, grid) every plot tab
/// applies. One definition instead of the per-renderer copies that had already
/// started to drift, so a palette change recolors every tab the same way.
/// </summary>
internal static class PlotThemeHelper
{
    public static void Apply(Plot plot, PlotThemePalette theme)
    {
        plot.FigureBackground.Color = Color.FromARGB(theme.SurfaceBg);
        plot.DataBackground.Color = Color.FromARGB(theme.ScopeBg);
        plot.Axes.Color(Color.FromARGB(theme.TextPrimary));
        plot.Axes.FrameColor(Color.FromARGB(theme.ScopeGrid));
        plot.Grid.MajorLineColor = Color.FromARGB(theme.ScopeGrid);
        plot.Grid.MinorLineColor = Color.FromARGB(theme.ScopeGrid);
    }
}
