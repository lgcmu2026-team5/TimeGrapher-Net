using Avalonia.Controls;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Port of GraphFrameRenderer.cpp: drives the scope plot, rate plot, sound image and results
/// label from a single <see cref="AnalysisFrame"/>. QCustomPlot is replaced by ScottPlot 5.
///
/// Scope series arrive at 48 kHz and accumulate, so instead of mutating a live plottable each
/// frame this keeps its own append/replace List&lt;double&gt; accumulators (mirroring QCPGraph
/// data()), purges old samples by x-key range, and rebuilds the Scatter plottable from the
/// current accumulator on every refresh. Markers (vertical/horizontal/text) are kept as
/// LinePlot/Text plottables tracked alongside their x-coordinates so they can be purged by
/// the same range as the scope data. UI thread only; no locking required.
/// </summary>
public sealed class GraphFrameRenderer
{
    // ---- constants from the anonymous namespace in GraphFrameRenderer.cpp ----
    private const int GraphHistoryInSeconds = 10;

    // setRenderSource() recorded analysisFrameSessionId / analysisFrameSourceId /
    // analysisFrameSourceSampleEnd as dynamic QObject properties for diagnostics. Avalonia has
    // no equivalent dynamic-property bag used by anything downstream, so it is omitted here.

    private struct ScopeGraphSpec
    {
        public string Id;
        public string Name;
        public uint Color;     // ARGB32 (Qt::GlobalColor equivalent)
        public int FillAlpha;
    }

    private struct RateGraphSpec
    {
        public string Id;
        public string Name;
        public uint Color;     // ARGB32
    }

    // ScopeGraphs[]: {scope.pcm "Rectified" blue, fill 20}, {scope.threshold "Trigger" red, fill 0}
    private static readonly ScopeGraphSpec[] ScopeGraphs =
    {
        new ScopeGraphSpec { Id = AnalysisGraphSeries.ScopePcm, Name = "Rectified", Color = Argb.Blue, FillAlpha = 20 },
        new ScopeGraphSpec { Id = AnalysisGraphSeries.ScopeThreshold, Name = "Trigger", Color = Argb.Red, FillAlpha = 0 },
    };

    // RateGraphs[]: {rate.tic "Tic Rate" red}, {rate.toc "Toc Rate" blue}
    private static readonly RateGraphSpec[] RateGraphs =
    {
        new RateGraphSpec { Id = AnalysisGraphSeries.RateTic, Name = "Tic Rate", Color = Argb.Red },
        new RateGraphSpec { Id = AnalysisGraphSeries.RateToc, Name = "Toc Rate", Color = Argb.Blue },
    };

    private static readonly int ScopeGraphCount = ScopeGraphs.Length;
    private static readonly int RateGraphCount = RateGraphs.Length;

    // ---- ctor-provided display targets (mScopePlot/mRatePlot/mSoundImage/mResults/mTextFontFamily) ----
    private readonly AvaPlot _scopePlot;
    private readonly AvaPlot _ratePlot;
    private readonly Avalonia.Controls.Image _soundImage;
    private readonly TextBlock _results;
    private readonly string _textFontFamily;

    // Per-graph accumulators replacing QCPGraph::data(). Index matches ScopeGraphs/RateGraphs.
    private readonly List<double>[] _scopeX;
    private readonly List<double>[] _scopeY;
    private readonly Scatter?[] _scopePlottables;

    private readonly List<double>[] _rateX;
    private readonly List<double>[] _rateY;
    private readonly Scatter?[] _ratePlottables;

    // Markers/text tracked with their x-coordinates so purgeHistory can drop them by range,
    // mirroring removeMarkersAndText() iterating QCustomPlot::item(i).
    private sealed class TrackedLine
    {
        public LinePlot Plot = null!;
        public double StartX;
        public double EndX;
    }

    private sealed class TrackedText
    {
        public Text Plot = null!;
        public double X;
    }

    private readonly List<TrackedLine> _scopeLines = new();
    private readonly List<TrackedText> _scopeTexts = new();

    public GraphFrameRenderer(AvaPlot scopePlot,
                              AvaPlot ratePlot,
                              Avalonia.Controls.Image soundImageControl,
                              TextBlock resultsText,
                              string textFontFamily)
    {
        _scopePlot = scopePlot;
        _ratePlot = ratePlot;
        _soundImage = soundImageControl;
        _results = resultsText;
        _textFontFamily = textFontFamily;

        _scopeX = new List<double>[ScopeGraphCount];
        _scopeY = new List<double>[ScopeGraphCount];
        _scopePlottables = new Scatter?[ScopeGraphCount];
        for (int i = 0; i < ScopeGraphCount; i++)
        {
            _scopeX[i] = new List<double>();
            _scopeY[i] = new List<double>();
        }

        _rateX = new List<double>[RateGraphCount];
        _rateY = new List<double>[RateGraphCount];
        _ratePlottables = new Scatter?[RateGraphCount];
        for (int i = 0; i < RateGraphCount; i++)
        {
            _rateX[i] = new List<double>();
            _rateY[i] = new List<double>();
        }
    }

    /// <summary>Port of createGraphs(): configure both plots and add the scope/rate series.</summary>
    public void CreateGraphs(double rateErrorYScale, int rateDataPoints)
    {
        // ---- scope plot ----
        Plot scope = _scopePlot.Plot;
        // QCP::iRangeDrag | QCP::iRangeZoom are interactive defaults already enabled in AvaPlot.
        scope.YLabel("Amplitude");
        scope.XLabel("Time");
        scope.Axes.SetLimitsY(0, 0.1);
        // xAxis->setTickLabels(false): hide x tick labels (legend toggled on at the end).
        HideXTickLabels(scope);

        scope.Clear(); // clearGraphs()
        for (int i = 0; i < ScopeGraphCount; i++)
        {
            _scopeX[i].Clear();
            _scopeY[i].Clear();
            _scopePlottables[i] = null;
        }
        // addGraph() for each scope spec: create the (initially empty) plottables now so the
        // legend entries exist from the start. RenderFrame later removes+recreates them from
        // the accumulators (= per-frame setData/addData on the QCPGraph).
        RebuildScopePlottables();

        scope.ShowLegend();

        // ---- rate plot ----
        Plot rate = _ratePlot.Plot;
        rate.YLabel("Rate Error (milliseconds)");
        rate.XLabel("Time");
        rate.Axes.SetLimitsY(-rateErrorYScale, rateErrorYScale);
        rate.Axes.SetLimitsX(0, rateDataPoints);
        HideXTickLabels(rate);

        rate.Clear(); // clearGraphs()
        for (int i = 0; i < RateGraphCount; i++)
        {
            _rateX[i].Clear();
            _rateY[i].Clear();
            _ratePlottables[i] = null;
        }
        RebuildRatePlottables();
        rate.ShowLegend();

        _scopePlot.Refresh();
        _ratePlot.Refresh();
    }

    /// <summary>Port of reset(): clear all series/markers and restore initial ranges + text.</summary>
    public void Reset(int sampleRate, double rateErrorYScale, int rateDataPoints)
    {
        // setRenderSource(...) calls omitted (no dynamic-property bag in Avalonia).

        // Original: graph(i)->data()->clear() (clears data, KEEPS the graphs) + clearItems()
        // (removes markers/text). We mirror that by clearing the accumulators, dropping the
        // tracked markers, and rebuilding the (now empty) graph plottables so legend stays.
        Plot scope = _scopePlot.Plot;
        scope.Clear();                 // remove all plottables (graphs + items)
        for (int i = 0; i < ScopeGraphCount; i++)
        {
            _scopeX[i].Clear();
            _scopeY[i].Clear();
            _scopePlottables[i] = null;
        }
        _scopeLines.Clear();           // clearItems(): markers/text removed
        _scopeTexts.Clear();
        RebuildScopePlottables();      // re-add the empty graphs (kept across reset)
        _scopePlot.Refresh();          // replot()

        Plot rate = _ratePlot.Plot;
        rate.Axes.SetLimitsY(-rateErrorYScale, rateErrorYScale);
        rate.Axes.SetLimitsX(0, rateDataPoints);
        rate.Clear();
        for (int i = 0; i < RateGraphCount; i++)
        {
            _rateX[i].Clear();
            _rateY[i].Clear();
            _ratePlottables[i] = null;
        }
        RebuildRatePlottables();       // re-add the empty graphs (kept across reset)
        _ratePlot.Refresh();           // replot()

        _results.Text = "RATE ------ s/d   AMPLITUDE ---   BEAT ERROR ---- ms   BEAT ----- bph";

        // Q_UNUSED(sample_rate); the blank white sound image is recreated and drawn.
        _ = sampleRate;
        int w = (int)_soundImage.Bounds.Width;
        int h = (int)_soundImage.Bounds.Height;
        if (w > 0 && h > 0)
        {
            var blank = new PixelBuffer(w, h);
            blank.Fill(Argb.White);
            PixelBufferBitmap.UpdateImage(_soundImage, blank);
        }
    }

    /// <summary>Port of renderFrame().</summary>
    public void RenderFrame(AnalysisFrame frame, int sampleRate, int scopeScale)
    {
        bool scopePlotUpdated = false;
        for (int i = 0; i < ScopeGraphCount; i++)
        {
            GraphSeriesFrame? series = FindSeries(frame.ScopeSeries, ScopeGraphs[i].Id);
            if (series != null)
            {
                if (series.Replace)
                {
                    _scopeX[i].Clear();
                    _scopeY[i].Clear();
                    _scopeX[i].AddRange(series.X);
                    _scopeY[i].AddRange(series.Y);
                }
                else
                {
                    _scopeX[i].AddRange(series.X);
                    _scopeY[i].AddRange(series.Y);
                }
                scopePlotUpdated = true;
            }
        }

        foreach (ScopeVerticalMarker marker in frame.VerticalMarkers)
        {
            AddVerticalMarker(marker.X, marker.Height, marker.Color);
            scopePlotUpdated = true;
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
            scopePlotUpdated = true;
        }

        foreach (ScopeTextMarker marker in frame.TextMarkers)
        {
            AddText(marker.X, marker.Height, marker.Text, marker.Color, marker.Alignment);
            scopePlotUpdated = true;
        }

        bool ratePlotUpdated = false;
        for (int i = 0; i < RateGraphCount; i++)
        {
            GraphSeriesFrame? series = FindSeries(frame.RateSeries, RateGraphs[i].Id);
            if (series != null)
            {
                if (series.Replace)
                {
                    _rateX[i].Clear();
                    _rateY[i].Clear();
                    _rateX[i].AddRange(series.X);
                    _rateY[i].AddRange(series.Y);
                }
                else
                {
                    _rateX[i].AddRange(series.X);
                    _rateY[i].AddRange(series.Y);
                }
                ratePlotUpdated = true;
            }
        }
        if (ratePlotUpdated)
        {
            RebuildRatePlottables();
            _ratePlot.Refresh(); // replot(rpQueuedReplot)
            // setRenderSource(mRatePlot, frame) omitted.
        }
        if (frame.MetricsUpdate.ResultsUpdated)
        {
            _results.Text = frame.MetricsUpdate.ResultsText;
            // setRenderSource(mResults, frame) omitted.
        }

        if (scopePlotUpdated)
        {
            PurgeHistory(sampleRate);
            RebuildScopePlottables();
            // xAxis->setRange(graph_tick_end, sampleRate/scope_scale, Qt::AlignRight):
            // right-aligned window of width (sampleRate / scope_scale) ending at graph_tick_end.
            double end = frame.GraphTickEnd;
            double width = (double)sampleRate / scopeScale;
            _scopePlot.Plot.Axes.SetLimitsX(end - width, end);
            _scopePlot.Plot.Axes.AutoScaleY();   // yAxis->rescale()
            _scopePlot.Refresh();                 // replot(rpQueuedReplot)
            // setRenderSource(mScopePlot, frame) omitted.
        }

        if (frame.SoundImageUpdated && frame.SoundImage != null)
        {
            PixelBufferBitmap.UpdateImage(_soundImage, frame.SoundImage);
            // setRenderSource(mSoundImage, frame) omitted.
        }
    }

    // ---- helpers ----

    private static GraphSeriesFrame? FindSeries(List<GraphSeriesFrame> seriesList, string id)
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
        // QCustomPlot xAxis->setTickLabels(false): blank x-axis tick labels.
        plot.Axes.Bottom.TickLabelStyle.IsVisible = false;
    }

    // Rebuild scope Scatter plottables from accumulators. Equivalent to QCPGraph holding the
    // accumulated data; recreated each refresh so ScottPlot renders the current List contents.
    private void RebuildScopePlottables()
    {
        Plot scope = _scopePlot.Plot;
        for (int i = 0; i < ScopeGraphCount; i++)
        {
            ScopeGraphSpec spec = ScopeGraphs[i];
            if (_scopePlottables[i] != null)
            {
                scope.Remove(_scopePlottables[i]!);
                _scopePlottables[i] = null;
            }

            // Add even when empty so the legend entry persists (createGraphs added all graphs).
            Scatter sc = scope.Add.Scatter(_scopeX[i], _scopeY[i]);
            sc.LineWidth = 1;                          // pen.setWidth(1)
            sc.LineColor = Color.FromARGB(spec.Color); // pen.setColor(spec.color)
            sc.MarkerStyle.IsVisible = false;          // QCPGraph default: line only, no markers
            if (spec.FillAlpha > 0)
            {
                // graph->setBrush: fill under the curve down to y=0 with color@alpha.
                sc.FillY = true;
                sc.FillYColor = Color.FromARGB(spec.Color).WithAlpha((byte)spec.FillAlpha);
            }
            sc.LegendText = spec.Name; // graph->setName(spec.name)
            _scopePlottables[i] = sc;
        }
    }

    // Rebuild rate Scatter plottables: disc markers size 3, no connecting line (lsNone).
    private void RebuildRatePlottables()
    {
        Plot rate = _ratePlot.Plot;
        for (int i = 0; i < RateGraphCount; i++)
        {
            RateGraphSpec spec = RateGraphs[i];
            if (_ratePlottables[i] != null)
            {
                rate.Remove(_ratePlottables[i]!);
                _ratePlottables[i] = null;
            }

            Scatter sc = rate.Add.Scatter(_rateX[i], _rateY[i]);
            // setScatterStyle(ssDisc, 3) + setLineStyle(lsNone):
            sc.LineWidth = 0;
            sc.MarkerShape = MarkerShape.FilledCircle;
            sc.MarkerSize = 3;
            sc.MarkerColor = Color.FromARGB(spec.Color); // setPen(QPen(color))
            sc.LegendText = spec.Name;                   // setName(spec.name)
            _ratePlottables[i] = sc;
        }
    }

    // purgeHistory(sample_rate): mirror the QCustomPlot logic for each scope graph.
    private void PurgeHistory(int sampleRate)
    {
        for (int i = 0; i < ScopeGraphCount; i++)
        {
            if (_scopeX[i].Count > (GraphHistoryInSeconds * sampleRate))
            {
                // getKeyRange (key = x). Data arrives in ascending x order.
                double minKey = _scopeX[i][0];
                double maxKey = _scopeX[i][_scopeX[i].Count - 1];
                double numKeys = maxKey - minKey;
                // C++: num_keys - ((GraphHistoryInSeconds * sample_rate) / 2) where the inner
                // product/divide is INTEGER arithmetic (int / int truncates) before the subtract.
                double numToRemove = numKeys - ((GraphHistoryInSeconds * sampleRate) / 2);
                double removeStart = minKey;
                double removeEnd = minKey + numToRemove;
                RemoveMarkersAndText(removeStart, removeEnd);
                RemoveScopeDataRange(i, removeStart, removeEnd);
            }
        }
    }

    // QCPDataContainer::remove(begin, end): remove points whose key is in [removeStart, removeEnd].
    private void RemoveScopeDataRange(int graphIndex, double removeStart, double removeEnd)
    {
        List<double> xs = _scopeX[graphIndex];
        List<double> ys = _scopeY[graphIndex];
        // Data is ascending; drop the contiguous prefix inside the range.
        int n = 0;
        while (n < xs.Count && xs[n] >= removeStart && xs[n] <= removeEnd)
        {
            n++;
        }
        if (n > 0)
        {
            xs.RemoveRange(0, n);
            ys.RemoveRange(0, n);
        }
    }

    private void AddVerticalMarker(double x, double height, uint color)
    {
        // QCPItemLine from (x,0) to (x,height); 2px dashed.
        LinePlot line = _scopePlot.Plot.Add.Line(x, 0.0, x, height);
        line.LineColor = Color.FromARGB(color);
        line.LineWidth = 2;
        line.LinePattern = LinePattern.Dashed;
        line.MarkerStyle.IsVisible = false;
        _scopeLines.Add(new TrackedLine { Plot = line, StartX = x, EndX = x });
    }

    private void AddText(double x, double height, string text, uint color, MarkerTextAlignment alignment)
    {
        // QCPItemText at plot coords (x,height), font (family,10), colored.
        Text label = _scopePlot.Plot.Add.Text(text, x, height);
        label.Color = Color.FromARGB(color);
        label.FontName = _textFontFamily;
        label.FontSize = 10;
        label.Alignment = MapAlignment(alignment);
        _scopeTexts.Add(new TrackedText { Plot = label, X = x });
    }

    // Qt::Alignment -> ScottPlot anchor. The position is the anchor point; AlignTop means the
    // text hangs below the point, AlignHCenter/AlignLeft set the horizontal anchor.
    private static Alignment MapAlignment(MarkerTextAlignment alignment) => alignment switch
    {
        // Qt::AlignHCenter | Qt::AlignTop -> anchor at upper-center of the text box.
        MarkerTextAlignment.CenterTop => Alignment.UpperCenter,
        // Qt::AlignLeft | Qt::AlignTop -> anchor at upper-left.
        MarkerTextAlignment.LeftTop => Alignment.UpperLeft,
        _ => Alignment.UpperLeft,
    };

    private void AddHorizontalMarkerInward(double xLeft, double xRight, double length, double height, uint color)
    {
        Color c = Color.FromARGB(color);

        // marker_left: from (x_left - length, height) to (x_left, height) with arrow head.
        LinePlot left = _scopePlot.Plot.Add.Line(xLeft - length, height, xLeft, height);
        left.LineColor = c;
        left.LineWidth = 1;
        left.LinePattern = LinePattern.Solid;
        left.MarkerStyle.IsVisible = false;
        _scopeLines.Add(new TrackedLine { Plot = left, StartX = xLeft - length, EndX = xLeft });

        // marker_right: from (x_right, height) to (x_right + length, height) with arrow tail.
        LinePlot right = _scopePlot.Plot.Add.Line(xRight, height, xRight + length, height);
        right.LineColor = c;
        right.LineWidth = 1;
        right.LinePattern = LinePattern.Solid;
        right.MarkerStyle.IsVisible = false;
        _scopeLines.Add(new TrackedLine { Plot = right, StartX = xRight, EndX = xRight + length });
    }

    private void AddHorizontalMarkerOutward(double xLeft, double xRight, double height, uint color)
    {
        // single QCPItemLine from (x_left,height) to (x_right,height); arrows on both ends.
        LinePlot line = _scopePlot.Plot.Add.Line(xLeft, height, xRight, height);
        line.LineColor = Color.FromARGB(color);
        line.LineWidth = 1;
        line.LinePattern = LinePattern.Solid;
        line.MarkerStyle.IsVisible = false;
        _scopeLines.Add(new TrackedLine { Plot = line, StartX = xLeft, EndX = xRight });
    }

    // removeMarkersAndText(range_min, range_max): drop any line whose start or end x is in range,
    // and any text whose x is in range. Iterate back-to-front like the original.
    private void RemoveMarkersAndText(double rangeMin, double rangeMax)
    {
        Plot scope = _scopePlot.Plot;
        for (int i = _scopeLines.Count - 1; i >= 0; --i)
        {
            TrackedLine tl = _scopeLines[i];
            double startKey = tl.StartX;
            double endKey = tl.EndX;
            if ((startKey >= rangeMin && startKey <= rangeMax) ||
                (endKey >= rangeMin && endKey <= rangeMax))
            {
                scope.Remove(tl.Plot);
                _scopeLines.RemoveAt(i);
            }
        }
        for (int i = _scopeTexts.Count - 1; i >= 0; --i)
        {
            TrackedText tt = _scopeTexts[i];
            double key = tt.X;
            if (key >= rangeMin && key <= rangeMax)
            {
                scope.Remove(tt.Plot);
                _scopeTexts.RemoveAt(i);
            }
        }
    }
}
