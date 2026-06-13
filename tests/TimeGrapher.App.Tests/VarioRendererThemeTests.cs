using Avalonia.Controls;
using Avalonia.Media;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;
using TimeGrapher.App.Rendering;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;
using Xunit;
using PlotColor = ScottPlot.Color;

namespace TimeGrapher.App.Tests;

public sealed class VarioRendererThemeTests
{
    private static PlotThemePalette Palette(uint text) => new(
        SurfaceBg: 0xFF101010,
        ScopeBg: 0xFF202020,
        ScopeGrid: 0xFF303030,
        TextPrimary: text,
        TraceWave: 0xFF404040,
        TraceTick: 0xFF505050,
        TraceTock: 0xFF606060);

    [Fact]
    public void ThemeToggleRecolorsCurrentMarkersAndLabels()
    {
        var ratePlot = new AvaPlot();
        var amplitudePlot = new AvaPlot();
        var summary = new VarioSummaryControls(
            new TextBlock(), new TextBlock(), new TextBlock(), new TextBlock());
        var bandBadges = new VarioBandBadgeControls(new Border(), new Border());
        var table = new VarioTableControls(BuildCells(), BuildCells());
        var renderer = new VarioRenderer(ratePlot, amplitudePlot, summary, bandBadges, table, "Arial");
        renderer.CreateGraphs();
        renderer.RenderFrame(SampleFrame(), new AnalysisTabRenderContext(48000, 2));

        PlotThemePalette dark = Palette(text: 0xFFAABBCC);
        renderer.ApplyTheme(dark);

        AssertCurrentMarkerColor(ratePlot.Plot, dark.TextPrimary);
        AssertCurrentMarkerColor(amplitudePlot.Plot, dark.TextPrimary);

        PlotThemePalette light = Palette(text: 0xFF001122);
        renderer.ApplyTheme(light);

        AssertCurrentMarkerColor(ratePlot.Plot, light.TextPrimary);
        AssertCurrentMarkerColor(amplitudePlot.Plot, light.TextPrimary);
    }

    [Fact]
    public void DarkThemeUsesReadableAverageAndBadColors()
    {
        var ratePlot = new AvaPlot();
        var amplitudePlot = new AvaPlot();
        var summary = new VarioSummaryControls(
            new TextBlock(), new TextBlock(), new TextBlock(), new TextBlock());
        var bandBadges = new VarioBandBadgeControls(new Border(), new Border());
        var table = new VarioTableControls(BuildCells(), BuildCells());
        var renderer = new VarioRenderer(ratePlot, amplitudePlot, summary, bandBadges, table, "Arial");
        PlotThemePalette dark = Palette(text: 0xFFC2C8CE);

        renderer.CreateGraphs();
        renderer.ApplyTheme(dark);
        renderer.RenderFrame(ServiceFrame(), new AnalysisTabRenderContext(48000, 2));

        LinePlot averageLine = ratePlot.Plot.GetPlottables<LinePlot>()
            .Single(line => line.LineWidth == 4);
        Text averageLabel = ratePlot.Plot.GetPlottables<Text>()
            .Single(text => text.LabelText == "avg");
        var badBrush = Assert.IsType<SolidColorBrush>(summary.AmpStatus.Foreground);

        Assert.Equal(PlotColor.FromARGB(0xFFFF6B6B), averageLine.LineColor);
        Assert.Equal(PlotColor.FromARGB(0xFFFF6B6B), averageLabel.LabelFontColor);
        Assert.True(ContrastRatio(badBrush.Color, dark.SurfaceBg) >= 4.5);
    }

    private static TextBlock[] BuildCells()
    {
        return Enumerable.Range(0, VarioRenderer.CellCount)
            .Select(_ => new TextBlock())
            .ToArray();
    }

    private static AnalysisFrame SampleFrame() => new()
    {
        MetricsHistory = new BeatMetricsHistorySnapshot
        {
            Version = 1,
            RateValid = true,
            RateSPerDay = 4.5,
            AmplitudeValid = true,
            AmplitudeDeg = 285.0,
            RateStats = new StatsSummary(true, -2.0, 4.5, 1.1, 1.0, 10),
            AmplitudeStats = new StatsSummary(true, 275.0, 285.0, 280.0, 2.0, 10),
        },
    };

    private static AnalysisFrame ServiceFrame() => new()
    {
        MetricsHistory = new BeatMetricsHistorySnapshot
        {
            Version = 1,
            RateValid = true,
            RateSPerDay = 4.5,
            AmplitudeValid = true,
            AmplitudeDeg = 200.0,
            RateStats = new StatsSummary(true, -2.0, 4.5, 1.1, 1.0, 40),
            AmplitudeStats = new StatsSummary(true, 190.0, 215.0, 200.0, 2.0, 40),
        },
    };

    private static void AssertCurrentMarkerColor(Plot plot, uint expected)
    {
        Text label = plot.GetPlottables<Text>().Single(text => text.LabelText == "current");
        LinePlot line = plot.GetPlottables<LinePlot>()
            .Single(line => line.LinePattern.Equals(LinePattern.Dashed));

        Assert.Equal(PlotColor.FromARGB(expected), label.LabelFontColor);
        Assert.Equal(PlotColor.FromARGB(expected), line.LineColor);
    }

    private static double ContrastRatio(Avalonia.Media.Color foreground, uint background)
    {
        double foregroundLuminance = Luminance(foreground.R, foreground.G, foreground.B);
        double backgroundLuminance = Luminance(
            (byte)((background >> 16) & 0xFF),
            (byte)((background >> 8) & 0xFF),
            (byte)(background & 0xFF));
        double lighter = Math.Max(foregroundLuminance, backgroundLuminance);
        double darker = Math.Min(foregroundLuminance, backgroundLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(byte red, byte green, byte blue)
    {
        return 0.2126 * SrgbToLinear(red)
            + 0.7152 * SrgbToLinear(green)
            + 0.0722 * SrgbToLinear(blue);
    }

    private static double SrgbToLinear(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
