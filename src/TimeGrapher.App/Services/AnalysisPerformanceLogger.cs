using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using TimeGrapher.Core.Metrics;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Services;

internal sealed class AnalysisPerformanceLogger : IDisposable
{
    private readonly BlockingCollection<AnalysisPerformanceLogEntry> _entries = new();
    private readonly StreamWriter _writer;
    private readonly Thread _writerThread;
    private readonly object _gate = new();
    private readonly double _ticksPerMs;
    private readonly RunningStats _capToProcMs = new();
    private readonly RunningStats _procToDispMs = new();
    private readonly RunningStats _endToEndMs = new();
    private ulong _droppedAudioSamples;
    private bool _disposed;

    public AnalysisPerformanceLogger(string path, double? ticksPerMs = null)
    {
        _ticksPerMs = ticksPerMs ?? Stopwatch.Frequency / 1000.0;
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _writer.WriteLine(
            "capture_to_processing_ms,processing_to_display_ms,end_to_end_latency_ms," +
            "capture_to_processing_avg_ms,capture_to_processing_worst_ms," +
            "processing_to_display_avg_ms,processing_to_display_worst_ms," +
            "end_to_end_avg_ms,end_to_end_worst_ms,dropped_audio_samples,missed_beat_detections");

        _writerThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "Analysis performance logger",
        };
        _writerThread.Start();
    }

    public void ObserveDisplayed(AnalysisFrame frame, long displayTicks)
    {
        if (frame.CaptureTimestamp <= 0 || frame.ProcessingCompletedTimestamp <= 0)
        {
            return;
        }

        double capToProcMs = (frame.ProcessingCompletedTimestamp - frame.CaptureTimestamp) / _ticksPerMs;
        double procToDispMs = (displayTicks - frame.ProcessingCompletedTimestamp) / _ticksPerMs;
        double endToEndMs = (displayTicks - frame.CaptureTimestamp) / _ticksPerMs;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _capToProcMs.Add(capToProcMs);
            _procToDispMs.Add(procToDispMs);
            _endToEndMs.Add(endToEndMs);
            _droppedAudioSamples += frame.InputSamplesDropped;

            var entry = new AnalysisPerformanceLogEntry(
                capToProcMs,
                procToDispMs,
                endToEndMs,
                _capToProcMs.Mean,
                _capToProcMs.Max,
                _procToDispMs.Mean,
                _procToDispMs.Max,
                _endToEndMs.Mean,
                _endToEndMs.Max,
                _droppedAudioSamples,
                frame.MissedBeats);
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
        _writer.Write(entry.CaptureToProcessingMs.ToString("F6", CultureInfo.InvariantCulture));
        Write(entry.ProcessingToDisplayMs);
        Write(entry.EndToEndLatencyMs);
        Write(entry.CaptureToProcessingAvgMs);
        Write(entry.CaptureToProcessingWorstMs);
        Write(entry.ProcessingToDisplayAvgMs);
        Write(entry.ProcessingToDisplayWorstMs);
        Write(entry.EndToEndAvgMs);
        Write(entry.EndToEndWorstMs);
        Write(entry.DroppedAudioSamples);
        Write(entry.MissedBeatDetections);
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

    private readonly record struct AnalysisPerformanceLogEntry(
        double CaptureToProcessingMs,
        double ProcessingToDisplayMs,
        double EndToEndLatencyMs,
        double CaptureToProcessingAvgMs,
        double CaptureToProcessingWorstMs,
        double ProcessingToDisplayAvgMs,
        double ProcessingToDisplayWorstMs,
        double EndToEndAvgMs,
        double EndToEndWorstMs,
        ulong DroppedAudioSamples,
        ulong MissedBeatDetections);
}
