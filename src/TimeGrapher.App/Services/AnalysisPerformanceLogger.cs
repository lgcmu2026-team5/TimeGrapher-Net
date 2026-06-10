using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Services;

internal sealed class AnalysisPerformanceLogger : IDisposable
{
    private readonly BlockingCollection<AnalysisPerformanceLogEntry> _entries = new();
    private readonly StreamWriter _writer;
    private readonly Thread _writerThread;
    private readonly object _gate = new();
    private bool _disposed;

    public AnalysisPerformanceLogger(string path)
    {
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _writer.WriteLine(
            "timestamp_utc,session_id,source_id,source_sample_end,sample_rate,bph,beat_period_ms," +
            "pending_samples,analysis_lag_samples,analysis_lag_ms,processing_elapsed_ms," +
            "deadline_level,input_overrun,input_samples_dropped,missed_beats,sync_loss_count,beat_synced");

        _writerThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "Analysis performance logger",
        };
        _writerThread.Start();
    }

    public void Observe(AnalysisFrame frame)
    {
        AnalysisPerformanceLogEntry entry = AnalysisPerformanceLogEntry.FromFrame(frame);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _entries.Add(entry);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _entries.CompleteAdding();
        }

        _writerThread.Join();
        _writer.Dispose();
        _entries.Dispose();
    }

    private void WriteLoop()
    {
        foreach (AnalysisPerformanceLogEntry entry in _entries.GetConsumingEnumerable())
        {
            WriteEntry(entry);
        }

        _writer.Flush();
    }

    private void WriteEntry(AnalysisPerformanceLogEntry entry)
    {
        _writer.Write(entry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
        Write(entry.SessionId);
        Write(entry.SourceId);
        Write(entry.SourceSampleEnd);
        Write(entry.SampleRate);
        Write(entry.Bph);
        Write(entry.BeatPeriodMs);
        Write(entry.PendingSamples);
        Write(entry.AnalysisLagSamples);
        Write(entry.AnalysisLagMs);
        Write(entry.ProcessingElapsedMs);
        Write(entry.DeadlineLevel);
        Write(entry.InputOverrun ? 1 : 0);
        Write(entry.InputSamplesDropped);
        Write(entry.MissedBeats);
        Write(entry.SyncLossCount);
        Write(entry.BeatSynced ? 1 : 0);
        _writer.WriteLine();
    }

    private void Write(double value)
    {
        _writer.Write(',');
        if (!double.IsNaN(value))
        {
            _writer.Write(value.ToString("F6", CultureInfo.InvariantCulture));
        }
    }

    private void Write(ulong value)
    {
        _writer.Write(',');
        _writer.Write(value.ToString(CultureInfo.InvariantCulture));
    }

    private void Write(uint value)
    {
        _writer.Write(',');
        _writer.Write(value.ToString(CultureInfo.InvariantCulture));
    }

    private void Write(int value)
    {
        _writer.Write(',');
        _writer.Write(value.ToString(CultureInfo.InvariantCulture));
    }

    private readonly record struct AnalysisPerformanceLogEntry(
        DateTimeOffset TimestampUtc,
        ulong SessionId,
        ulong SourceId,
        ulong SourceSampleEnd,
        int SampleRate,
        int Bph,
        double BeatPeriodMs,
        ulong PendingSamples,
        ulong AnalysisLagSamples,
        double AnalysisLagMs,
        double ProcessingElapsedMs,
        int DeadlineLevel,
        bool InputOverrun,
        ulong InputSamplesDropped,
        ulong MissedBeats,
        uint SyncLossCount,
        bool BeatSynced)
    {
        public static AnalysisPerformanceLogEntry FromFrame(AnalysisFrame frame)
        {
            int sampleRate = Math.Max(1, frame.SampleRate);
            int bph = frame.MetricsHistory?.Bph
                ?? (frame.MetricsUpdate.BeatTimingSampleUpdated ? frame.MetricsUpdate.BeatTimingSample.Bph : 0);

            return new AnalysisPerformanceLogEntry(
                DateTimeOffset.UtcNow,
                frame.SessionId,
                frame.SourceId,
                frame.SourceSampleEnd,
                frame.SampleRate,
                bph,
                bph > 0 ? 3600.0 * 1000.0 / bph : double.NaN,
                frame.PendingSamples,
                frame.AnalysisLagSamples,
                frame.AnalysisLagSamples * 1000.0 / sampleRate,
                frame.ProcessingElapsedMs,
                frame.DeadlineDegradationLevel,
                frame.InputOverrun,
                frame.InputSamplesDropped,
                frame.MissedBeats,
                frame.SyncLossCount,
                frame.BeatSynced);
        }
    }
}
