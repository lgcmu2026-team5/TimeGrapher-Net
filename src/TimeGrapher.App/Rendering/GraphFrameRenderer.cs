using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Metrics;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Small UI-thread facade for shared analysis UI state. Tab-specific rendering lives
/// in RateScopeRenderer and SoundPrintRenderer.
/// </summary>
internal sealed class GraphFrameRenderer
{
    /// <summary>
    /// Placeholder shown before any metrics arrive. Field widths match WatchMetrics.FormatResults
    /// (fixed-width) so the readout never shifts when real values replace the dashes.
    /// </summary>
    public const string PlaceholderResults =
        "RATE ----- s/d | AMPLITUDE ---° | BEAT ERROR ---- ms | BEAT ----- bph";

    private readonly IReadOnlyList<IAnalysisFrameConsumer> _consumers;
    private readonly TextBlock _results;
    private string? _lastResultsText;

    public GraphFrameRenderer(
        IEnumerable<IAnalysisFrameConsumer> consumers,
        TextBlock resultsText)
    {
        _consumers = consumers.ToArray();
        _results = resultsText;
    }

    public void Initialize(AnalysisTabResetContext context)
    {
        foreach (IAnalysisFrameConsumer consumer in _consumers)
        {
            consumer.Initialize(context);
        }
    }

    public void Reset(AnalysisTabResetContext context)
    {
        foreach (IAnalysisFrameConsumer consumer in _consumers)
        {
            consumer.Reset(context);
        }
        SetResults(PlaceholderResults);
    }

    /// <summary>
    /// Renders <paramref name="text"/> into the results readout, coloring the spans wrapped in
    /// WatchMetrics value markers ('{' … '}') with the accent brush and stripping the markers.
    /// Label text and dash placeholders keep the default foreground. Rebuilds the inline runs
    /// only when the text actually changes, so repeated identical updates cost nothing.
    /// </summary>
    public void SetResults(string text)
    {
        if (text == _lastResultsText)
        {
            return;
        }

        _lastResultsText = text;
        RenderInto(_results, text);
    }

    private static void RenderInto(TextBlock target, string text)
    {
        InlineCollection inlines = target.Inlines ??= new InlineCollection();
        inlines.Clear();

        int segmentStart = 0;
        bool accent = false;
        for (int i = 0; i <= text.Length; i++)
        {
            bool boundary = i == text.Length
                || text[i] == WatchMetrics.ValueSpanStart
                || text[i] == WatchMetrics.ValueSpanEnd;
            if (!boundary)
            {
                continue;
            }

            if (i > segmentStart)
            {
                var run = new Run(text.Substring(segmentStart, i - segmentStart));
                if (accent)
                {
                    run.Bind(TextElement.ForegroundProperty, run.GetResourceObservable("ChromeAccentBrush"));
                }

                inlines.Add(run);
            }

            if (i < text.Length)
            {
                accent = text[i] == WatchMetrics.ValueSpanStart;
            }

            segmentStart = i + 1;
        }
    }

    public void ApplyTheme(PlotThemePalette theme)
    {
        foreach (RateScopeFrameConsumer consumer in _consumers.OfType<RateScopeFrameConsumer>())
        {
            consumer.ApplyTheme(theme);
        }
    }

    public void UpdateResults(AnalysisFrame frame)
    {
        if (frame.MetricsUpdate.ResultsUpdated)
        {
            SetResults(frame.MetricsUpdate.ResultsText);
        }
    }
}
