using System.Diagnostics;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Imaging;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Analysis;

public sealed class SoundPrintFrameProjector
{
    private const int SoundPixelSize = 3;
    private const int SoundImagePublishIntervalMs = 100;

    private readonly PixelBuffer _soundImage;
    private readonly SoundImageRenderer _soundRenderer = new();
    private readonly Stopwatch _publishTimer = new();
    private bool _publishPending = true;
    private bool _hasBph;

    public SoundPrintFrameProjector(int sampleRate, int width, int height)
    {
        _soundImage = new PixelBuffer(width, height);
        var config = new SoundImageRenderer.Config
        {
            Bph = 0.0,
            SampleRateHz = sampleRate,
            SoundColor = Argb.Rgba(255, 0, 0, 255),
            BackgroundColor = Argb.Rgba(255, 255, 255, 255),
            Direction = SoundImageRenderer.VerticalTimeDirection.TopDown,
            WarmupColumns = 2,
            AnchorColumns = 12,
            Gamma = 0.5f,
            LivePreviewCurrentColumn = true,
        };

        if (!_soundRenderer.Initialize(_soundImage, config))
        {
            throw new InvalidOperationException("Failed to initialize SoundImageRenderer.");
        }
        _soundRenderer.Reset();
    }

    public void ProcessSamples(ReadOnlySpan<float> block)
    {
        _soundRenderer.ProcessSamples(block);
    }

    public void Project(DetectorMetricsBlockUpdate update)
    {
        DetectorResultSnapshot result = update.Result;
        if (result.SyncLostEvent || result.DetectorResetEvent || result.SyncStatus != TgSyncStatus.Synced)
        {
            _hasBph = false;
            _soundRenderer.SetBph(0.0);
        }
        else if ((!_hasBph || Math.Abs(_soundRenderer.CurrentBph - result.DetectedBph) > 0.5) &&
                 result.SyncStatus == TgSyncStatus.Synced)
        {
            _hasBph = true;
            _soundRenderer.SetBph(result.DetectedBph);
        }

        if (!_hasBph)
        {
            return;
        }

        foreach (DetectedEventUpdate eventUpdate in update.Events)
        {
            if (eventUpdate.Event.Type == TgEventType.A)
            {
                _soundRenderer.MarkAEventAbsoluteSampleIndex((ulong)eventUpdate.EventSample, Argb.Rgba(0, 255, 0, 255), SoundPixelSize);
            }
            else if (eventUpdate.Event.Type == TgEventType.C)
            {
                _soundRenderer.MarkCEventAbsoluteSampleIndex((ulong)eventUpdate.EventSample, Argb.Rgba(0, 0, 255, 255), SoundPixelSize);
            }
        }
    }

    public void AppendSnapshot(AnalysisFrame frame, bool force = false)
    {
        if (force ||
            _publishPending ||
            !_publishTimer.IsRunning ||
            _publishTimer.ElapsedMilliseconds >= SoundImagePublishIntervalMs)
        {
            frame.SoundImage = _soundImage.Clone();
            frame.SoundImageUpdated = true;
            _publishPending = false;
            _publishTimer.Restart();
        }
    }
}
