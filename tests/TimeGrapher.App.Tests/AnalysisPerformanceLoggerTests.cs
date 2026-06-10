using TimeGrapher.App.Services;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class AnalysisPerformanceLoggerTests
{
    [Fact]
    public void ObserveWritesProcessingBudgetAndLagCsvColumns()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");

        using (var logger = new AnalysisPerformanceLogger(path))
        {
            logger.Observe(new AnalysisFrame
            {
                SessionId = 7,
                SourceId = 9,
                SourceSampleEnd = 4096,
                SampleRate = 48000,
                PendingSamples = 4096,
                AnalysisLagSamples = 480,
                ProcessingElapsedMs = 12.345678,
                DeadlineDegradationLevel = 2,
                InputOverrun = true,
                InputSamplesDropped = 96,
                MissedBeats = 3,
                SyncLossCount = 1,
                BeatSynced = true,
                MetricsHistory = new BeatMetricsHistorySnapshot
                {
                    Bph = 43200,
                },
            });
        }

        string[] lines = File.ReadAllLines(path);
        File.Delete(path);

        Assert.Equal(
            "timestamp_utc,session_id,source_id,source_sample_end,sample_rate,bph,beat_period_ms," +
            "pending_samples,analysis_lag_samples,analysis_lag_ms,processing_elapsed_ms," +
            "deadline_level,input_overrun,input_samples_dropped,missed_beats,sync_loss_count,beat_synced",
            lines[0]);
        Assert.EndsWith(
            ",7,9,4096,48000,43200,83.333333,4096,480,10.000000,12.345678,2,1,96,3,1,1",
            lines[1]);
    }
}
