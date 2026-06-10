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
    private readonly ReviewCursorLayer?[] _cursors;

    // Identity gate on the projector's shared-instance pattern: between
    // publish-floor rebuilds (and on every paused-scrub re-route) frames
    // re-attach the same immutable GraphSeriesFrame per lane, and a rebuild
    // always allocates new ones — so reference equality is a correct change
    // detector and skips the redundant copy/rescale/refresh.
    private readonly GraphSeriesFrame?[] _lastSeries;

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
        _cursors = new ReviewCursorLayer?[_plots.Length];
        _lastSeries = new GraphSeriesFrame?[_plots.Length];

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
            _lastSeries[i] = null;
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

    /// <summary>
    /// Re-arms live windowing on all four lanes after a pan/zoom (the one-way
    /// follow-live latch otherwise sticks until the session restarts).
    /// </summary>
    public void ResetView()
    {
        _followLive = true;
        for (int i = 0; i < _plots.Length; i++)
        {
            _plots[i].Plot.Axes.AutoScale();
        }

        RefreshAll();
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        for (int i = 0; i < _plots.Length; i++)
        {
            GraphSeriesFrame? laneSeries = SeriesDataReducer.FindSeries(
                frame.ScopeSeries, MultiFilterScopeLanes.All[i].SeriesId);
            bool updated = !ReferenceEquals(laneSeries, _lastSeries[i]) &&
                SeriesDataReducer.TryReplaceSeriesData(
                    laneSeries, _x[i], _y[i], MultiFilterFrameProjector.FilterPointBudget);
            if (updated)
            {
                _lastSeries[i] = laneSeries;
            }

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
        // The lanes plot absolute sample ticks; map the stream time onto them.
        return _cursors[lane]?.Update(context.ReviewCursorTimeS * context.SampleRate) ?? false;
    }

    private ReviewCursorLayer AddCursor(Plot plot)
    {
        var cursor = new ReviewCursorLayer(plot);
        cursor.ApplyTheme(_theme);
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

            _cursors[i]?.ApplyTheme(_theme);
        }
    }

    private void ApplyPlotTheme(Plot plot)
    {
        PlotThemeHelper.Apply(plot, _theme);
    }

    private void RefreshAll()
    {
        for (int i = 0; i < _plots.Length; i++)
        {
            _plots[i].Refresh();
        }
    }

}
