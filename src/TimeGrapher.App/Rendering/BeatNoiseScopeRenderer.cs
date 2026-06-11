using Avalonia.Controls;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Beat-Noise Scope (Scope 1 + Scope 2), rendered from the cumulative
/// BeatSegmentsSnapshot the frame carries.
///
/// Scope 1: the selected-or-latest beat segment on the main plot (X in ms,
/// clipped to the 20/200/400 ms range), with pooled A / C-peak / C-onset
/// marker lines and an optional mirrored view. The capture's envelope is
/// already rectified (absolute value); MIRROR additionally draws its negation
/// to approximate the bipolar waveform look of the reference display. The
/// frameless strip lane below compresses the 8 most recent beats side by side;
/// pressing a strip selects it (a pooled span highlights the slot).
///
/// Scope 2: the two phase-alternating averaged lanes on a fixed 0-20 ms axis,
/// vertically offset and labeled trace 1/2 (never tic/toc), with the per-lane
/// average amplitude and Σ progress in the readout below.
///
/// All plottables refill in place; re-renders only when the snapshot version
/// changes, so coalesced or repeated frames cost nothing. Segments reference
/// pooled Core buffers that stay valid only until rotated out, so every render
/// re-reads from the latest snapshot and nothing UI-side caches sample data
/// beyond it.
/// </summary>
internal sealed class BeatNoiseScopeRenderer
{
    public const int DefaultRangeMs = 400;

    private const int StripPointBudget = 200;
    private const double Lane2Baseline = 0.0;
    private const double Lane1Baseline = 1.2;
    private const byte SelectionFillAlpha = 48;

    private readonly AvaPlot _mainPlot;
    private readonly AvaPlot _stripPlot;
    private readonly AvaPlot _averagePlot;
    private readonly TextBlock _liftText;
    private readonly TextBlock _averageText;

    private readonly List<double> _mainX = new();
    private readonly List<double> _mainY = new();
    private readonly List<double> _mainYMirror = new();
    private readonly List<double>[] _stripX;
    private readonly List<double>[] _stripY;
    private readonly List<double> _lane1X = new();
    private readonly List<double> _lane1Y = new();
    private readonly List<double> _lane2X = new();
    private readonly List<double> _lane2Y = new();

    private Scatter? _mainScatter;
    private Scatter? _mirrorScatter;
    private VerticalLine? _aMarker;
    private VerticalLine? _cPeakMarker;
    private VerticalLine? _cOnsetMarker;
    private ReviewCursorLayer? _reviewCursor;
    private readonly Scatter?[] _stripScatters;
    private HorizontalSpan? _selectionSpan;
    private Scatter? _lane1Scatter;
    private Scatter? _lane2Scatter;

    private PlotThemePalette _theme = PlotThemePalette.Current;
    private ulong _lastVersion;
    private int _rangeMs = DefaultRangeMs;
    private bool _mirror;
    private int? _selectedSlot;
    private BeatSegmentsSnapshot? _lastSnapshot;
    private double? _lastCursorTimeS;

    public BeatNoiseScopeRenderer(
        AvaPlot mainPlot,
        AvaPlot stripPlot,
        AvaPlot averagePlot,
        TextBlock liftText,
        TextBlock averageText)
    {
        _mainPlot = mainPlot;
        _stripPlot = stripPlot;
        _averagePlot = averagePlot;
        _liftText = liftText;
        _averageText = averageText;

        _stripX = new List<double>[BeatNoiseScopeLogic.StripCount];
        _stripY = new List<double>[BeatNoiseScopeLogic.StripCount];
        _stripScatters = new Scatter?[BeatNoiseScopeLogic.StripCount];
        for (int i = 0; i < BeatNoiseScopeLogic.StripCount; i++)
        {
            _stripX[i] = new List<double>();
            _stripY[i] = new List<double>();
        }
    }

    public int RangeMs => _rangeMs;

    public void ApplyTheme(PlotThemePalette theme)
    {
        _theme = theme;
        ApplyPlotTheme(_mainPlot.Plot);
        ApplyPlotTheme(_stripPlot.Plot);
        ApplyPlotTheme(_averagePlot.Plot);
        ApplySeriesTheme();
        RefreshAll();
    }

    public void CreateGraphs()
    {
        _lastVersion = 0;
        _lastSnapshot = null;
        _selectedSlot = null;
        _lastCursorTimeS = null;
        _liftText.Text = "LIFT —";
        _averageText.Text = BeatNoiseScopeLogic.AverageLine(BeatNoiseAverageSnapshot.Empty);

        Plot main = _mainPlot.Plot;
        main.Clear();
        _mainX.Clear();
        _mainY.Clear();
        _mainYMirror.Clear();
        ApplyPlotTheme(main);
        main.YLabel("Envelope");
        main.XLabel("ms");
        _mainScatter = main.Add.Scatter(_mainX, _mainY);
        _mainScatter.LineWidth = 1;
        _mainScatter.MarkerStyle.IsVisible = false;
        _mirrorScatter = main.Add.Scatter(_mainX, _mainYMirror);
        _mirrorScatter.LineWidth = 1;
        _mirrorScatter.MarkerStyle.IsVisible = false;
        _mirrorScatter.IsVisible = _mirror;
        _aMarker = AddMarker(main, LinePattern.Dashed);
        _cPeakMarker = AddMarker(main, LinePattern.Dashed);
        _cOnsetMarker = AddMarker(main, LinePattern.Dotted);
        _reviewCursor = AddCursor(main);
        ApplyRangeLimits();
        PlotAxisRules.ClampLeftEdgeToZero(main);

        // Frameless strip lane: the data area fills the whole control, so a
        // pointer x-fraction maps directly onto the 8 slots.
        Plot strip = _stripPlot.Plot;
        strip.Clear();
        ApplyPlotTheme(strip);
        strip.Layout.Frameless();
        strip.Grid.IsVisible = false;
        strip.Axes.SetLimits(0, BeatNoiseScopeLogic.StripCount, 0, 1);
        _selectionSpan = strip.Add.HorizontalSpan(0.0, 1.0);
        _selectionSpan.IsVisible = false;
        for (int i = 0; i < BeatNoiseScopeLogic.StripCount; i++)
        {
            _stripX[i].Clear();
            _stripY[i].Clear();
            _stripScatters[i] = strip.Add.Scatter(_stripX[i], _stripY[i]);
            _stripScatters[i]!.LineWidth = 1;
            _stripScatters[i]!.MarkerStyle.IsVisible = false;
        }

        Plot average = _averagePlot.Plot;
        average.Clear();
        _lane1X.Clear();
        _lane1Y.Clear();
        _lane2X.Clear();
        _lane2Y.Clear();
        ApplyPlotTheme(average);
        average.YLabel("trace 2   ·   trace 1");
        average.XLabel("ms");
        average.Axes.Left.TickLabelStyle.IsVisible = false;
        _lane1Scatter = average.Add.Scatter(_lane1X, _lane1Y);
        _lane1Scatter.LineWidth = 1;
        _lane1Scatter.MarkerStyle.IsVisible = false;
        _lane2Scatter = average.Add.Scatter(_lane2X, _lane2Y);
        _lane2Scatter.LineWidth = 1;
        _lane2Scatter.MarkerStyle.IsVisible = false;
        average.Axes.SetLimits(0, BeatNoiseAverager.LaneWindowMs, -0.1, Lane1Baseline + 1.15);
        PlotAxisRules.ClampLeftEdgeToZero(average);

        ApplySeriesTheme();
        RefreshAll();
    }

    public void Reset()
    {
        CreateGraphs();
    }

    /// <summary>Scope 1 range selector (20 / 200 / 400 ms).</summary>
    public void SetRangeMs(int rangeMs)
    {
        if (_rangeMs == rangeMs)
        {
            return;
        }

        _rangeMs = rangeMs;
        ApplyRangeLimits();
        if (_lastSnapshot is { } snapshot)
        {
            RenderMain(snapshot);
        }

        _mainPlot.Refresh();
    }

    /// <summary>Scope 1 MIRROR toggle (bipolar mirrored view of the rectified envelope).</summary>
    public void SetMirror(bool mirror)
    {
        if (_mirror == mirror)
        {
            return;
        }

        _mirror = mirror;
        if (_mirrorScatter != null)
        {
            _mirrorScatter.IsVisible = mirror;
        }

        if (_lastSnapshot is { } snapshot)
        {
            RenderMain(snapshot);
        }

        _mainPlot.Refresh();
    }

    /// <summary>Strip-lane press at the given horizontal fraction (0..1) of the control.</summary>
    public void SelectStripAtFraction(double fraction)
    {
        if (_lastSnapshot is not { } snapshot)
        {
            return;
        }

        int slot = BeatNoiseScopeLogic.StripSlotFromFraction(fraction);
        _selectedSlot = BeatNoiseScopeLogic.NextSelection(_selectedSlot, slot, snapshot.Segments.Count);
        RenderMain(snapshot);
        UpdateSelectionSpan(snapshot);
        // The review cursor's offset is relative to the DISPLAYED segment's
        // window start; changing the selection while paused must recompute it
        // (no new frame arrives to do so), or the cursor stays drawn at the
        // previous segment's offset — possibly outside the new beat entirely.
        UpdateReviewCursor(_lastCursorTimeS);
        _mainPlot.Refresh();
        _stripPlot.Refresh();
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        BeatSegmentsSnapshot? snapshot = frame.BeatSegments;
        if (snapshot != null)
        {
            _lastSnapshot = snapshot;
        }

        _lastCursorTimeS = context.ReviewCursorTimeS;
        bool cursorMoved = UpdateReviewCursor(context.ReviewCursorTimeS);
        if (snapshot == null || snapshot.Version == _lastVersion)
        {
            if (cursorMoved)
            {
                _mainPlot.Refresh();
            }

            return;
        }

        _lastVersion = snapshot.Version;
        RenderMain(snapshot);
        RenderStrips(snapshot);
        RenderAverage(snapshot.Average);
        _liftText.Text = BeatNoiseScopeLogic.LiftText(snapshot.LiftAngleDeg);
        RefreshAll();
    }

    private void RenderMain(BeatSegmentsSnapshot snapshot)
    {
        BeatSegment? segment = BeatNoiseScopeLogic.DisplayedSegment(snapshot, _selectedSlot);
        _mainX.Clear();
        _mainY.Clear();
        _mainYMirror.Clear();

        if (segment == null)
        {
            SetMarker(_aMarker, null);
            SetMarker(_cPeakMarker, null);
            SetMarker(_cOnsetMarker, null);
            return;
        }

        ReadOnlySpan<float> samples = segment.Samples.Span;
        double rangeMax = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            double x = i * segment.MsPerPoint;
            double y = samples[i];
            _mainX.Add(x);
            _mainY.Add(y);
            _mainYMirror.Add(-y);
            if (x <= _rangeMs && y > rangeMax)
            {
                rangeMax = y;
            }
        }

        if (rangeMax <= 0.0)
        {
            rangeMax = 1.0;
        }

        double yMax = rangeMax * 1.1;
        _mainPlot.Plot.Axes.SetLimitsY(_mirror ? -yMax : -0.02 * yMax, yMax);

        SetMarker(_aMarker, segment.AOffsetMs);
        SetMarker(_cPeakMarker, segment.CPeakValid ? segment.CPeakOffsetMs : null);
        SetMarker(_cOnsetMarker, segment.COnsetValid ? segment.COnsetOffsetMs : null);
    }

    private void RenderStrips(BeatSegmentsSnapshot snapshot)
    {
        for (int slot = 0; slot < BeatNoiseScopeLogic.StripCount; slot++)
        {
            List<double> x = _stripX[slot];
            List<double> y = _stripY[slot];
            x.Clear();
            y.Clear();

            int index = BeatNoiseScopeLogic.SegmentIndexForSlot(slot, snapshot.Segments.Count);
            if (index < 0)
            {
                continue;
            }

            // Compress each segment into its slot via the shared strip-lane
            // sampling policy (max-decimate + per-segment peak normalization).
            int stripSlot = slot;
            EnvelopeLaneSampler.MaxDecimateNormalized(
                snapshot.Segments[index].Samples.Span, StripPointBudget,
                (p, points, _, normalized) =>
                {
                    x.Add(stripSlot + 0.03 + 0.94 * p / (points - 1.0));
                    y.Add(0.05 + 0.9 * normalized);
                });
        }

        UpdateSelectionSpan(snapshot);
    }

    private void UpdateSelectionSpan(BeatSegmentsSnapshot snapshot)
    {
        if (_selectionSpan == null)
        {
            return;
        }

        bool visible = _selectedSlot is int slot
            && BeatNoiseScopeLogic.SegmentIndexForSlot(slot, snapshot.Segments.Count) >= 0;
        _selectionSpan.IsVisible = visible;
        if (visible)
        {
            _selectionSpan.X1 = _selectedSlot!.Value;
            _selectionSpan.X2 = _selectedSlot.Value + 1;
        }
    }

    private void RenderAverage(BeatNoiseAverageSnapshot average)
    {
        // One shared scale across both lanes so their relative amplitude shows.
        double max = 0.0;
        foreach (float value in average.Lane1)
        {
            if (value > max)
            {
                max = value;
            }
        }

        foreach (float value in average.Lane2)
        {
            if (value > max)
            {
                max = value;
            }
        }

        if (max <= 0.0)
        {
            max = 1.0;
        }

        FillLane(_lane1X, _lane1Y, average.Lane1, average.MsPerPoint, Lane1Baseline, max);
        FillLane(_lane2X, _lane2Y, average.Lane2, average.MsPerPoint, Lane2Baseline, max);
        _averageText.Text = BeatNoiseScopeLogic.AverageLine(average);
    }

    private static void FillLane(
        List<double> x, List<double> y, IReadOnlyList<float> lane,
        double msPerPoint, double baseline, double scale)
    {
        x.Clear();
        y.Clear();
        for (int i = 0; i < lane.Count; i++)
        {
            x.Add(i * msPerPoint);
            y.Add(baseline + lane[i] / scale);
        }
    }

    /// <summary>Review-cursor contract: a dotted marker at the scrub time's in-window offset.</summary>
    private bool UpdateReviewCursor(double? reviewCursorTimeS)
    {
        if (_reviewCursor == null)
        {
            return false;
        }

        BeatSegment? segment = _lastSnapshot is { } snapshot
            ? BeatNoiseScopeLogic.DisplayedSegment(snapshot, _selectedSlot)
            : null;
        double? offsetMs = BeatNoiseScopeLogic.CursorOffsetMs(reviewCursorTimeS, segment);
        return _reviewCursor.Update(offsetMs);
    }

    private void ApplyRangeLimits()
    {
        _mainPlot.Plot.Axes.SetLimitsX(0, _rangeMs);
    }

    private static VerticalLine AddMarker(Plot plot, LinePattern pattern)
    {
        VerticalLine marker = plot.Add.VerticalLine(0.0);
        marker.LineWidth = 1;
        marker.LinePattern = pattern;
        marker.IsVisible = false;
        marker.EnableAutoscale = false;
        return marker;
    }

    private static void SetMarker(VerticalLine? marker, double? x)
    {
        if (marker == null)
        {
            return;
        }

        marker.IsVisible = x is not null;
        if (x is double position)
        {
            marker.X = position;
        }
    }

    private ReviewCursorLayer AddCursor(Plot plot)
    {
        var cursor = new ReviewCursorLayer(plot);
        cursor.ApplyTheme(_theme);
        return cursor;
    }

    private void ApplySeriesTheme()
    {
        if (_mainScatter != null)
        {
            _mainScatter.LineColor = Color.FromARGB(_theme.TraceWave);
        }

        if (_mirrorScatter != null)
        {
            _mirrorScatter.LineColor = Color.FromARGB(_theme.TraceWave);
        }

        // A = tick green, C = tock red: the same themed event color mapping the
        // scope markers use (RateScopeRenderer.ThemeColor).
        if (_aMarker != null)
        {
            _aMarker.LineColor = Color.FromARGB(_theme.TraceTick);
        }

        if (_cPeakMarker != null)
        {
            _cPeakMarker.LineColor = Color.FromARGB(_theme.TraceTock);
        }

        if (_cOnsetMarker != null)
        {
            _cOnsetMarker.LineColor = Color.FromARGB(_theme.TraceTock);
        }

        foreach (Scatter? strip in _stripScatters)
        {
            if (strip != null)
            {
                strip.LineColor = Color.FromARGB(_theme.TraceWave);
            }
        }

        if (_selectionSpan != null)
        {
            _selectionSpan.FillStyle.Color = Color.FromARGB(_theme.TraceTick).WithAlpha(SelectionFillAlpha);
            _selectionSpan.LineStyle.Color = Color.FromARGB(_theme.TraceTick);
        }

        // Lane colors only distinguish the two traces; the labels stay 1/2.
        if (_lane1Scatter != null)
        {
            _lane1Scatter.LineColor = Color.FromARGB(_theme.TraceTick);
        }

        if (_lane2Scatter != null)
        {
            _lane2Scatter.LineColor = Color.FromARGB(_theme.TraceTock);
        }

        _reviewCursor?.ApplyTheme(_theme);
    }

    private void ApplyPlotTheme(Plot plot)
    {
        PlotThemeHelper.Apply(plot, _theme);
    }

    private void RefreshAll()
    {
        _mainPlot.Refresh();
        _stripPlot.Refresh();
        _averagePlot.Refresh();
    }
}
