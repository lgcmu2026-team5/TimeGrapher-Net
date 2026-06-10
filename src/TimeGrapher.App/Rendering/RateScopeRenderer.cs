using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

internal sealed class RateScopeRenderer
{
    private readonly AvaPlot _scopePlot;
    private readonly AvaPlot _ratePlot;
    private readonly string _textFontFamily;

    private readonly GraphSeriesDefinition[] _scopeSeries;
    private readonly GraphSeriesDefinition[] _rateSeries;

    private readonly List<double>[] _scopeX;
    private readonly List<double>[] _scopeY;
    private readonly List<double>[] _rateX;
    private readonly List<double>[] _rateY;

    // Scope event markers are pooled: a render tick repositions the existing
    // LinePlot/Text plottables in place and hides the surplus instead of
    // removing and re-allocating the whole 2 s marker window (~100+ plottables)
    // at up to 30 Hz on the UI thread. Hidden plottables are excluded from
    // ScottPlot's autoscale, so leftovers cannot distort the Y fit.
    private readonly List<LinePlot> _scopeLinePool = new();
    private readonly List<Text> _scopeTextPool = new();
    private int _scopeLinesUsed;
    private int _scopeTextsUsed;
    private readonly List<Scatter> _scopePlots = new();
    private readonly List<Scatter> _ratePlots = new();
    private LinePlot? _scopeReviewCursor;
    private PlotThemePalette _theme = PlotThemePalette.Current;

    // The scope auto-follows incoming audio (scrolls its X window each frame). Once the
    // user pans/zooms it, we stop following so the view stays put; ResetView() re-enables it.
    private bool _scopeFollowLive = true;
    private double _rateErrorYScale;
    private int _rateDataPoints;

    public RateScopeRenderer(AvaPlot scopePlot, AvaPlot ratePlot, string textFontFamily)
    {
        _scopePlot = scopePlot;
        _ratePlot = ratePlot;
        _textFontFamily = textFontFamily;

        _scopePlot.PointerWheelChanged += (_, _) => _scopeFollowLive = false;
        _scopePlot.PointerPressed += (_, _) => _scopeFollowLive = false;

        GraphSeriesDefinition[] graphSeries = InfoTabCatalog.RateScope.GraphSeries.ToArray();
        _scopeSeries = graphSeries.Where(series => series.RenderMode == GraphSeriesRenderMode.Line).ToArray();
        _rateSeries = graphSeries.Where(series => series.RenderMode == GraphSeriesRenderMode.Points).ToArray();

        _scopeX = CreateSeriesLists(_scopeSeries.Length);
        _scopeY = CreateSeriesLists(_scopeSeries.Length);
        _rateX = CreateSeriesLists(_rateSeries.Length);
        _rateY = CreateSeriesLists(_rateSeries.Length);
    }

    public void ApplyTheme(PlotThemePalette theme)
    {
        _theme = theme;
        ApplyPlotTheme(_scopePlot.Plot);
        ApplyPlotTheme(_ratePlot.Plot);
        ApplySeriesTheme();
        _scopePlot.Refresh();
        _ratePlot.Refresh();
    }

    public void CreateGraphs(double rateErrorYScale, int rateDataPoints)
    {
        _rateErrorYScale = rateErrorYScale;
        _rateDataPoints = rateDataPoints;
        _scopeFollowLive = true;
        Plot scope = _scopePlot.Plot;
        scope.Clear();
        ApplyPlotTheme(scope);
        scope.YLabel("Amplitude");
        scope.XLabel("Time");
        scope.Axes.SetLimitsY(0, 0.1);
        HideXTickLabels(scope);
        ClearSeriesData(_scopeX, _scopeY);
        DropScopeMarkerPool();
        AddScopePlottables();
        _scopeReviewCursor = AddReviewCursor(scope);
        scope.ShowLegend();

        Plot rate = _ratePlot.Plot;
        rate.Clear();
        ApplyPlotTheme(rate);
        rate.YLabel("Rate Error (ms)");
        rate.XLabel("Time");
        rate.Axes.SetLimitsY(-rateErrorYScale, rateErrorYScale);
        rate.Axes.SetLimitsX(0, rateDataPoints);
        HideXTickLabels(rate);
        ClearSeriesData(_rateX, _rateY);
        AddRatePlottables();
        rate.ShowLegend();

        _scopePlot.Refresh();
        _ratePlot.Refresh();
    }

    public void Reset(double rateErrorYScale, int rateDataPoints)
    {
        _rateErrorYScale = rateErrorYScale;
        _rateDataPoints = rateDataPoints;
        _scopeFollowLive = true;
        Plot scope = _scopePlot.Plot;
        scope.Clear();
        ApplyPlotTheme(scope);
        ClearSeriesData(_scopeX, _scopeY);
        DropScopeMarkerPool();
        AddScopePlottables();
        _scopeReviewCursor = AddReviewCursor(scope);
        _scopePlot.Refresh();

        Plot rate = _ratePlot.Plot;
        rate.Clear();
        ApplyPlotTheme(rate);
        rate.Axes.SetLimitsY(-rateErrorYScale, rateErrorYScale);
        rate.Axes.SetLimitsX(0, rateDataPoints);
        ClearSeriesData(_rateX, _rateY);
        AddRatePlottables();
        _ratePlot.Refresh();
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        bool scopeUpdated = ReplaceScopeSeries(frame);
        bool rateUpdated = ReplaceRateSeries(frame);
        // Review cursor on the waveform pane only: its x base is absolute sample
        // ticks, so stream time maps onto it (the Multi-Filter Scope mapping).
        // The rate pane plots a fixed beat-index ring (0..rateDataPoints), not
        // stream time, so the review-cursor contract has no meaningful x mapping
        // there.
        bool cursorMoved = UpdateReviewCursor(context);

        if (rateUpdated)
        {
            _ratePlot.Refresh();
        }

        if (scopeUpdated)
        {
            UpdateScopeMarkers(frame);
            if (_scopeFollowLive)
            {
                double width = (double)context.SampleRate / Math.Max(1, context.ScopeScale);
                double end = frame.GraphTickEnd;
                _scopePlot.Plot.Axes.SetLimitsX(end - width, end);
                _scopePlot.Plot.Axes.AutoScaleY();
            }
        }

        if (scopeUpdated || cursorMoved)
        {
            _scopePlot.Refresh();
        }
    }

    /// <summary>Review-cursor contract: a dotted marker at the scrub time on the waveform pane.</summary>
    private bool UpdateReviewCursor(AnalysisTabRenderContext context)
    {
        if (_scopeReviewCursor == null)
        {
            return false;
        }

        bool visible = context.ReviewCursorTimeS is not null;
        bool changed = false;

        if (_scopeReviewCursor.IsVisible != visible)
        {
            _scopeReviewCursor.IsVisible = visible;
            changed = true;
        }

        if (context.ReviewCursorTimeS is double timeS)
        {
            double x = timeS * context.SampleRate;
            if (Math.Abs(_scopeReviewCursor.Start.X - x) > double.Epsilon)
            {
                _scopeReviewCursor.Line = new CoordinateLine(x, -1e6, x, 1e6);
                changed = true;
            }
        }

        return changed;
    }

    private LinePlot AddReviewCursor(Plot plot)
    {
        LinePlot cursor = plot.Add.Line(0.0, 0.0, 0.0, 0.0);
        cursor.MarkerStyle.IsVisible = false;
        cursor.LineWidth = 1;
        cursor.LinePattern = LinePattern.Dotted;
        cursor.LineColor = Color.FromARGB(_theme.TextPrimary);
        cursor.IsVisible = false;
        return cursor;
    }

    /// <summary>Resets the rate plot (top) to its configured limits.</summary>
    public void ResetRateView()
    {
        _ratePlot.Plot.Axes.SetLimitsY(-_rateErrorYScale, _rateErrorYScale);
        _ratePlot.Plot.Axes.SetLimitsX(0, _rateDataPoints);
        _ratePlot.Refresh();
    }

    /// <summary>Restores the scope plot (bottom): re-arms live auto-follow and refits.</summary>
    public void ResetScopeView()
    {
        _scopeFollowLive = true;
        _scopePlot.Plot.Axes.AutoScale();
        _scopePlot.Refresh();
    }

    private bool ReplaceScopeSeries(AnalysisFrame frame)
    {
        bool updated = false;
        for (int i = 0; i < _scopeSeries.Length; i++)
        {
            GraphSeriesFrame? series = FindSeries(frame.ScopeSeries, _scopeSeries[i].Id);
            if (series == null)
            {
                continue;
            }

            updated |= SeriesDataReducer.TryReplaceSeriesData(series, _scopeX[i], _scopeY[i], _scopeSeries[i].TargetPointBudget);
        }

        return updated;
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

    private void AddScopePlottables()
    {
        Plot scope = _scopePlot.Plot;
        _scopePlots.Clear();
        for (int i = 0; i < _scopeSeries.Length; i++)
        {
            GraphSeriesDefinition spec = _scopeSeries[i];
            Scatter sc = scope.Add.Scatter(_scopeX[i], _scopeY[i]);
            sc.LineWidth = 1;
            sc.LineColor = Color.FromARGB(ThemeColor(spec));
            sc.MarkerStyle.IsVisible = false;
            if (spec.FillAlpha > 0)
            {
                sc.FillY = true;
                sc.FillYColor = Color.FromARGB(ThemeColor(spec)).WithAlpha((byte)spec.FillAlpha);
            }
            sc.LegendText = spec.Name;
            _scopePlots.Add(sc);
        }
    }

    private void AddRatePlottables()
    {
        Plot rate = _ratePlot.Plot;
        _ratePlots.Clear();
        for (int i = 0; i < _rateSeries.Length; i++)
        {
            GraphSeriesDefinition spec = _rateSeries[i];
            Scatter sc = rate.Add.Scatter(_rateX[i], _rateY[i]);
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
        for (int i = 0; i < _scopePlots.Count && i < _scopeSeries.Length; i++)
        {
            uint color = ThemeColor(_scopeSeries[i]);
            _scopePlots[i].LineColor = Color.FromARGB(color);
            if (_scopeSeries[i].FillAlpha > 0)
            {
                _scopePlots[i].FillYColor = Color.FromARGB(color).WithAlpha((byte)_scopeSeries[i].FillAlpha);
            }
        }

        for (int i = 0; i < _ratePlots.Count && i < _rateSeries.Length; i++)
        {
            _ratePlots[i].MarkerColor = Color.FromARGB(ThemeColor(_rateSeries[i]));
        }

        if (_scopeReviewCursor != null)
        {
            _scopeReviewCursor.LineColor = Color.FromARGB(_theme.TextPrimary);
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

    private uint ThemeColor(GraphSeriesDefinition spec) => spec.Id switch
    {
        // Waveform = wave color; tick beats green; tock beats (and trigger) red.
        AnalysisGraphSeries.ScopePcm => _theme.TraceWave,
        AnalysisGraphSeries.ScopeThreshold => _theme.TraceTock,
        AnalysisGraphSeries.RateTic => _theme.TraceTick,
        AnalysisGraphSeries.RateToc => _theme.TraceTock,
        _ => _theme.TraceWave,
    };

    private static List<double>[] CreateSeriesLists(int count)
    {
        var lists = new List<double>[count];
        for (int i = 0; i < count; i++)
        {
            lists[i] = new List<double>();
        }

        return lists;
    }

    private static void ClearSeriesData(List<double>[] xs, List<double>[] ys)
    {
        for (int i = 0; i < xs.Length; i++)
        {
            xs[i].Clear();
            ys[i].Clear();
        }
    }

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

    private static void HideXTickLabels(Plot plot)
    {
        plot.Axes.Bottom.TickLabelStyle.IsVisible = false;
    }

    /// <summary>Pool cleanup for paths that already detached everything via Plot.Clear().</summary>
    private void DropScopeMarkerPool()
    {
        _scopeLinePool.Clear();
        _scopeTextPool.Clear();
        _scopeLinesUsed = 0;
        _scopeTextsUsed = 0;
    }

    private void UpdateScopeMarkers(AnalysisFrame frame)
    {
        _scopeLinesUsed = 0;
        _scopeTextsUsed = 0;

        foreach (ScopeVerticalMarker marker in frame.VerticalMarkers)
        {
            AddVerticalMarker(marker.X, marker.Height, marker.Color);
        }

        foreach (ScopeHorizontalMarker marker in frame.HorizontalMarkers)
        {
            if (marker.Direction == HorizontalMarkerDirection.Inward)
            {
                AddHorizontalMarkerInward(marker.XLeft, marker.XRight, marker.Length, marker.Height, marker.Color);
            }
            else
            {
                AddHorizontalMarkerOutward(marker.XLeft, marker.XRight, marker.Height, marker.Color);
            }
        }

        foreach (ScopeTextMarker marker in frame.TextMarkers)
        {
            AddText(marker.X, marker.Height, marker.Text, marker.Color, marker.Alignment);
        }

        for (int i = _scopeLinesUsed; i < _scopeLinePool.Count; i++)
        {
            _scopeLinePool[i].IsVisible = false;
        }
        for (int i = _scopeTextsUsed; i < _scopeTextPool.Count; i++)
        {
            _scopeTextPool[i].IsVisible = false;
        }
    }

    private LinePlot AcquireLine()
    {
        if (_scopeLinesUsed < _scopeLinePool.Count)
        {
            LinePlot pooled = _scopeLinePool[_scopeLinesUsed++];
            pooled.IsVisible = true;
            return pooled;
        }

        LinePlot created = _scopePlot.Plot.Add.Line(0.0, 0.0, 0.0, 0.0);
        created.MarkerStyle.IsVisible = false;
        _scopeLinePool.Add(created);
        _scopeLinesUsed++;
        return created;
    }

    private Text AcquireText()
    {
        if (_scopeTextsUsed < _scopeTextPool.Count)
        {
            Text pooled = _scopeTextPool[_scopeTextsUsed++];
            pooled.IsVisible = true;
            return pooled;
        }

        Text created = _scopePlot.Plot.Add.Text("", 0.0, 0.0);
        created.LabelFontName = _textFontFamily;
        created.LabelFontSize = 10;
        _scopeTextPool.Add(created);
        _scopeTextsUsed++;
        return created;
    }

    private void AddVerticalMarker(double x, double height, uint color)
    {
        LinePlot line = AcquireLine();
        line.Line = new CoordinateLine(x, 0.0, x, height);
        line.LineColor = Color.FromARGB(ThemeColor(color));
        line.LineWidth = 2;
        line.LinePattern = LinePattern.Dashed;
    }

    private void AddText(double x, double height, string text, uint color, MarkerTextAlignment alignment)
    {
        Text label = AcquireText();
        label.LabelText = text;
        label.Location = new Coordinates(x, height);
        label.LabelFontColor = Color.FromARGB(ThemeColor(color));
        label.Alignment = MapAlignment(alignment);
    }

    private static Alignment MapAlignment(MarkerTextAlignment alignment) => alignment switch
    {
        MarkerTextAlignment.CenterTop => Alignment.UpperCenter,
        MarkerTextAlignment.LeftTop => Alignment.UpperLeft,
        _ => Alignment.UpperLeft,
    };

    private void AddHorizontalMarkerInward(double xLeft, double xRight, double length, double height, uint color)
    {
        Color c = Color.FromARGB(ThemeColor(color));

        LinePlot left = AcquireLine();
        left.Line = new CoordinateLine(xLeft - length, height, xLeft, height);
        left.LineColor = c;
        left.LineWidth = 1;
        left.LinePattern = LinePattern.Solid;

        LinePlot right = AcquireLine();
        right.Line = new CoordinateLine(xRight, height, xRight + length, height);
        right.LineColor = c;
        right.LineWidth = 1;
        right.LinePattern = LinePattern.Solid;
    }

    private void AddHorizontalMarkerOutward(double xLeft, double xRight, double height, uint color)
    {
        LinePlot line = AcquireLine();
        line.Line = new CoordinateLine(xLeft, height, xRight, height);
        line.LineColor = Color.FromARGB(ThemeColor(color));
        line.LineWidth = 1;
        line.LinePattern = LinePattern.Solid;
    }

    private uint ThemeColor(uint sourceColor) => sourceColor switch
    {
        Argb.Green => _theme.TraceTick,
        Argb.Red => _theme.TraceTock,
        Argb.Black => _theme.TextPrimary,
        _ => sourceColor,
    };
}
