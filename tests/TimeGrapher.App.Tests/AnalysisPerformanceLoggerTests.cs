using TimeGrapher.App.Services;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class AnalysisPerformanceLoggerTests
{
    [Fact]
    public void ObserveDisplayedWritesRequirementLatencyCsvColumns()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");

        using (var logger = new AnalysisPerformanceLogger(path, ticksPerMs: 1000.0))
        {
            logger.ObserveDisplayed(new AnalysisFrame
            {
                InputSamplesDropped = 96,
                MissedBeats = 3,
                CaptureTimestamp = 1_000,
                ProcessingCompletedTimestamp = 11_000,
            }, displayTicks: 16_000);
        }

        string[] lines = File.ReadAllLines(path);
        File.Delete(path);

        Assert.Equal(
            "capture_to_processing_ms,processing_to_display_ms,end_to_end_latency_ms," +
            "capture_to_processing_avg_ms,capture_to_processing_worst_ms," +
            "processing_to_display_avg_ms,processing_to_display_worst_ms," +
            "end_to_end_avg_ms,end_to_end_worst_ms,dropped_audio_samples,missed_beat_detections",
            lines[0]);
        Assert.Equal(
            "10.000000,5.000000,15.000000,10.000000,10.000000,5.000000,5.000000,15.000000,15.000000,96,3",
            lines[1]);
    }

    [Fact]
    public void ObserveDisplayedReportsCumulativeAverageAndWorstCase()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");

        using (var logger = new AnalysisPerformanceLogger(path, ticksPerMs: 1000.0))
        {
            logger.ObserveDisplayed(new AnalysisFrame
            {
                InputSamplesDropped = 3,
                CaptureTimestamp = 1_000,
                ProcessingCompletedTimestamp = 3_000,
            }, displayTicks: 6_000);
            logger.ObserveDisplayed(new AnalysisFrame
            {
                InputSamplesDropped = 4,
                MissedBeats = 2,
                CaptureTimestamp = 10_000,
                ProcessingCompletedTimestamp = 18_000,
            }, displayTicks: 30_000);
        }

        string[] lines = File.ReadAllLines(path);
        File.Delete(path);

        Assert.Equal(
            "8.000000,12.000000,20.000000,5.000000,8.000000,7.500000,12.000000,12.500000,20.000000,7,2",
            lines[2]);
    }
}
