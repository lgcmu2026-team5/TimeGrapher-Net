using Avalonia.Controls;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Waveform Comparison Display with Timing Markers, rendered from the
/// cumulative BeatSegmentsSnapshot the frame carries (the Beat-Noise Scope's
/// segment infrastructure reused for cross-beat comparison).
///
/// One plot stacks the recent beats in aligned lanes: lane k draws segment k's
/// envelope normalized to its own peak (so shapes compare directly even when
/// beats differ in loudness) and offset by k * LaneSpacing vertically, with
/// every lane aligned on its A event (x = 0 = A onset; segments are A-anchored
/// by capture). Pooled vertical guides mark x = 0 (A, green) and the mean
/// C-peak interval across the shown lanes (red — the cross-beat consistency
/// reference), and each lane carries a phase + A→C label. The header line
/// above reads the current rate / beat error / BPH from the cumulative
/// metrics history.
///
/// All plottables refill in place; the lanes re-render only when the segments
/// snapshot version changes and the header only when the metrics-history
/// version changes, so coalesced or repeated frames cost nothing. Segments
/// reference pooled Core buffers that stay valid only until rotated out, so
/// every render re-reads from the latest snapshot and nothing UI-side caches
/// sample data beyond it.
/// </summary>
internal sealed class WaveformCompareRenderer
{
    /// <summary>
    /// Max-decimated points per lane (the strip-lane decimation policy):
    /// 8 lanes x 800 points stays inside the scope point budget.
    /// </summary>
    private const int LanePointBudget = 800;

    /// <summary>X range: a small blank strip left of the pre-roll hosts the lane labels.</summary>
    private const double XMinMs = -2.0 * BeatSegmentCapture.PreEventMs;
    private const double XMaxMs = BeatSegmentCapture.WindowMs - BeatSegmentCapture.PreEventMs;
    private const double LaneLabelXMs = XMinMs + 0.5;

    /// <summary>Lane label top relative to the lane baseline (traces peak at +1.0).</summary>
    private const double LaneLabelYOffset = 1.12;

    /// <summary>Headroom above the top lane's normalized peak.</summary>
    private const double YHeadroom = 1.15;

    private readonly AvaPlot _plot;
    private readonly TextBlock _headerText;
    private readonly string _textFontFamily;

    private readonly List<double>[] _laneX;
    private readonly List<double>[] _laneY;

    private readonly Scatter?[] _laneScatters;
    private readonly Text?[] _laneLabels;
    private VerticalLine? _aGuide;
    private VerticalLine? _cMeanGuide;
    private Text? _aGuideLabel;
    private Text? _cMeanGuideLabel;
    private LinePlot? _reviewCursor;

    private PlotThemePalette _theme = PlotThemePalette.Current;
    private ulong _lastVersion;
    private ulong _lastHistoryVersion;
    private BeatSegmentsSnapshot? _lastSnapshot;

    public WaveformCompareRenderer(AvaPlot plot, TextBlock headerText, string textFontFamily)
    {
        _plot = plot;
        _headerText = headerText;
        _textFontFamily = textFontFamily;

        _laneX = new List<double>[WaveformCompareLogic.MaxLanes];
        _laneY = new List<double>[WaveformCompareLogic.MaxLanes];
        _laneScatters = new Scatter?[WaveformCompareLogic.MaxLanes];
        _laneLabels = new Text?[WaveformCompareLogic.MaxLanes];
        for (int i = 0; i < WaveformCompareLogic.MaxLanes; i++)
        {
            _laneX[i] = new List<double>();
            _laneY[i] = new List<double>();
        }
    }

    public void ApplyTheme(PlotThemePalette theme)
    {
        _theme = theme;
        ApplyPlotTheme(_plot.Plot);
        ApplySeriesTheme();
        _plot.Refresh();
    }

    public void CreateGraphs()
    {
        _lastVersion = 0;
        _lastHistoryVersion = 0;
        _lastSnapshot = null;
        _headerText.Text = WaveformCompareLogic.HeaderLine(null);

        Plot plot = _plot.Plot;
        plot.Clear();
        ApplyPlotTheme(plot);
        plot.YLabel("Beats (oldest at the bottom)");
        plot.XLabel("ms from A");
        plot.Axes.Left.TickLabelStyle.IsVisible = false;

        for (int i = 0; i < WaveformCompareLogic.MaxLanes; i++)
        {
            _laneX[i].Clear();
            _laneY[i].Clear();
            _laneScatters[i] = plot.Add.Scatter(_laneX[i], _laneY[i]);
            _laneScatters[i]!.LineWidth = 1;
            _laneScatters[i]!.MarkerStyle.IsVisible = false;
            _laneLabels[i] = AddLabel(plot);
        }

        _aGuide = AddGuide(plot);
        _cMeanGuide = AddGuide(plot);
        _aGuideLabel = AddLabel(plot);
        _cMeanGuideLabel = AddLabel(plot);
        _reviewCursor = AddCursor(plot);

        plot.Axes.SetLimitsX(XMinMs, XMaxMs);
        plot.Axes.SetLimitsY(-0.1, YTop(0));

        ApplySeriesTheme();
        _plot.Refresh();
    }

    public void Reset()
    {
        CreateGraphs();
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        UpdateHeader(frame.MetricsHistory);

        BeatSegmentsSnapshot? snapshot = frame.BeatSegments;
        if (snapshot != null)
        {
            _lastSnapshot = snapshot;
        }

        bool changed = UpdateReviewCursor(context.ReviewCursorTimeS);

        if (snapshot != null && snapshot.Version != _lastVersion)
        {
            _lastVersion = snapshot.Version;
            RenderLanes(snapshot);
            changed = true;
        }

        if (changed)
        {
            _plot.Refresh();
        }
    }

    /// <summary>
    /// Header numeric line, re-formatted only when the history version changes
    /// (the header lives outside the plot, so it never forces a plot refresh).
    /// </summary>
    private void UpdateHeader(BeatMetricsHistorySnapshot? history)
    {
        if (history == null || history.Version == _lastHistoryVersion)
        {
            return;
        }

        _lastHistoryVersion = history.Version;
        _headerText.Text = WaveformCompareLogic.HeaderLine(history);
    }

    private void RenderLanes(BeatSegmentsSnapshot snapshot)
    {
        IReadOnlyList<BeatSegment> segments = snapshot.Segments;
        int count = Math.Min(segments.Count, WaveformCompareLogic.MaxLanes);

        for (int lane = 0; lane < WaveformCompareLogic.MaxLanes; lane++)
        {
            _laneX[lane].Clear();
            _laneY[lane].Clear();

            Text? label = _laneLabels[lane];
            if (lane >= count)
            {
                if (label != null)
                {
                    label.IsVisible = false;
                }

                continue;
            }

            BeatSegment segment = segments[lane];
            double baseline = lane * WaveformCompareLogic.LaneSpacing;
            FillLane(segment, baseline, _laneX[lane], _laneY[lane]);

            if (label != null)
            {
                label.IsVisible = true;
                label.LabelText = WaveformCompareLogic.LaneLabel(segment);
                label.Location = new Coordinates(LaneLabelXMs, baseline + LaneLabelYOffset);
            }
        }

        UpdateGuides(segments, count);
        _plot.Plot.Axes.SetLimitsY(-0.1, YTop(count));
    }

    /// <summary>
    /// Fills one lane with the segment's envelope, A-aligned (x = ms from the A
    /// onset), normalized to the segment's own peak and max-decimated to the
    /// lane point budget (envelope-preserving, the strip-lane policy).
    /// </summary>
    private static void FillLane(BeatSegment segment, double baseline, List<double> x, List<double> y)
    {
        ReadOnlySpan<float> samples = segment.Samples.Span;
        int stride = Math.Max(1, samples.Length / LanePointBudget);

        float peak = 0f;
        foreach (float value in samples)
        {
            if (value > peak)
            {
                peak = value;
            }
        }

        if (peak <= 0f)
        {
            peak = 1f;
        }

        int points = samples.Length / stride;
        for (int p = 0; p < points; p++)
        {
            float max = samples[p * stride];
            for (int s = 1; s < stride; s++)
            {
                float value = samples[p * stride + s];
                if (value > max)
                {
                    max = value;
                }
            }

            x.Add(p * stride * segment.MsPerPoint - segment.AOffsetMs);
            y.Add(baseline + max / peak);
        }
    }

    private void UpdateGuides(IReadOnlyList<BeatSegment> segments, int count)
    {
        double labelY = YTop(count);
        SetGuide(
            _aGuide, _aGuideLabel,
            count > 0 ? 0.0 : null,
            WaveformCompareLogic.AGuideLabel, labelY);

        double? meanC = WaveformCompareLogic.MeanCPeakOffsetMs(segments);
        SetGuide(
            _cMeanGuide, _cMeanGuideLabel,
            meanC,
            meanC is double mean ? WaveformCompareLogic.CMeanGuideLabel(mean) : "", labelY);
    }

    private static double YTop(int laneCount) =>
        (Math.Max(1, laneCount) - 1) * WaveformCompareLogic.LaneSpacing + YHeadroom;

    /// <summary>Review-cursor contract: a dotted marker at the scrub time's A-relative offset.</summary>
    private bool UpdateReviewCursor(double? reviewCursorTimeS)
    {
        if (_reviewCursor == null)
        {
            return false;
        }

        double? offsetMs = WaveformCompareLogic.CursorOffsetMs(
            reviewCursorTimeS,
            _lastSnapshot?.Segments ?? Array.Empty<BeatSegment>());
        bool visible = offsetMs is not null;
        bool changed = false;

        if (_reviewCursor.IsVisible != visible)
        {
            _reviewCursor.IsVisible = visible;
            changed = true;
        }

        if (offsetMs is double x && Math.Abs(_reviewCursor.Start.X - x) > double.Epsilon)
        {
            _reviewCursor.Line = new CoordinateLine(x, -1e6, x, 1e6);
            changed = true;
        }

        return changed;
    }

    private static VerticalLine AddGuide(Plot plot)
    {
        VerticalLine guide = plot.Add.VerticalLine(0.0);
        guide.LineWidth = 1;
        guide.LinePattern = LinePattern.Dashed;
        guide.IsVisible = false;
        guide.EnableAutoscale = false;
        return guide;
    }

    private Text AddLabel(Plot plot)
    {
        Text label = plot.Add.Text("", 0.0, 0.0);
        label.LabelFontName = _textFontFamily;
        label.LabelFontSize = 11;
        label.Alignment = Alignment.UpperLeft;
        label.IsVisible = false;
        return label;
    }

    private static void SetGuide(VerticalLine? guide, Text? label, double? x, string text, double labelY)
    {
        if (guide == null || label == null)
        {
            return;
        }

        bool visible = x is not null;
        guide.IsVisible = visible;
        label.IsVisible = visible;
        if (x is double position)
        {
            guide.X = position;
            label.LabelText = text;
            label.Location = new Coordinates(position, labelY);
        }
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
        foreach (Scatter? lane in _laneScatters)
        {
            if (lane != null)
            {
                lane.LineColor = Color.FromARGB(_theme.TraceWave);
            }
        }

        foreach (Text? label in _laneLabels)
        {
            if (label != null)
            {
                label.LabelFontColor = Color.FromARGB(_theme.TextPrimary);
            }
        }

        // A = green, C = red: the same event color contract as the scope markers.
        if (_aGuide != null)
        {
            _aGuide.LineColor = Color.FromARGB(Argb.Green);
        }

        if (_aGuideLabel != null)
        {
            _aGuideLabel.LabelFontColor = Color.FromARGB(Argb.Green);
        }

        if (_cMeanGuide != null)
        {
            _cMeanGuide.LineColor = Color.FromARGB(Argb.Red);
        }

        if (_cMeanGuideLabel != null)
        {
            _cMeanGuideLabel.LabelFontColor = Color.FromARGB(Argb.Red);
        }

        if (_reviewCursor != null)
        {
            _reviewCursor.LineColor = Color.FromARGB(_theme.TextPrimary);
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
