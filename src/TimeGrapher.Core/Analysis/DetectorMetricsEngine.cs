using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Metrics;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Analysis;

public sealed record DetectorMetricsEngineConfig(
    int SampleRate,
    double LiftAngle,
    int AveragingPeriod,
    bool UseCOnset,
    bool AutoBph,
    int ManualBph,
    double HpfCutoffHz);

public readonly record struct DetectedEventUpdate(
    TgEvent Event,
    double EventSample,
    WatchMetricsUpdate MetricsUpdate);

public sealed record DetectorMetricsBlockUpdate(
    DetectorResultSnapshot Result,
    IReadOnlyList<DetectedEventUpdate> Events);

public sealed record DetectorResultSnapshot(
    TgSyncStatus SyncStatus,
    int DetectedBph,
    double MeasuredPeriodS,
    IReadOnlyList<TgEvent> Events,
    float[] ProcessedPcm,
    int ProcessedPcmLen,
    ulong ProcessedPcmStartSample,
    bool SyncLostEvent,
    bool SyncAcquiredEvent,
    bool DetectorResetEvent,
    float OnsetThreshold,
    float MinPeakThreshold,
    float NoiseFloor,
    float ReferencePeak);

/// <summary>
/// Shared detector + metrics pipeline used by the live worker and the headless
/// verifier so their event/metric contracts cannot drift apart.
/// </summary>
public sealed class DetectorMetricsEngine
{
    private readonly DetectorMetricsEngineConfig _config;
    private readonly WatchMetrics _metrics;
    private readonly TgDetector _detector;
    private readonly TgResult _result = new();

    public DetectorMetricsEngine(DetectorMetricsEngineConfig config)
    {
        _config = config;
        _metrics = new WatchMetrics(new WatchMetricsConfig
        {
            SampleRate = config.SampleRate,
            LiftAngle = config.LiftAngle,
            AveragingPeriod = config.AveragingPeriod,
            MaxRateDataPoints = 250,
            RateErrorYScale = 10.0,
            RlsWindowInit = 100,
        });

        TgConfig detectorConfig = TgConfig.Default();
        detectorConfig.SampleRate = config.SampleRate;
        detectorConfig.BphMode = config.AutoBph ? TgBphMode.Auto : TgBphMode.Manual;
        detectorConfig.ManualBph = config.ManualBph;
        detectorConfig.SuppressPreSyncEvents = true;
        detectorConfig.HpfCutoffHz = config.HpfCutoffHz;

        _detector = new TgDetector(detectorConfig);
        _metrics.Reset();
    }

    public DetectorMetricsBlockUpdate Process(ReadOnlySpan<float> block)
    {
        _detector.Process(block, _result);
        return BuildUpdate();
    }

    public DetectorMetricsBlockUpdate Flush()
    {
        _detector.Flush(_result);
        return BuildUpdate();
    }

    private DetectorMetricsBlockUpdate BuildUpdate()
    {
        bool synced = _result.SyncStatus == TgSyncStatus.Synced;
        var updates = new List<DetectedEventUpdate>(_result.Events.Count);

        foreach (TgEvent ev in _result.Events)
        {
            double eventSample = EventSample(ev);
            WatchMetricsUpdate metricsUpdate = ev.Type switch
            {
                TgEventType.A => _metrics.HandleAEvent(eventSample, synced, _result.DetectedBph),
                TgEventType.C => _metrics.HandleCEvent(eventSample, synced, _result.DetectedBph),
                _ => new WatchMetricsUpdate(),
            };

            updates.Add(new DetectedEventUpdate(ev, eventSample, metricsUpdate));
        }

        TgEvent[] eventsSnapshot = _result.Events.ToArray();
        var processedPcmSnapshot = new float[_result.ProcessedPcmLen];
        if (_result.ProcessedPcmLen > 0)
        {
            Array.Copy(_result.ProcessedPcm, processedPcmSnapshot, _result.ProcessedPcmLen);
        }

        var resultSnapshot = new DetectorResultSnapshot(
            _result.SyncStatus,
            _result.DetectedBph,
            _result.MeasuredPeriodS,
            eventsSnapshot,
            processedPcmSnapshot,
            _result.ProcessedPcmLen,
            _result.ProcessedPcmStartSample,
            _result.SyncLostEvent,
            _result.SyncAcquiredEvent,
            _result.DetectorResetEvent,
            _result.OnsetThreshold,
            _result.MinPeakThreshold,
            _result.NoiseFloor,
            _result.ReferencePeak);

        return new DetectorMetricsBlockUpdate(resultSnapshot, updates);
    }

    private double EventSample(TgEvent ev)
    {
        if (ev.Type == TgEventType.C && _config.UseCOnset && ev.OnsetValid)
        {
            return ev.OnsetSampleIndex + ev.OnsetSubSampleOffset;
        }

        return ev.SampleIndex + ev.SubSampleOffset;
    }
}
