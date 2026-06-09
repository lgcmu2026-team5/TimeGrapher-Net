using System;
using System.Globalization;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Services;

/// <summary>
/// Turns per-frame throughput / lag / overrun information into the status-bar text.
/// Tracks the last reported background/foreground rates so it only emits a new
/// throughput line when something actually changed. Extracted from MainWindow so the
/// formatting and change-detection are unit-testable and out of the view.
/// </summary>
internal sealed class AnalysisRunStatusReporter
{
    private double _backgroundFps;
    private double _backgroundSps;
    private double _backgroundSpf;
    private double _foregroundFps;
    private double _foregroundSps;
    private double _foregroundSpf;

    /// <summary>Result of describing a frame.</summary>
    /// <param name="StatusText">New status-bar text, or null to leave it unchanged.</param>
    /// <param name="ConsoleWarning">Diagnostic to write to stderr, or null.</param>
    internal readonly record struct Report(string? StatusText, string? ConsoleWarning);

    public void Reset()
    {
        _backgroundFps = _backgroundSps = _backgroundSpf = 0.0;
        _foregroundFps = _foregroundSps = _foregroundSpf = 0.0;
    }

    public Report Describe(AnalysisFrame frame, ulong droppedFrames, int sampleRate)
    {
        bool statusUpdated = false;
        if (_backgroundFps != frame.BackgroundFps ||
            _backgroundSps != frame.BackgroundSps ||
            _backgroundSpf != frame.BackgroundSpf)
        {
            _backgroundFps = frame.BackgroundFps;
            _backgroundSps = frame.BackgroundSps;
            _backgroundSpf = frame.BackgroundSpf;
            statusUpdated = true;
        }

        if (frame.ForegroundStatsUpdated &&
            (_foregroundFps != frame.ForegroundFps ||
             _foregroundSps != frame.ForegroundSps ||
             _foregroundSpf != frame.ForegroundSpf))
        {
            _foregroundFps = frame.ForegroundFps;
            _foregroundSps = frame.ForegroundSps;
            _foregroundSpf = frame.ForegroundSpf;
            statusUpdated = true;
        }

        string? statusText = statusUpdated ? FormatThroughput() : null;
        string? consoleWarning = null;

        if (frame.InputOverrun)
        {
            statusText = "Audio input overrun: dropped " +
                         frame.InputSamplesDropped.ToString(CultureInfo.InvariantCulture) +
                         " samples before analysis";
        }
        else if (frame.AnalysisLagSamples > (ulong)Math.Max(1, sampleRate / 4))
        {
            double lagMs = frame.AnalysisLagSamples * 1000.0 / Math.Max(1, sampleRate);
            statusText = string.Format(
                CultureInfo.InvariantCulture,
                "Analysis lag: {0:F0} ms ({1} samples), processing {2:F1} ms",
                lagMs,
                frame.AnalysisLagSamples,
                frame.ProcessingElapsedMs);
        }
        else if (frame.DeadlineDegradationLevel > 0)
        {
            // Sticky state from the analysis-side deadline monitor: lag may have
            // subsided below the warning threshold while quality is still reduced.
            statusText = string.Format(
                CultureInfo.InvariantCulture,
                "Deadline pressure: rendering quality reduced (level {0}/{1})",
                frame.DeadlineDegradationLevel,
                AnalysisDeadlineMonitor.MaxLevel);
        }
        else if (droppedFrames != 0)
        {
            consoleWarning = "UI render coalesced " +
                             droppedFrames.ToString(CultureInfo.InvariantCulture) +
                             " analysis frame(s)";
        }

        return new Report(statusText, consoleWarning);
    }

    private string FormatThroughput() => string.Format(
        CultureInfo.InvariantCulture,
        "Backgroud Audio Thread Average - FPS:{0}, SPS:{1}, SPF: {2} Foregroud Audio Handler Average - FPS:{3}, SPS:{4}, SPF: {5}",
        _backgroundFps.ToString("F0", CultureInfo.InvariantCulture),
        _backgroundSps.ToString("F0", CultureInfo.InvariantCulture),
        _backgroundSpf.ToString("F0", CultureInfo.InvariantCulture),
        _foregroundFps.ToString("F0", CultureInfo.InvariantCulture),
        _foregroundSps.ToString("F0", CultureInfo.InvariantCulture),
        _foregroundSpf.ToString("F0", CultureInfo.InvariantCulture));
}
