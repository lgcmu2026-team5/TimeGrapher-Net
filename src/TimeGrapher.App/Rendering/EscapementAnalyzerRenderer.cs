using Avalonia.Controls;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Escapement Analyzer and Marker-Line Display, rendered from the cumulative
/// BeatSegmentsSnapshot the frame carries (the Beat-Noise Scope's segment
/// infrastructure reused for fine-grained intra-beat timing).
///
/// One large plot shows the latest beat's envelope with pooled vertical timing
/// markers and millisecond labels for the escapement-cycle events: A (green
/// dashed, the cycle's zero reference), C peak (red dashed) and C onset (red
/// dotted, only when the detector located the cluster's rising edge). The
/// numeric panel below reads the current A→C interval per reference, the
/// onset-vs-peak delta, and — via EscapementTimingTracker fed here on each
/// snapshot-version change — the windowed mean±sigma of both references plus
/// which reference is more repeatable.
///
/// All plottables refill in place; re-renders only when the snapshot version
/// changes, so coalesced or repeated frames cost nothing. Segments reference
/// pooled Core buffers that stay valid only until rotated out, so every render
/// re-reads from the latest snapshot and nothing UI-side caches sample data
/// beyond it.
/// </summary>
internal sealed class EscapementAnalyzerRenderer
{
    private const double YHeadroom = 1.1;
    /// <summary>Top label row (A and C peak), as a fraction of the envelope max.</summary>
    private const double TopLabelFraction = 1.06;
    /// <summary>Second label row (C onset, which sits close to C peak), kept below the top row.</summary>
    private const double SecondLabelFraction = 0.97;

    private readonly AvaPlot _plot;
    private readonly TextBlock[] _valueTexts;
    private readonly string _textFontFamily;
    private readonly EscapementTimingTracker _tracker = new();

    private readonly List<double> _envelopeX = new();
    private readonly List<double> _envelopeY = new();

    private Scatter? _envelopeScatter;
    private VerticalLine? _aMarker;
    private VerticalLine? _cPeakMarker;
    private VerticalLine? _cOnsetMarker;
    private Text? _aLabel;
    private Text? _cPeakLabel;
    private Text? _cOnsetLabel;

    private PlotThemePalette _theme = PlotThemePalette.Current;
    private ulong _lastVersion;
    private ulong _lastObservedVersion;

    public EscapementAnalyzerRenderer(AvaPlot plot, TextBlock[] valueTexts, string textFontFamily)
    {
        _plot = plot;
        _valueTexts = valueTexts;
        _textFontFamily = textFontFamily;
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
        _lastObservedVersion = 0;
        _tracker.Reset();
        foreach (TextBlock value in _valueTexts)
        {
            value.Text = VarioReadout.Missing;
        }

        Plot plot = _plot.Plot;
        plot.Clear();
        _envelopeX.Clear();
        _envelopeY.Clear();
        ApplyPlotTheme(plot);
        plot.YLabel("Envelope");
        plot.XLabel("ms");
        _envelopeScatter = plot.Add.Scatter(_envelopeX, _envelopeY);
        _envelopeScatter.LineWidth = 1;
        _envelopeScatter.MarkerStyle.IsVisible = false;
        _aMarker = AddMarker(plot, LinePattern.Dashed);
        _cPeakMarker = AddMarker(plot, LinePattern.Dashed);
        _cOnsetMarker = AddMarker(plot, LinePattern.Dotted);
        _aLabel = AddLabel(plot);
        _cPeakLabel = AddLabel(plot);
        _cOnsetLabel = AddLabel(plot);
        plot.Axes.SetLimitsX(0, BeatSegmentCapture.WindowMs);

        ApplySeriesTheme();
        _plot.Refresh();
    }

    public void Reset()
    {
        CreateGraphs();
    }

    /// <summary>
    /// Feeds the repeatability tracker from every routed frame (the consumer's
    /// ObserveFrame path), so the advertised last-32-beats window keeps
    /// accumulating while another tab is active. Version-gated and O(ring) per
    /// new snapshot (~2/s), so the observe path stays trivial.
    /// </summary>
    public void ObserveSegments(AnalysisFrame frame)
    {
        BeatSegmentsSnapshot? snapshot = frame.BeatSegments;
        if (snapshot == null || snapshot.Version == _lastObservedVersion)
        {
            return;
        }

        _lastObservedVersion = snapshot.Version;
        _tracker.Accumulate(snapshot);
    }

    public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
    {
        // Review cursor deliberately not rendered here: this is a single-beat
        // (latest segment) inspection view whose x-domain is milliseconds
        // within that beat's window, not stream time, so
        // context.ReviewCursorTimeS has no meaningful x mapping on this plot.
        _ = context;

        BeatSegmentsSnapshot? snapshot = frame.BeatSegments;
        if (snapshot == null || snapshot.Version == _lastVersion)
        {
            return;
        }

        _lastVersion = snapshot.Version;
        // Catch-up for re-routed frames (tab switch / scrub) whose snapshot the
        // observe path already consumed - Accumulate is watermark-idempotent.
        ObserveSegments(frame);

        BeatSegment? latest = snapshot.Segments.Count > 0 ? snapshot.Segments[^1] : null;
        double envelopeMax = RenderEnvelope(latest);
        UpdateMarkers(latest, envelopeMax);
        UpdateReadout(latest);
        _plot.Refresh();
    }

    /// <summary>Refills the envelope series and rescales the axes; returns the envelope max.</summary>
    private double RenderEnvelope(BeatSegment? segment)
    {
        _envelopeX.Clear();
        _envelopeY.Clear();
        if (segment == null)
        {
            return 1.0;
        }

        ReadOnlySpan<float> samples = segment.Samples.Span;
        double max = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            double y = samples[i];
            _envelopeX.Add(i * segment.MsPerPoint);
            _envelopeY.Add(y);
            if (y > max)
            {
                max = y;
            }
        }

        if (max <= 0.0)
        {
            max = 1.0;
        }

        _plot.Plot.Axes.SetLimitsX(0, segment.MsPerPoint * samples.Length);
        _plot.Plot.Axes.SetLimitsY(-0.02 * max, YHeadroom * max);
        return max;
    }

    private void UpdateMarkers(BeatSegment? segment, double envelopeMax)
    {
        if (segment == null)
        {
            SetMarker(_aMarker, _aLabel, null, "");
            SetMarker(_cPeakMarker, _cPeakLabel, null, "");
            SetMarker(_cOnsetMarker, _cOnsetLabel, null, "");
            return;
        }

        double topLabelY = TopLabelFraction * envelopeMax;
        double secondLabelY = SecondLabelFraction * envelopeMax;

        SetMarker(_aMarker, _aLabel, segment.AOffsetMs, EscapementReadout.AMarkerLabel, topLabelY);
        SetMarker(
            _cPeakMarker, _cPeakLabel,
            segment.CPeakValid ? segment.CPeakOffsetMs : null,
            EscapementReadout.CPeakMarkerLabel(segment.CPeakOffsetMs - segment.AOffsetMs),
            topLabelY);
        SetMarker(
            _cOnsetMarker, _cOnsetLabel,
            segment.COnsetValid ? segment.COnsetOffsetMs : null,
            EscapementReadout.COnsetMarkerLabel(segment.COnsetOffsetMs - segment.AOffsetMs),
            secondLabelY);
    }

    private void UpdateReadout(BeatSegment? latest)
    {
        string[] values = EscapementReadout.Values(latest, _tracker);
        for (int i = 0; i < _valueTexts.Length && i < values.Length; i++)
        {
            _valueTexts[i].Text = values[i];
        }
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

    private Text AddLabel(Plot plot)
    {
        Text label = plot.Add.Text("", 0.0, 0.0);
        label.LabelFontName = _textFontFamily;
        label.LabelFontSize = 11;
        label.Alignment = Alignment.UpperLeft;
        label.IsVisible = false;
        return label;
    }

    private static void SetMarker(
        VerticalLine? marker, Text? label, double? x, string text, double labelY = 0.0)
    {
        if (marker == null || label == null)
        {
            return;
        }

        bool visible = x is not null;
        marker.IsVisible = visible;
        label.IsVisible = visible;
        if (x is double position)
        {
            marker.X = position;
            label.LabelText = text;
            label.Location = new Coordinates(position, labelY);
        }
    }

    private void ApplySeriesTheme()
    {
        if (_envelopeScatter != null)
        {
            _envelopeScatter.LineColor = Color.FromARGB(_theme.TraceWave);
        }

        // A = green, C = red: the same event color contract as the scope markers.
        if (_aMarker != null)
        {
            _aMarker.LineColor = Color.FromARGB(Argb.Green);
        }

        if (_aLabel != null)
        {
            _aLabel.LabelFontColor = Color.FromARGB(Argb.Green);
        }

        foreach (VerticalLine? marker in new[] { _cPeakMarker, _cOnsetMarker })
        {
            if (marker != null)
            {
                marker.LineColor = Color.FromARGB(Argb.Red);
            }
        }

        foreach (Text? label in new[] { _cPeakLabel, _cOnsetLabel })
        {
            if (label != null)
            {
                label.LabelFontColor = Color.FromARGB(Argb.Red);
            }
        }
    }

    private void ApplyPlotTheme(Plot plot)
    {
        PlotThemeHelper.Apply(plot, _theme);
    }
}
