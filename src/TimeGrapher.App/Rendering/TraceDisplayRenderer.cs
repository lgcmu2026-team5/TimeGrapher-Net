using Avalonia.Controls;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Trace Display: continuous rate-deviation and amplitude traces over elapsed
/// time, rendered from the cumulative BeatMetricsHistorySnapshot the frame
/// already carries. The amplitude plot shows the 270-300 degree acceptance band;
/// an alert banner reports late-running and out-of-range amplitude; the footer
/// shows since-start and rolling (60 s) averages. Re-renders only when the
/// snapshot version changes, so coalesced or repeated frames cost nothing.
/// </summary>
internal sealed class TraceDisplayRenderer
{
    private const double RollingWindowS = 60.0;
    private const byte BandFillAlpha = 36;

    private readonly AvaPlot _ratePlot;
    private readonly AvaPlot _amplitudePlot;
    private readonly Border _alertBanner;
    private readonly TextBlock _alertText;
    private readonly TextBlock _summaryText;

    private readonly List<double> _rateX = new();
    private readonly List<double> _rateY = new();
    private readonly List<double> _amplitudeX = new();
    private readonly List<double> _amplitudeY = new();

    private Scatter? _rateScatter;
    private Scatter? _amplitudeScatter;
    private VerticalSpan? _amplitudeBand;
    private ReviewCursorLayer? _rateCursor;
    private ReviewCursorLayer? _amplitudeCursor;

    private PlotThemePalette _theme = PlotThemePalette.Current;
    private ulong _lastVersion;
    private bool _followLive = true;

    public TraceDisplayRenderer(
        AvaPlot ratePlot,
        AvaPlot amplitudePlot,
        Border alertBanner,
        TextBlock alertText,
        TextBlock summaryText)
    {
        _ratePlot = ratePlot;
        _amplitudePlot = amplitudePlot;
        _alertBanner = alertBanner;
        _alertText = alertText;
        _summaryText = summaryText;

        _ratePlot.PointerWheelChanged += (_, _) => _followLive = false;
        _ratePlot.PointerPressed += (_, _) => _followLive = false;
        _amplitudePlot.PointerWheelChanged += (_, _) => _followLive = false;
        _amplitudePlot.PointerPressed += (_, _) => _followLive = false;
    }

    public void ApplyTheme(PlotThemePalette theme)
    {
        _theme = theme;
        ApplyPlotTheme(_ratePlot.Plot);
        ApplyPlotTheme(_amplitudePlot.Plot);
        ApplySeriesTheme();
        _ratePlot.Refresh();
        _amplitudePlot.Refresh();
    }

    public void CreateGraphs()
    {
        _lastVersion = 0;
        _followLive = true;
        _alertBanner.IsVisible = false;
        _summaryText.Text = "";

        Plot rate = _ratePlot.Plot;
        rate.Clear();
        _rateX.Clear();
        _rateY.Clear();
        ApplyPlotTheme(rate);
        rate.YLabel("Rate (s/d)");
        rate.XLabel("Elapsed (s)");
        _rateScatter = rate.Add.Scatter(_rateX, _rateY);
        _rateScatter.LineWidth = 2;
        _rateScatter.MarkerStyle.IsVisible = false;
        _rateCursor = AddCursor(rate);

        Plot amplitude = _amplitudePlot.Plot;
        amplitude.Clear();
        _amplitudeX.Clear();
        _amplitudeY.Clear();
        ApplyPlotTheme(amplitude);
        amplitude.YLabel("Amplitude (°)");
        amplitude.XLabel("Elapsed (s)");
        _amplitudeBand = amplitude.Add.VerticalSpan(
            TraceAlertEvaluator.AmplitudeMinDeg, TraceAlertEvaluator.AmplitudeMaxDeg);
        _amplitudeScatter = amplitude.Add.Scatter(_amplitudeX, _amplitudeY);
        _amplitudeScatter.LineWidth = 2;
        _amplitudeScatter.MarkerStyle.IsVisible = false;
        _amplitudeCursor = AddCursor(amplitude);

        ApplySeriesTheme();
        _ratePlot.Refresh();
        _amplitudePlot.Refresh();
    }

    public void Reset()
    {
        CreateGraphs();
    }

    public void ResetView()
    {
        _followLive = true;
        _ratePlot.Plot.Axes.AutoScale();
        _amplitudePlot.Plot.Axes.AutoScale();
        _ratePlot.Refresh();
        _amplitudePlot.Refresh();
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        BeatMetricsHistorySnapshot? history = frame.MetricsHistory;
        bool cursorMoved = UpdateReviewCursor(context.ReviewCursorTimeS);

        if (history == null || history.Version == _lastVersion)
        {
            if (cursorMoved)
            {
                _ratePlot.Refresh();
                _amplitudePlot.Refresh();
            }

            return;
        }

        _lastVersion = history.Version;

        // History series are already bounded by DecimatingSeries; budget 0 = copy as-is.
        SeriesDataReducer.ReplaceSeriesData(_rateX, _rateY, history.Rate.X, history.Rate.Y, targetPointBudget: 0);
        SeriesDataReducer.ReplaceSeriesData(_amplitudeX, _amplitudeY, history.Amplitude.X, history.Amplitude.Y, targetPointBudget: 0);

        if (_followLive)
        {
            _ratePlot.Plot.Axes.AutoScale();
            _amplitudePlot.Plot.Axes.AutoScale();
        }

        UpdateAlerts(history);
        UpdateSummaries(history);

        _ratePlot.Refresh();
        _amplitudePlot.Refresh();
    }

    private void UpdateAlerts(BeatMetricsHistorySnapshot history)
    {
        TraceAlertState alerts = TraceAlertEvaluator.Evaluate(history);
        _alertBanner.IsVisible = alerts.Message != null;
        if (alerts.Message != null)
        {
            _alertText.Text = "⚠ " + alerts.Message;
        }
    }

    private void UpdateSummaries(BeatMetricsHistorySnapshot history)
    {
        string Format(string label, double? sinceStart, double? rolling, string unit) =>
            sinceStart is double avg && rolling is double roll
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0} avg {1:+0.0;-0.0;0.0}{3} / last {4:F0}s {2:+0.0;-0.0;0.0}{3}",
                    label, avg, roll, unit, RollingWindowS)
                : label + " avg —";

        _summaryText.Text =
            Format("RATE", MetricsSeriesMath.Average(history.Rate),
                MetricsSeriesMath.RollingAverage(history.Rate, RollingWindowS), " s/d")
            + "   |   "
            + Format("AMP", MetricsSeriesMath.Average(history.Amplitude),
                MetricsSeriesMath.RollingAverage(history.Amplitude, RollingWindowS), "°");
    }

    /// <summary>Review-cursor contract: a vertical marker at the scrub time on both plots.</summary>
    private bool UpdateReviewCursor(double? reviewCursorTimeS)
    {
        bool changed = _rateCursor?.Update(reviewCursorTimeS) ?? false;
        changed |= _amplitudeCursor?.Update(reviewCursorTimeS) ?? false;
        return changed;
    }

    private ReviewCursorLayer AddCursor(Plot plot)
    {
        var cursor = new ReviewCursorLayer(plot);
        cursor.ApplyTheme(_theme);
        return cursor;
    }

    private void ApplySeriesTheme()
    {
        if (_rateScatter != null)
        {
            _rateScatter.LineColor = Color.FromARGB(_theme.TraceTick);
        }

        if (_amplitudeScatter != null)
        {
            _amplitudeScatter.LineColor = Color.FromARGB(_theme.TraceWave);
        }

        if (_amplitudeBand != null)
        {
            _amplitudeBand.FillStyle.Color = Color.FromARGB(_theme.TraceTick).WithAlpha(BandFillAlpha);
            _amplitudeBand.LineStyle.Color = Color.FromARGB(_theme.TraceTick).WithAlpha((byte)(BandFillAlpha * 2));
        }

        _rateCursor?.ApplyTheme(_theme);
        _amplitudeCursor?.ApplyTheme(_theme);
    }

    private void ApplyPlotTheme(Plot plot)
    {
        PlotThemeHelper.Apply(plot, _theme);
    }
}
