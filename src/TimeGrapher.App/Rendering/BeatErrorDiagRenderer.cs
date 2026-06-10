using Avalonia.Controls;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Beat Error Display and Diagnostic Trace: a numeric panel (rate, amplitude,
/// signed beat error, BPH plus the derived DiffTicTac / DiffPeriod / AvgPeriod)
/// above the tic/toc rate-error traces, with a BeatErrorDiagnostics banner for
/// the separation alert and major-fault slope conditions. The traces refill from
/// the per-frame AnalysisGraphSeries.RateTic/RateToc snapshots every frame
/// already carries (the RateScopeRenderer pattern); the numeric panel and banner
/// re-evaluate only when the cumulative history snapshot version changes.
/// </summary>
internal sealed class BeatErrorDiagRenderer
{
    private readonly AvaPlot _tracePlot;
    private readonly Border _alertBanner;
    private readonly TextBlock _alertText;
    private readonly TextBlock[] _valueTexts;

    private readonly GraphSeriesDefinition[] _rateSeries;
    private readonly List<double>[] _rateX;
    private readonly List<double>[] _rateY;
    private readonly List<Scatter> _ratePlots = new();

    private PlotThemePalette _theme = PlotThemePalette.Current;
    private ulong _lastVersion;
    private double _rateErrorYScale;
    private int _rateDataPoints;

    public BeatErrorDiagRenderer(AvaPlot tracePlot, Border alertBanner, TextBlock alertText, TextBlock[] valueTexts)
    {
        _tracePlot = tracePlot;
        _alertBanner = alertBanner;
        _alertText = alertText;
        _valueTexts = valueTexts;

        _rateSeries = InfoTabCatalog.Get(InfoTabCatalog.BeatErrorDiagTabId).GraphSeries.ToArray();
        _rateX = new List<double>[_rateSeries.Length];
        _rateY = new List<double>[_rateSeries.Length];
        for (int i = 0; i < _rateSeries.Length; i++)
        {
            _rateX[i] = new List<double>();
            _rateY[i] = new List<double>();
        }
    }

    public void ApplyTheme(PlotThemePalette theme)
    {
        _theme = theme;
        ApplyPlotTheme(_tracePlot.Plot);
        ApplySeriesTheme();
        _tracePlot.Refresh();
    }

    public void CreateGraphs(double rateErrorYScale, int rateDataPoints)
    {
        _rateErrorYScale = rateErrorYScale;
        _rateDataPoints = rateDataPoints;
        _lastVersion = 0;
        _alertBanner.IsVisible = false;
        foreach (TextBlock value in _valueTexts)
        {
            value.Text = VarioReadout.Missing;
        }

        Plot trace = _tracePlot.Plot;
        trace.Clear();
        ApplyPlotTheme(trace);
        trace.YLabel("Rate Error (ms)");
        trace.XLabel("Beat");
        trace.Axes.SetLimitsY(-rateErrorYScale, rateErrorYScale);
        trace.Axes.SetLimitsX(0, rateDataPoints);
        trace.Axes.Bottom.TickLabelStyle.IsVisible = false;
        for (int i = 0; i < _rateSeries.Length; i++)
        {
            _rateX[i].Clear();
            _rateY[i].Clear();
        }

        AddTracePlottables();
        trace.ShowLegend();
        _tracePlot.Refresh();
    }

    /// <summary>Restores the trace plot to its configured limits.</summary>
    public void ResetView()
    {
        _tracePlot.Plot.Axes.SetLimitsY(-_rateErrorYScale, _rateErrorYScale);
        _tracePlot.Plot.Axes.SetLimitsX(0, _rateDataPoints);
        _tracePlot.Refresh();
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        // Review cursor deliberately not rendered here: this trace's x-domain is
        // the WatchMetrics ring-buffer beat index (0..MaxRateDataPoints), not
        // stream time, so context.ReviewCursorTimeS has no meaningful x mapping
        // on this plot.
        _ = context;

        bool rateUpdated = ReplaceRateSeries(frame);

        BeatMetricsHistorySnapshot? history = frame.MetricsHistory;
        if (history != null && history.Version != _lastVersion)
        {
            _lastVersion = history.Version;
            UpdateReadout(history);
            UpdateDiagnosis(history);
        }

        if (rateUpdated)
        {
            _tracePlot.Refresh();
        }
    }

    private void UpdateReadout(BeatMetricsHistorySnapshot history)
    {
        string[] values = BeatErrorReadout.Values(history);
        for (int i = 0; i < _valueTexts.Length && i < values.Length; i++)
        {
            _valueTexts[i].Text = values[i];
        }
    }

    private void UpdateDiagnosis(BeatMetricsHistorySnapshot history)
    {
        BeatErrorDiagnosis diagnosis = BeatErrorDiagnostics.Evaluate(history);
        _alertBanner.IsVisible = diagnosis.Message != null;
        if (diagnosis.Message != null)
        {
            // The major-fault message is already the stronger wording
            // ("MAJOR FAULT: ..."); the banner styling is shared.
            _alertText.Text = "⚠ " + diagnosis.Message;
        }
    }

    private bool ReplaceRateSeries(AnalysisFrame frame)
    {
        bool updated = false;
        for (int i = 0; i < _rateSeries.Length; i++)
        {
            GraphSeriesFrame? series = FindSeries(frame.RateSeries, _rateSeries[i].Id);
            if (series == null)
            {
                continue;
            }

            updated |= SeriesDataReducer.TryReplaceSeriesData(series, _rateX[i], _rateY[i], _rateSeries[i].TargetPointBudget);
        }

        return updated;
    }

    private void AddTracePlottables()
    {
        Plot trace = _tracePlot.Plot;
        _ratePlots.Clear();
        for (int i = 0; i < _rateSeries.Length; i++)
        {
            GraphSeriesDefinition spec = _rateSeries[i];
            Scatter sc = trace.Add.Scatter(_rateX[i], _rateY[i]);
            sc.LineWidth = 0;
            sc.MarkerShape = MarkerShape.FilledCircle;
            sc.MarkerSize = 3;
            sc.MarkerColor = Color.FromARGB(ThemeColor(spec));
            sc.LegendText = spec.Name;
            _ratePlots.Add(sc);
        }
    }

    private void ApplySeriesTheme()
    {
        for (int i = 0; i < _ratePlots.Count && i < _rateSeries.Length; i++)
        {
            _ratePlots[i].MarkerColor = Color.FromARGB(ThemeColor(_rateSeries[i]));
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
        plot.Legend.BackgroundColor = Color.FromARGB(_theme.ScopeBg);
        plot.Legend.FontColor = Color.FromARGB(_theme.TextPrimary);
        plot.Legend.OutlineColor = Color.FromARGB(_theme.ScopeGrid);
    }

    // Same tick/tock color mapping the Rate/Scope traces use.
    private uint ThemeColor(GraphSeriesDefinition spec) => spec.Id switch
    {
        AnalysisGraphSeries.RateTic => _theme.TraceTick,
        AnalysisGraphSeries.RateToc => _theme.TraceTock,
        _ => _theme.TraceWave,
    };

    private static GraphSeriesFrame? FindSeries(IReadOnlyList<GraphSeriesFrame> seriesList, string id)
    {
        foreach (GraphSeriesFrame series in seriesList)
        {
            if (series.Id == id)
            {
                return series;
            }
        }

        return null;
    }
}
