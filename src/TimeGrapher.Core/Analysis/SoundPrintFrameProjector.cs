using System.Diagnostics;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Imaging;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Analysis;

public sealed class SoundPrintFrameProjector
{
    private const int SoundPixelSize = 3;
    private const int SoundImagePublishIntervalMs = 100;

    // Publish snapshots rotate through this fixed pool instead of allocating a
    // fresh width*height*4-byte buffer (LOH-sized at the default 1019x654) per
    // publish. A published buffer is overwritten again only after
    // PublishBufferCount-1 newer publishes; the render scheduler's latest-wins
    // delivery keeps the UI within one publish of the newest image, so on-screen
    // reads never touch a buffer that is being recycled.
    private const int PublishBufferCount = 3;

    private readonly PixelBuffer _soundImage;
    private readonly PixelBuffer[] _publishBuffers = new PixelBuffer[PublishBufferCount];
    private int _nextPublishBuffer;
    private readonly SoundImageRenderer _soundRenderer = new();
    private readonly Stopwatch _publishTimer = new();
    private bool _publishPending = true;
    private bool _hasBph;
    private int _publishIntervalScale = 1;

    public SoundPrintFrameProjector(int sampleRate, int width, int height, uint backgroundColor)
    {
        _soundImage = new PixelBuffer(width, height);
        for (int i = 0; i < PublishBufferCount; ++i)
        {
            _publishBuffers[i] = new PixelBuffer(width, height);
        }
        var config = new SoundImageRenderer.Config
        {
            Bph = 0.0,
            SampleRateHz = sampleRate,
            SoundColor = Argb.Rgba(255, 0, 0, 255),
            BackgroundColor = backgroundColor,
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

    /// <summary>
    /// Deadline-degradation knob: disable/re-enable the live redraw of the
    /// in-progress column. Analysis thread only.
    /// </summary>
    public void SetLivePreviewEnabled(bool enabled)
    {
        _soundRenderer.SetLivePreviewCurrentColumn(enabled);
    }

    /// <summary>
    /// Deadline-degradation knob: stretch the publish interval by an integer
    /// factor (1 = the default 100 ms cadence). Analysis thread only.
    /// </summary>
    public void SetPublishIntervalScale(int scale)
    {
        _publishIntervalScale = Math.Max(1, scale);
    }

    /// <summary>
    /// Re-tints the sound print to a new background color and flags the image for
    /// republish on the next <see cref="AppendSnapshot"/>.
    /// </summary>
    public void SetBackgroundColor(uint backgroundColor)
    {
        _soundRenderer.Recolor(backgroundColor);
        _publishPending = true;
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
            _publishTimer.ElapsedMilliseconds >= (long)SoundImagePublishIntervalMs * _publishIntervalScale)
        {
            PixelBuffer snapshot = _publishBuffers[_nextPublishBuffer];
            _nextPublishBuffer = (_nextPublishBuffer + 1) % PublishBufferCount;
            Array.Copy(_soundImage.Pixels, snapshot.Pixels, snapshot.Pixels.Length);
            frame.SoundImage = snapshot;
            frame.SoundImageUpdated = true;
            _publishPending = false;
            _publishTimer.Restart();
        }
    }
}
