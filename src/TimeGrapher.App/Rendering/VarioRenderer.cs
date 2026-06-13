using Avalonia.Controls;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>Status chips, elapsed and the Overall conclusion the renderer drives.</summary>
internal sealed record VarioSummaryControls(
    TextBlock RateStatus, TextBlock AmpStatus,
    TextBlock Elapsed,
    TextBlock OverallText);

internal sealed record VarioBandBadgeControls(Control Rate, Control Amplitude);

/// <summary>Numeric table cells (Min, Max, Spread, Average, Sigma, Current) per measure.</summary>
internal sealed record VarioTableControls(
    IReadOnlyList<TextBlock> RateCells,
    IReadOnlyList<TextBlock> AmplitudeCells);

/// <summary>
/// Vario display: per-position stability of rate and amplitude. Each gauge shows
/// the acceptable band (green), the measured min and max (blue lines), the
/// average (red solid line) and the current reading (black dashed line) — opaque lines so they
/// stay legible over the band rather than blending a translucent fill into it;
/// short role labels are placed by <see cref="VarioGaugeLayout"/> in fixed lanes
/// so close values remain readable. A SUMMARY bar carries the verdicts and elapsed time;
/// the table holds the exact numbers. Gauges are non-interactive (no Vario zoom
/// requirement; QAS-5 wants the readings legible without scroll/zoom), so their
/// X-window stays locked to the derived range.
/// </summary>
internal sealed class VarioRenderer
{
    public const int CellMin = 0;
    public const int CellMax = 1;
    public const int CellSpread = 2;
    public const int CellAverage = 3;
    public const int CellSigma = 4;
    public const int CellCurrent = 5;
    public const int CellCount = 6;

    private const uint MinMaxBlue = 0xFF2D7DD2;
    private const uint AvgRed = 0xFFC0392B;
    private const uint DarkThemeAvgRed = 0xFFFF6B6B;
    private const uint AcceptBandFill = 0xFFE9C46A;
    private const uint AcceptBandEdge = 0xFF9A6A00;
    private const byte AcceptBandAlpha = 42;
    private const byte AcceptBandEdgeAlpha = 180;

    // Y layout inside each gauge: bands fill the plot; labels sit in the headroom.
    private const double YMax = 1.42;
    private const int LabelPoolSize = 4;

    private sealed class Gauge
    {
        public required AvaPlot Plot { get; init; }
        public required IReadOnlyList<TextBlock> Cells { get; init; }
        public required TextBlock StatusText { get; init; }
        public required Control AcceptBandBadge { get; init; }
        public required Func<StatsSummary, VarioVerdict> Assess { get; init; }
        public required double AcceptMin { get; init; }
        public required double AcceptMax { get; init; }
        public required string Unit { get; init; }
        public required string NumericFormat { get; init; }
        public required string RangeFormat { get; init; }

        public HorizontalSpan? AcceptBand;
        public LinePlot? MinLine;
        public LinePlot? MaxLine;
        public LinePlot? AvgLine;
        public LinePlot? CurrentLine;
        public readonly List<Text> Labels = new(LabelPoolSize);
    }

    private readonly Gauge _rate;
    private readonly Gauge _amplitude;
    private readonly VarioSummaryControls _summary;
    private readonly VarioBandBadgeControls _bandBadges;
    private readonly string _textFontFamily;
    private PlotThemePalette _theme = PlotThemePalette.Current;
    private ulong _lastVersion;
    private double? _lastCursor;

    public VarioRenderer(
        AvaPlot ratePlot, AvaPlot amplitudePlot,
        VarioSummaryControls summary, VarioBandBadgeControls bandBadges,
        VarioTableControls table, string textFontFamily)
    {
        _summary = summary;
        _bandBadges = bandBadges;
        _textFontFamily = textFontFamily;

        _rate = new Gauge
        {
            Plot = ratePlot,
            Cells = table.RateCells,
            StatusText = summary.RateStatus,
            AcceptBandBadge = bandBadges.Rate,
            Assess = s => VarioVerdict.ForRate(s, VarioGaugePolicy.RateAcceptMinSPerDay, VarioGaugePolicy.RateAcceptMaxSPerDay),
            AcceptMin = VarioGaugePolicy.RateAcceptMinSPerDay,
            AcceptMax = VarioGaugePolicy.RateAcceptMaxSPerDay,
            Unit = " s/d",
            NumericFormat = "+0.0;-0.0;0.0",
            RangeFormat = "0.0",
        };
        _amplitude = new Gauge
        {
            Plot = amplitudePlot,
            Cells = table.AmplitudeCells,
            StatusText = summary.AmpStatus,
            AcceptBandBadge = bandBadges.Amplitude,
            Assess = s => VarioVerdict.ForAmplitude(s, VarioGaugePolicy.AmplitudeAcceptMinDeg, VarioGaugePolicy.AmplitudeAcceptMaxDeg),
            AcceptMin = VarioGaugePolicy.AmplitudeAcceptMinDeg,
            AcceptMax = VarioGaugePolicy.AmplitudeAcceptMaxDeg,
            Unit = "°",
            NumericFormat = "0",
            RangeFormat = "0",
        };

        // A value gauge is not pannable/zoomable: lock it to the derived X-window
        // so a stray scroll can never push the markers off screen.
        ratePlot.UserInputProcessor.Disable();
        amplitudePlot.UserInputProcessor.Disable();
    }

    public void ApplyTheme(PlotThemePalette theme)
    {
        _theme = theme;
        foreach (Gauge gauge in new[] { _rate, _amplitude })
        {
            ApplyPlotTheme(gauge.Plot.Plot);
            ApplyGaugeTheme(gauge);
            ApplyLabelTheme(gauge);
            gauge.Plot.Refresh();
        }
    }

    public void CreateGraphs()
    {
        _lastVersion = 0;
        _lastCursor = null;
        _bandBadges.Rate.IsVisible = false;
        _bandBadges.Amplitude.IsVisible = false;
        foreach (Gauge gauge in new[] { _rate, _amplitude })
        {
            Plot plot = gauge.Plot.Plot;
            plot.Clear();
            gauge.Labels.Clear();
            ApplyPlotTheme(plot);

            // The X axis is the measured-value scale; the Y axis carries no data
            // (every marker is a full-height line), so its ticks and horizontal
            // grid are hidden to avoid implying a second dimension.
            plot.Axes.Left.TickLabelStyle.IsVisible = false;
            plot.Axes.Left.MajorTickStyle.Length = 0;
            plot.Axes.Left.MinorTickStyle.Length = 0;
            plot.Axes.Left.IsVisible = false;
            plot.Axes.Right.IsVisible = false;
            plot.Grid.YAxisStyle.IsVisible = false;

            gauge.AcceptBand = plot.Add.HorizontalSpan(gauge.AcceptMin, gauge.AcceptMax);
            gauge.AcceptBand.IsVisible = false;
            gauge.MinLine = AddLine(plot, 3, LinePattern.Solid);
            gauge.MaxLine = AddLine(plot, 3, LinePattern.Solid);
            gauge.AvgLine = AddLine(plot, 4, LinePattern.Solid);
            gauge.CurrentLine = AddLine(plot, 2, LinePattern.Dashed);
            for (int i = 0; i < LabelPoolSize; i++)
            {
                Text label = plot.Add.Text(string.Empty, 0.0, VarioGaugeLayout.CurrentLabelY);
                label.LabelFontName = _textFontFamily;
                label.LabelFontSize = 12;
                label.LabelBold = true;
                label.IsVisible = false;
                gauge.Labels.Add(label);
            }

            ApplyGaugeTheme(gauge);
            (double lo, double hi) = VarioGaugePolicy.GaugeRange(gauge.AcceptMin, gauge.AcceptMax, default, null);
            plot.Axes.SetLimitsX(lo, hi);
            plot.Axes.SetLimitsY(0.0, YMax);
            gauge.Plot.Refresh();
        }

        SetPlaceholderSummary();
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

        VarioVerdict rateVerdict = UpdateGauge(_rate, history.RateStats, rateCurrent);
        VarioVerdict amplitudeVerdict = UpdateGauge(_amplitude, history.AmplitudeStats, amplitudeCurrent);

        _summary.Elapsed.Text = VarioReadout.FormatElapsed(history.StatsElapsedS);
        UpdateOverall(rateVerdict, amplitudeVerdict);
    }

    private VarioVerdict UpdateGauge(Gauge gauge, StatsSummary stats, double? current)
    {
        Plot plot = gauge.Plot.Plot;
        double? min = stats.Valid ? stats.Min : null;
        double? max = stats.Valid ? stats.Max : null;
        double? avg = stats.Valid ? stats.Mean : null;

        (double lo, double hi) = VarioGaugePolicy.GaugeRange(gauge.AcceptMin, gauge.AcceptMax, stats, current);
        plot.Axes.SetLimitsX(lo, hi);
        plot.Axes.SetLimitsY(0.0, YMax);

        bool showAcceptBand = VarioGaugePolicy.ShouldShowAcceptBand(stats, current);
        gauge.AcceptBandBadge.IsVisible = showAcceptBand;
        if (gauge.AcceptBand != null)
        {
            gauge.AcceptBand.IsVisible = showAcceptBand;
        }

        PositionLine(gauge.MinLine, min);
        PositionLine(gauge.MaxLine, max);
        PositionLine(gauge.AvgLine, avg);
        PositionLine(gauge.CurrentLine, current);
        PlaceLabels(gauge, lo, hi, min, max, avg, current);

        SetCells(gauge, stats, current);

        VarioVerdict verdict = gauge.Assess(stats);
        gauge.StatusText.Text = verdict.Text;
        gauge.StatusText.Foreground = LevelBrush(verdict.Level);

        gauge.Plot.Refresh();
        return verdict;
    }

    private void PlaceLabels(Gauge gauge, double lo, double hi, double? min, double? max, double? avg, double? current)
    {
        foreach (Text label in gauge.Labels)
        {
            label.IsVisible = false;
        }

        IReadOnlyList<GaugeLabel> layout = VarioGaugeLayout.LayOut(lo, hi, min, max, avg, current);
        for (int i = 0; i < layout.Count && i < gauge.Labels.Count; i++)
        {
            GaugeLabel spec = layout[i];
            Text label = gauge.Labels[i];
            label.LabelText = spec.Role;
            label.Location = new Coordinates(spec.X, spec.Y);
            label.LabelFontColor = RoleColor(spec.Role);
            label.Alignment = spec.Anchor switch
            {
                GaugeLabelAnchor.Left => Alignment.LowerLeft,
                GaugeLabelAnchor.Right => Alignment.LowerRight,
                _ => Alignment.LowerCenter,
            };
            label.IsVisible = true;
        }
    }

    private void SetCells(Gauge gauge, StatsSummary stats, double? current)
    {
        string Stat(double? value, string format) => VarioReadout.Format(value, format, gauge.Unit);

        gauge.Cells[CellMin].Text = Stat(stats.Valid ? stats.Min : null, gauge.NumericFormat);
        gauge.Cells[CellMax].Text = Stat(stats.Valid ? stats.Max : null, gauge.NumericFormat);
        gauge.Cells[CellSpread].Text = Stat(stats.Valid ? stats.Max - stats.Min : null, gauge.RangeFormat);
        gauge.Cells[CellAverage].Text = Stat(stats.Valid ? stats.Mean : null, gauge.NumericFormat);
        gauge.Cells[CellSigma].Text = Stat(stats.Valid ? stats.Sigma : null, "0.00");
        gauge.Cells[CellCurrent].Text = Stat(current, gauge.NumericFormat);
    }

    private void UpdateOverall(VarioVerdict rate, VarioVerdict amplitude)
    {
        VarioVerdict overall = VarioVerdict.Overall(rate, amplitude);
        if (overall.Level == VarioVerdictLevel.Pending)
        {
            _summary.OverallText.Text = " ";
            _summary.OverallText.Foreground = LevelBrush(VarioVerdictLevel.Pending);
            return;
        }

        _summary.OverallText.Text = overall.Text;
        _summary.OverallText.Foreground = LevelBrush(overall.Level);
    }

    private void SetPlaceholderSummary()
    {
        foreach (Gauge gauge in new[] { _rate, _amplitude })
        {
            gauge.StatusText.Text = VarioVerdict.Measuring.Text;
            gauge.StatusText.Foreground = LevelBrush(VarioVerdictLevel.Pending);
            foreach (TextBlock cell in gauge.Cells)
            {
                cell.Text = VarioReadout.Missing;
            }
        }

        _summary.Elapsed.Text = "00:00";
        _summary.OverallText.Text = " ";
        _summary.OverallText.Foreground = LevelBrush(VarioVerdictLevel.Pending);
    }

    private ScottPlot.Color RoleColor(string role) => role switch
    {
        "avg" => Color.FromARGB(AverageColor()),
        "current" => Color.FromARGB(_theme.TextPrimary),
        _ => Color.FromARGB(MinMaxBlue),
    };

    private Avalonia.Media.IBrush LevelBrush(VarioVerdictLevel level) =>
        new Avalonia.Media.SolidColorBrush(LevelColor(level));

    private Avalonia.Media.Color LevelColor(VarioVerdictLevel level) => level switch
    {
        VarioVerdictLevel.Good => Avalonia.Media.Color.FromRgb(0x00, 0x72, 0xB2),
        VarioVerdictLevel.Warn => Avalonia.Media.Color.FromRgb(0xB0, 0x6A, 0x00),
        VarioVerdictLevel.Bad when IsDark(_theme.SurfaceBg) => Avalonia.Media.Color.FromRgb(0xFF, 0x6B, 0x6B),
        VarioVerdictLevel.Bad => Avalonia.Media.Color.FromRgb(0xC0, 0x30, 0x30),
        _ => Avalonia.Media.Color.FromRgb(0x80, 0x80, 0x80),
    };

    private uint AverageColor() => IsDark(_theme.ScopeBg) ? DarkThemeAvgRed : AvgRed;

    private static bool IsDark(uint argb)
    {
        double r = SrgbToLinear((argb >> 16) & 0xFF);
        double g = SrgbToLinear((argb >> 8) & 0xFF);
        double b = SrgbToLinear(argb & 0xFF);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b) < 0.18;
    }

    private static double SrgbToLinear(uint channel)
    {
        double value = channel / 255.0;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static void PositionLine(LinePlot? line, double? value)
    {
        if (line == null)
        {
            return;
        }

        if (value is double x)
        {
            line.Line = new CoordinateLine(x, 0.0, x, 1.0);
            line.IsVisible = true;
        }
        else
        {
            line.IsVisible = false;
        }
    }

    private static LinePlot AddLine(Plot plot, float width, LinePattern pattern)
    {
        LinePlot line = plot.Add.Line(0.0, 0.0, 0.0, 1.0);
        line.MarkerStyle.IsVisible = false;
        line.LineWidth = width;
        line.LinePattern = pattern;
        line.IsVisible = false;
        return line;
    }

    private void ApplyGaugeTheme(Gauge gauge)
    {
        if (gauge.AcceptBand != null)
        {
            gauge.AcceptBand.FillStyle.Color = Color.FromARGB(AcceptBandFill).WithAlpha(AcceptBandAlpha);
            gauge.AcceptBand.LineStyle.Color = Color.FromARGB(AcceptBandEdge).WithAlpha(AcceptBandEdgeAlpha);
            gauge.AcceptBand.LineStyle.Width = 2;
        }

        if (gauge.MinLine != null)
        {
            gauge.MinLine.LineColor = Color.FromARGB(MinMaxBlue);
        }

        if (gauge.MaxLine != null)
        {
            gauge.MaxLine.LineColor = Color.FromARGB(MinMaxBlue);
        }

        if (gauge.AvgLine != null)
        {
            gauge.AvgLine.LineColor = Color.FromARGB(AverageColor());
        }

        if (gauge.CurrentLine != null)
        {
            gauge.CurrentLine.LineColor = Color.FromARGB(_theme.TextPrimary);
        }
    }

    private void ApplyLabelTheme(Gauge gauge)
    {
        foreach (Text label in gauge.Labels)
        {
            label.LabelFontColor = RoleColor(label.LabelText);
        }
    }

    private void ApplyPlotTheme(Plot plot)
    {
        PlotThemeHelper.Apply(plot, _theme);
    }
}
