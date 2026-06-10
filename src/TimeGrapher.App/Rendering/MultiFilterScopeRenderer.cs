using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Multi-Filter Scope: four vertically stacked views (F0..F3, one lane per
/// <see cref="MultiFilterScopeLanes"/> entry) of the same signal, refilled in
/// place by series id from the frame's Core-decimated replace snapshots so the
/// four filters are easy to switch between and compare. Each plot windows its
/// X axis to the last two seconds of its own series (x = absolute sample ticks
/// on the projector's counter, so limits derive from the series' own max x).
/// Honors the review-cursor contract by mapping the scrubbed stream time to
/// sample ticks on every lane.
/// </summary>
internal sealed class MultiFilterScopeRenderer
{
    private readonly AvaPlot[] _plots;
    private readonly List<double>[] _x;
    private readonly List<double>[] _y;
    private readonly Scatter?[] _scatters;
    private readonly LinePlot?[] _cursors;

    private PlotThemePalette _theme = PlotThemePalette.Current;
    private bool _followLive = true;

    public MultiFilterScopeRenderer(IReadOnlyList<AvaPlot> plots)
    {
        if (plots.Count != MultiFilterScopeLanes.All.Count)
        {
            throw new ArgumentException(
                $"Expected one plot per filter lane ({MultiFilterScopeLanes.All.Count}).", nameof(plots));
        }

        _plots = plots.ToArray();
        _x = new List<double>[_plots.Length];
        _y = new List<double>[_plots.Length];
        _scatters = new Scatter?[_plots.Length];
        _cursors = new LinePlot?[_plots.Length];

        for (int i = 0; i < _plots.Length; i++)
        {
            _x[i] = new List<double>();
            _y[i] = new List<double>();
            _plots[i].PointerWheelChanged += (_, _) => _followLive = false;
            _plots[i].PointerPressed += (_, _) => _followLive = false;
        }
    }

    public void ApplyTheme(PlotThemePalette theme)
    {
        _theme = theme;
        for (int i = 0; i < _plots.Length; i++)
        {
            ApplyPlotTheme(_plots[i].Plot);
        }

        ApplySeriesTheme();
        RefreshAll();
    }

    public void CreateGraphs()
    {
        _followLive = true;
        for (int i = 0; i < _plots.Length; i++)
        {
            Plot plot = _plots[i].Plot;
            plot.Clear();
            _x[i].Clear();
            _y[i].Clear();
            ApplyPlotTheme(plot);
            plot.YLabel(MultiFilterScopeLanes.All[i].Label);
            plot.Axes.Bottom.TickLabelStyle.IsVisible = false;
            _scatters[i] = plot.Add.Scatter(_x[i], _y[i]);
            _scatters[i]!.LineWidth = 1;
            _scatters[i]!.MarkerStyle.IsVisible = false;
            _cursors[i] = AddCursor(plot);
        }

        ApplySeriesTheme();
        RefreshAll();
    }

    public void Reset()
    {
        CreateGraphs();
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        for (int i = 0; i < _plots.Length; i++)
        {
            bool updated = SeriesDataReducer.TryReplaceSeriesData(
                FindSeries(frame, MultiFilterScopeLanes.All[i].SeriesId),
                _x[i], _y[i], MultiFilterFrameProjector.FilterPointBudget);
            bool cursorMoved = UpdateReviewCursor(i, context);

            if (updated && _followLive && _x[i].Count > 0)
            {
                // Window to the last 2 s of this lane's own x base (sample ticks).
                double end = _x[i][^1];
                double width = (double)MultiFilterFrameProjector.WindowSeconds * context.SampleRate;
                _plots[i].Plot.Axes.SetLimitsX(end - width, end);
                _plots[i].Plot.Axes.AutoScaleY();
            }

            if (updated || cursorMoved)
            {
                _plots[i].Refresh();
            }
        }
    }

    /// <summary>Review-cursor contract: a dotted marker at the scrub time on every lane.</summary>
    private bool UpdateReviewCursor(int lane, AnalysisTabRenderContext context)
    {
        LinePlot? cursor = _cursors[lane];
        if (cursor == null)
        {
            return false;
        }

        bool visible = context.ReviewCursorTimeS is not null;
        bool changed = false;

        if (cursor.IsVisible != visible)
        {
            cursor.IsVisible = visible;
            changed = true;
        }

        if (context.ReviewCursorTimeS is double timeS)
        {
            // The lanes plot absolute sample ticks; map the stream time onto them.
            double x = timeS * context.SampleRate;
            if (Math.Abs(cursor.Start.X - x) > double.Epsilon)
            {
                cursor.Line = new CoordinateLine(x, -1e6, x, 1e6);
                changed = true;
            }
        }

        return changed;
    }

    private static LinePlot AddCursor(Plot plot)
    {
        LinePlot cursor = plot.Add.Line(0.0, 0.0, 0.0, 0.0);
        cursor.MarkerStyle.IsVisible = false;
        cursor.LineWidth = 1;
        cursor.LinePattern = LinePattern.Dotted;
        cursor.IsVisible = false;
        return cursor;
    }

    private void ApplySeriesTheme()
    {
        for (int i = 0; i < _plots.Length; i++)
        {
            if (_scatters[i] is { } scatter)
            {
                scatter.LineColor = Color.FromARGB(MultiFilterScopeLanes.All[i].Color(_theme));
            }

            if (_cursors[i] is { } cursor)
            {
                cursor.LineColor = Color.FromARGB(_theme.TextPrimary);
            }
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

    private void RefreshAll()
    {
        for (int i = 0; i < _plots.Length; i++)
        {
            _plots[i].Refresh();
        }
    }

    private static GraphSeriesFrame? FindSeries(AnalysisFrame frame, string id)
    {
        foreach (GraphSeriesFrame series in frame.ScopeSeries)
        {
            if (series.Id == id)
            {
                return series;
            }
        }

        return null;
    }
}
