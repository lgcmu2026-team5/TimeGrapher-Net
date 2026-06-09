using TimeGrapher.App.Services;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class AnalysisRunStatusReporterTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void FirstThroughputChangeProducesStatusText()
    {
        var reporter = new AnalysisRunStatusReporter();
        var frame = new AnalysisFrame { BackgroundFps = 60, BackgroundSps = 48000, BackgroundSpf = 800 };

        AnalysisRunStatusReporter.Report report = reporter.Describe(frame, droppedFrames: 0, SampleRate);

        Assert.NotNull(report.StatusText);
        Assert.Contains("FPS:60", report.StatusText);
        Assert.Null(report.ConsoleWarning);
    }

    [Fact]
    public void UnchangedThroughputProducesNoStatusText()
    {
        var reporter = new AnalysisRunStatusReporter();
        var frame = new AnalysisFrame { BackgroundFps = 60, BackgroundSps = 48000, BackgroundSpf = 800 };

        reporter.Describe(frame, 0, SampleRate);
        AnalysisRunStatusReporter.Report second = reporter.Describe(frame, 0, SampleRate);

        Assert.Null(second.StatusText);
    }

    [Fact]
    public void InputOverrunOverridesWithOverrunMessage()
    {
        var reporter = new AnalysisRunStatusReporter();
        var frame = new AnalysisFrame { InputOverrun = true, InputSamplesDropped = 1234 };

        AnalysisRunStatusReporter.Report report = reporter.Describe(frame, 0, SampleRate);

        Assert.Contains("overrun", report.StatusText);
        Assert.Contains("1234", report.StatusText);
    }

    [Fact]
    public void LargeAnalysisLagReportsLag()
    {
        var reporter = new AnalysisRunStatusReporter();
        var frame = new AnalysisFrame { AnalysisLagSamples = (ulong)SampleRate, ProcessingElapsedMs = 5.0 };

        AnalysisRunStatusReporter.Report report = reporter.Describe(frame, 0, SampleRate);

        Assert.Contains("Analysis lag", report.StatusText);
    }

    [Fact]
    public void DeadlineDegradationLevelShowsQualityReducedStatus()
    {
        var reporter = new AnalysisRunStatusReporter();
        // Lag already back under the warning threshold, but the monitor still
        // holds a reduced-quality level -> the sticky state must stay visible.
        var frame = new AnalysisFrame { DeadlineDegradationLevel = 2 };

        AnalysisRunStatusReporter.Report report = reporter.Describe(frame, 0, SampleRate);

        Assert.Contains("quality reduced", report.StatusText);
        Assert.Contains("2/3", report.StatusText);
    }

    [Fact]
    public void DroppedFramesWithoutChangeWarnsToConsoleOnly()
    {
        var reporter = new AnalysisRunStatusReporter();
        // No throughput change, no overrun, no lag -> only the coalesced-frames warning.
        AnalysisRunStatusReporter.Report report = reporter.Describe(new AnalysisFrame(), droppedFrames: 3, SampleRate);

        Assert.Null(report.StatusText);
        Assert.Contains("coalesced", report.ConsoleWarning);
        Assert.Contains("3", report.ConsoleWarning);
    }

    [Fact]
    public void ResetClearsRememberedThroughput()
    {
        var reporter = new AnalysisRunStatusReporter();
        var frame = new AnalysisFrame { BackgroundFps = 60, BackgroundSps = 48000, BackgroundSpf = 800 };

        reporter.Describe(frame, 0, SampleRate);
        reporter.Reset();
        AnalysisRunStatusReporter.Report afterReset = reporter.Describe(frame, 0, SampleRate);

        // After reset the same stats look new again -> a status line is re-emitted.
        Assert.NotNull(afterReset.StatusText);
    }
}
