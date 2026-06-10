using Avalonia.Controls;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Vario display: long-term stability of rate and amplitude as two horizontal
/// value gauges plus numeric readouts. Per the plan: green region = acceptable
/// range, blue markers = measured min/max, red marker = average; numeric grid
/// shows min/max/avg/sigma/current/elapsed, all from the running statistics on
/// the cumulative history snapshot (exact regardless of series decimation).
/// </summary>
internal sealed class VarioRenderer
{
    // The palette has no blue; a fixed mid-blue stays readable on both themes.
    private const uint MinMaxBlue = 0xFF2D7DD2;
    private const byte AcceptBandAlpha = 40;

    private sealed class Gauge
    {
        public required AvaPlot Plot { get; init; }
        public required TextBlock Readout { get; init; }
        public required double AcceptMin { get; init; }
        public required double AcceptMax { get; init; }
        public required string Unit { get; init; }
        public required string NumericFormat { get; init; }
        public HorizontalSpan? AcceptBand;
        public LinePlot? MinLine;
        public LinePlot? MaxLine;
        public LinePlot? MeanLine;
        public LinePlot? CurrentLine;
    }

    private readonly Gauge _rate;
    private readonly Gauge _amplitude;
    private PlotThemePalette _theme = PlotThemePalette.Current;
    private ulong _lastVersion;
    private double? _lastCursor;

    public VarioRenderer(AvaPlot ratePlot, TextBlock rateReadout, AvaPlot amplitudePlot, TextBlock amplitudeReadout)
    {
        _rate = new Gauge
        {
            Plot = ratePlot,
            Readout = rateReadout,
            AcceptMin = VarioGaugePolicy.RateAcceptMinSPerDay,
            AcceptMax = VarioGaugePolicy.RateAcceptMaxSPerDay,
            Unit = " s/d",
            NumericFormat = "+0.0;-0.0;0.0",
        };
        _amplitude = new Gauge
        {
            Plot = amplitudePlot,
            Readout = amplitudeReadout,
            AcceptMin = VarioGaugePolicy.AmplitudeAcceptMinDeg,
            AcceptMax = VarioGaugePolicy.AmplitudeAcceptMaxDeg,
            Unit = "°",
            NumericFormat = "0",
        };
    }

    public void ApplyTheme(PlotThemePalette theme)
    {
        _theme = theme;
        foreach (Gauge gauge in new[] { _rate, _amplitude })
        {
            ApplyPlotTheme(gauge.Plot.Plot);
            ApplyGaugeTheme(gauge);
            gauge.Plot.Refresh();
        }
    }

    public void CreateGraphs()
    {
        _lastVersion = 0;
        _lastCursor = null;
        foreach (Gauge gauge in new[] { _rate, _amplitude })
        {
            Plot plot = gauge.Plot.Plot;
            plot.Clear();
            ApplyPlotTheme(plot);
            plot.Axes.Left.TickLabelStyle.IsVisible = false;
            plot.Axes.SetLimitsY(0.0, 1.0);

            gauge.AcceptBand = plot.Add.HorizontalSpan(gauge.AcceptMin, gauge.AcceptMax);
            gauge.MinLine = AddMarker(plot, 3);
            gauge.MaxLine = AddMarker(plot, 3);
            gauge.MeanLine = AddMarker(plot, 3);
            gauge.CurrentLine = AddMarker(plot, 1);

            ApplyGaugeTheme(gauge);
            (double lo, double hi) = VarioGaugePolicy.GaugeRange(gauge.AcceptMin, gauge.AcceptMax, default, null);
            plot.Axes.SetLimitsX(lo, hi);
            gauge.Readout.Text = PlaceholderReadout();
            gauge.Plot.Refresh();
        }
    }

    public void Reset()
    {
        CreateGraphs();
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        BeatMetricsHistorySnapshot? history = frame.MetricsHistory;
        if (history == null)
        {
            return;
        }

        if (history.Version == _lastVersion && context.ReviewCursorTimeS == _lastCursor)
        {
            return;
        }

        _lastVersion = history.Version;
        _lastCursor = context.ReviewCursorTimeS;

        double? rateCurrent = context.ReviewCursorTimeS is double rt
            ? VarioReadout.ValueAt(history.Rate, rt)
            : history.RateValid ? history.RateSPerDay : null;
        double? amplitudeCurrent = context.ReviewCursorTimeS is double at
            ? VarioReadout.ValueAt(history.Amplitude, at)
            : history.AmplitudeValid ? history.AmplitudeDeg : null;

        UpdateGauge(_rate, history.RateStats, rateCurrent, history.LatestTimeS);
        UpdateGauge(_amplitude, history.AmplitudeStats, amplitudeCurrent, history.LatestTimeS);
    }

    private void UpdateGauge(Gauge gauge, StatsSummary stats, double? current, double elapsedS)
    {
        Plot plot = gauge.Plot.Plot;
        (double lo, double hi) = VarioGaugePolicy.GaugeRange(gauge.AcceptMin, gauge.AcceptMax, stats, current);
        plot.Axes.SetLimitsX(lo, hi);
        plot.Axes.SetLimitsY(0.0, 1.0);

        PositionMarker(gauge.MinLine, stats.Valid ? stats.Min : null);
        PositionMarker(gauge.MaxLine, stats.Valid ? stats.Max : null);
        PositionMarker(gauge.MeanLine, stats.Valid ? stats.Mean : null);
        PositionMarker(gauge.CurrentLine, current);

        gauge.Readout.Text = string.Join("   ",
            "MIN " + VarioReadout.Format(stats.Valid ? stats.Min : null, gauge.NumericFormat, gauge.Unit),
            "MAX " + VarioReadout.Format(stats.Valid ? stats.Max : null, gauge.NumericFormat, gauge.Unit),
            "AVG " + VarioReadout.Format(stats.Valid ? stats.Mean : null, gauge.NumericFormat, gauge.Unit),
            "σ " + VarioReadout.Format(stats.Valid ? stats.Sigma : null, "0.00", gauge.Unit),
            "CUR " + VarioReadout.Format(current, gauge.NumericFormat, gauge.Unit),
            "ELAPSED " + VarioReadout.FormatElapsed(elapsedS));

        gauge.Plot.Refresh();
    }

    private static string PlaceholderReadout() =>
        "MIN " + VarioReadout.Missing + "   MAX " + VarioReadout.Missing + "   AVG " + VarioReadout.Missing +
        "   σ " + VarioReadout.Missing + "   CUR " + VarioReadout.Missing + "   ELAPSED 00:00";

    private static void PositionMarker(LinePlot? marker, double? value)
    {
        if (marker == null)
        {
            return;
        }

        if (value is double x)
        {
            marker.Line = new CoordinateLine(x, 0.0, x, 1.0);
            marker.IsVisible = true;
        }
        else
        {
            marker.IsVisible = false;
        }
    }

    private static LinePlot AddMarker(Plot plot, float width)
    {
        LinePlot line = plot.Add.Line(0.0, 0.0, 0.0, 1.0);
        line.MarkerStyle.IsVisible = false;
        line.LineWidth = width;
        line.IsVisible = false;
        return line;
    }

    private void ApplyGaugeTheme(Gauge gauge)
    {
        if (gauge.AcceptBand != null)
        {
            gauge.AcceptBand.FillStyle.Color = Color.FromARGB(_theme.TraceTick).WithAlpha(AcceptBandAlpha);
            gauge.AcceptBand.LineStyle.Color = Color.FromARGB(_theme.TraceTick).WithAlpha((byte)(AcceptBandAlpha * 2));
        }

        if (gauge.MinLine != null)
        {
            gauge.MinLine.LineColor = Color.FromARGB(MinMaxBlue);
        }

        if (gauge.MaxLine != null)
        {
            gauge.MaxLine.LineColor = Color.FromARGB(MinMaxBlue);
        }

        if (gauge.MeanLine != null)
        {
            gauge.MeanLine.LineColor = Color.FromARGB(_theme.TraceTock);
        }

        if (gauge.CurrentLine != null)
        {
            gauge.CurrentLine.LineColor = Color.FromARGB(_theme.TextPrimary);
        }
    }

    private void ApplyPlotTheme(Plot plot)
    {
        plot.FigureBackground.Color = Color.FromARGB(_theme.SurfaceBg);
        plot.DataBackground.Color = Color.FromARGB(_theme.ScopeBg);
        plot.Axes.Color(Color.FromARGB(_theme.TextPrimary));
        plot.Axes.FrameColor(Color.FromARGB(_theme.ScopeGrid));
        plot.Grid.MajorLineColor = Color.FromARGB(_theme.ScopeGrid);
        plot.Grid.MinorLineColor = Color.FromARGB(_theme.ScopeGrid);
    }
}
