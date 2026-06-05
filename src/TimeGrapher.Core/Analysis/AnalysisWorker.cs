using System.Diagnostics;
using System.Globalization;
using TimeGrapher.Core.AudioIo;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Imaging;
using TimeGrapher.Core.Metrics;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Analysis;

/// <summary>
/// Port of TAnalysisWorker (AnalysisWorker.cpp). Owns the detector / sound-image
/// renderer / watch-metrics and turns new audio in the shared ring buffer into a
/// single <see cref="AnalysisFrame"/> per <see cref="HandleInputData"/> pass.
///
/// Threading: the original relied on Qt's moveToThread + queued
/// AudioDataReady -> HandleInputData. This port owns a dedicated thread that waits
/// on an <see cref="AutoResetEvent"/> (signalled by <see cref="NotifyDataReady"/>)
/// and runs HandleInputData once per wake-up.
/// </summary>
public sealed class AnalysisWorker : IDisposable
{
    public sealed class Config
    {
        public int SampleRate = 48000;
        public double LiftAngle = 52.0;
        public int AveragingPeriod = 2;
        public bool UseCOnset = false;
        public ulong SessionId = 0;
        public bool AutoBph = true;
        public int ManualBph = 0;
        public double HpfCutoffHz = 0.0;
        public int SoundImageWidth = 0;
        public int SoundImageHeight = 0;
        public WavStreamWriter? WavWriter = null;
    }

    private const uint DetectorNumberOfSamples = 4096u;
    private const int SoundPixelSize = 3;

    // double inwardMarkerLength(int sample_rate): 500.0 * (sample_rate / 48000.0)
    private static double InwardMarkerLength(int sampleRate)
    {
        return 500.0 * (sampleRate / 48000.0);
    }

    private readonly MasterAudioBuffer _rawAudio;
    private readonly Config _config;
    private readonly WatchMetrics _metrics;
    private readonly TgConfig _detectorConfig;
    private readonly TgDetector _ctx;
    private readonly float[] _inputBlock;
    private readonly PixelBuffer _soundImage;
    private readonly SoundImageRenderer _soundRenderer = new();
    private readonly TgResult _result = new();

    private bool _soundRenderHasBph = false;
    private double _lastA = 0.0;
    private bool _haveLastA = false;
    private ulong _localGraphTicks = 0;
    private ulong _nextFrameSourceId = 1;
    private bool _foregroundTimerStarted = false;
    private readonly Stopwatch _foregroundTimer = new();
    private double _foregroundLastTime = 0.0;
    private ulong _foregroundFrameCount = 0;
    private ulong _foregroundSampleCount = 0;

    // Thread loop state (port-only; replaces Qt moveToThread).
    private Thread? _thread;
    private readonly AutoResetEvent _wakeup = new(false);
    private volatile bool _stopRequested = false;

    /// <summary>Raised on the analysis thread when a frame is ready.</summary>
    public event Action<AnalysisFrame>? AnalysisFrameReady;

    public AnalysisWorker(MasterAudioBuffer buffer, Config config)
    {
        _rawAudio = buffer;
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

        _detectorConfig = TgConfig.Default();
        _detectorConfig.SampleRate = _config.SampleRate;
        _detectorConfig.BphMode = _config.AutoBph ? TgBphMode.Auto : TgBphMode.Manual;
        _detectorConfig.ManualBph = _config.ManualBph;
        _detectorConfig.SuppressPreSyncEvents = true;
        _detectorConfig.HpfCutoffHz = _config.HpfCutoffHz;

        _ctx = new TgDetector(_detectorConfig);

        _inputBlock = new float[DetectorNumberOfSamples];

        _soundImage = new PixelBuffer(_config.SoundImageWidth, _config.SoundImageHeight);
        var soundImageConfig = new SoundImageRenderer.Config
        {
            Bph = 0.0,
            SampleRateHz = _config.SampleRate,
            SoundColor = Argb.Rgba(255, 0, 0, 255),
            BackgroundColor = Argb.Rgba(255, 255, 255, 255),
            Direction = SoundImageRenderer.VerticalTimeDirection.TopDown,
            WarmupColumns = 2,
            AnchorColumns = 12,
            Gamma = 0.5f,
            LivePreviewCurrentColumn = true,
        };

        if (!_soundRenderer.Initialize(_soundImage, soundImageConfig))
        {
            throw new InvalidOperationException("Failed to initialize SoundImageRenderer.");
        }
        _soundRenderer.Reset();
        _metrics.Reset();
    }

    /// <summary>Starts the analysis thread (AutoResetEvent wait loop).</summary>
    public void Start()
    {
        if (_thread != null)
        {
            return;
        }
        _stopRequested = false;
        _thread = new Thread(ThreadLoop)
        {
            Name = "AnalysisWorker",
            IsBackground = true,
            Priority = ThreadPriority.Highest, // QThread::TimeCriticalPriority
        };
        _thread.Start();
    }

    /// <summary>Signal that new audio is available. Callable from any thread.</summary>
    public void NotifyDataReady()
    {
        _wakeup.Set();
    }

    /// <summary>Stops the analysis thread and joins it.</summary>
    public void Stop()
    {
        if (_thread == null)
        {
            return;
        }
        _stopRequested = true;
        _wakeup.Set();
        _thread.Join();
        _thread = null;
    }

    private void ThreadLoop()
    {
        while (true)
        {
            _wakeup.WaitOne();
            if (_stopRequested)
            {
                break;
            }
            HandleInputData();
        }
    }

    public void HandleInputData()
    {
        ulong localTotalSamplesWritten;
        lock (_rawAudio.Lock)
        {
            localTotalSamplesWritten = _rawAudio.TotalSamplesWritten;
        }

        int samplesToAdd = (int)(localTotalSamplesWritten - _rawAudio.AnalysisLastTotalSamplesWritten);
        if (samplesToAdd <= 0)
        {
            return;
        }

        var frame = new AnalysisFrame
        {
            SessionId = _config.SessionId,
            SourceId = _nextFrameSourceId++,
            SourceSampleEnd = localTotalSamplesWritten,
            BackgroundFps = _rawAudio.Fps,
            BackgroundSps = _rawAudio.Sps,
            BackgroundSpf = _rawAudio.Spf,
        };

        if (!_foregroundTimerStarted)
        {
            _foregroundTimer.Restart();
            _foregroundTimerStarted = true;
            _foregroundLastTime = 0.0;
            _foregroundFrameCount = 0;
            _foregroundSampleCount = 0;
        }

        while (samplesToAdd > 0)
        {
            int slice = samplesToAdd > (int)DetectorNumberOfSamples
                            ? (int)DetectorNumberOfSamples
                            : samplesToAdd;

            for (int i = 0; i < slice; i++)
            {
                _inputBlock[i] = _rawAudio.Samples[_rawAudio.AnalysisLastWriteIndex];
                _rawAudio.AnalysisLastWriteIndex =
                    (_rawAudio.AnalysisLastWriteIndex + 1) % (uint)_rawAudio.NumberOfAudioSamples;
            }

            var block = new ReadOnlySpan<float>(_inputBlock, 0, slice);

            _config.WavWriter?.Write(block);

            _soundRenderer.ProcessSamples(block);

            _ctx.Process(block, _result);

            ProcessDetectorResult(_result, slice, frame);
            UpdateForegroundStats(slice, frame);
            samplesToAdd -= slice;
        }

        _rawAudio.AnalysisLastTotalSamplesWritten = localTotalSamplesWritten;
        frame.GraphTickEnd = _localGraphTicks;
        frame.SoundImage = _soundImage.Clone();
        frame.SoundImageUpdated = true;
        AppendGraphSeries(frame);

        AnalysisFrameReady?.Invoke(frame);
    }

    private void ProcessDetectorResult(TgResult result, int slice, AnalysisFrame frame)
    {
        // Q_UNUSED(slice)
        _ = slice;

        double threshold = result.OnsetThreshold;
        for (int i = 0; i < result.ProcessedPcmLen; i++)
        {
            frame.ScopeX.Add(_localGraphTicks);
            frame.ScopePcm.Add(result.ProcessedPcm[i]);
            frame.ScopeThreshold.Add(threshold);
            _localGraphTicks++;
        }

        if ((!_soundRenderHasBph) && (result.SyncStatus == TgSyncStatus.Synced))
        {
            _soundRenderHasBph = true;
            _soundRenderer.SetBph(result.DetectedBph);
        }

        for (int i = 0; i < result.Events.Count; i++)
        {
            if (result.Events[i].Type == TgEventType.A)
            {
                double value = result.Events[i].SampleIndex + result.Events[i].SubSampleOffset;
                AppendAEvent(value, result.Events[i].PeakValue, result.SyncStatus == TgSyncStatus.Synced, result.DetectedBph, frame);
            }
            else if (result.Events[i].Type == TgEventType.C)
            {
                AppendCEvent(result.Events[i], result.SyncStatus == TgSyncStatus.Synced, result.DetectedBph, frame);
            }
            else
            {
                Console.Error.WriteLine("Unkown Event Type");
            }
        }
    }

    private void AppendAEvent(double eventSample, float peakValue, bool synced, int detectedBph, AnalysisFrame frame)
    {
        var vertical = new ScopeVerticalMarker
        {
            X = eventSample,
            Height = peakValue,
            Color = Argb.Green,
        };
        frame.VerticalMarkers.Add(vertical);

        if (_haveLastA)
        {
            double delta = eventSample - _lastA;

            var horizontal = new ScopeHorizontalMarker
            {
                Direction = HorizontalMarkerDirection.Outward,
                XLeft = _lastA,
                XRight = eventSample,
                Height = peakValue / 2.0,
                Color = Argb.Black,
            };
            frame.HorizontalMarkers.Add(horizontal);

            var textMarker = new ScopeTextMarker
            {
                X = _lastA + (delta / 2.0),
                Height = peakValue / 2.0,
                // QString(" %1 ms ").arg(delta * 1000.0 / sample_rate, 0, 'f', 2)
                Text = " " + (delta * 1000.0 / _config.SampleRate).ToString("F2", CultureInfo.InvariantCulture) + " ms ",
                Color = Argb.Black,
                Alignment = MarkerTextAlignment.CenterTop, // Qt::AlignHCenter | Qt::AlignTop
            };
            frame.TextMarkers.Add(textMarker);
        }

        _lastA = eventSample;
        _haveLastA = true;
        AppendMetricsUpdate(_metrics.HandleAEvent(eventSample, synced, detectedBph), frame);

        if (_soundRenderHasBph)
        {
            // markAEventAbsoluteSampleIndex takes quint64; double is implicitly truncated.
            _soundRenderer.MarkAEventAbsoluteSampleIndex((ulong)eventSample, Argb.Rgba(0, 255, 0, 255), SoundPixelSize);
        }
    }

    private void AppendCEvent(TgEvent ev, bool synced, int detectedBph, AnalysisFrame frame)
    {
        double eventSample;
        if (_config.UseCOnset)
        {
            if (ev.OnsetValid)
            {
                eventSample = ev.OnsetSampleIndex + ev.OnsetSubSampleOffset;
            }
            else
            {
                Console.Error.WriteLine("Invalid C Onset using C peak");
                eventSample = ev.SampleIndex + ev.SubSampleOffset;
            }
        }
        else
        {
            eventSample = ev.SampleIndex + ev.SubSampleOffset;
        }

        WatchMetricsUpdate metricsUpdate = _metrics.HandleCEvent(eventSample, synced, detectedBph);

        var vertical = new ScopeVerticalMarker
        {
            X = eventSample,
            Height = ev.PeakValue,
            Color = Argb.Red,
        };
        frame.VerticalMarkers.Add(vertical);

        var horizontal = new ScopeHorizontalMarker
        {
            Direction = HorizontalMarkerDirection.Inward,
            XLeft = _lastA,
            XRight = eventSample,
            Length = InwardMarkerLength(_config.SampleRate),
            Height = ev.PeakValue,
            Color = Argb.Black,
        };
        frame.HorizontalMarkers.Add(horizontal);

        var textMarker = new ScopeTextMarker
        {
            X = eventSample + InwardMarkerLength(_config.SampleRate),
            Height = ev.PeakValue,
            Text = metricsUpdate.CMarkerText,
            Color = Argb.Black,
            Alignment = MarkerTextAlignment.LeftTop, // Qt::AlignLeft | Qt::AlignTop
        };
        frame.TextMarkers.Add(textMarker);

        AppendMetricsUpdate(metricsUpdate, frame);

        if (_soundRenderHasBph)
        {
            _soundRenderer.MarkCEventAbsoluteSampleIndex((ulong)eventSample, Argb.Rgba(0, 0, 255, 255), SoundPixelSize);
        }
    }

    private void AppendMetricsUpdate(WatchMetricsUpdate update, AnalysisFrame frame)
    {
        if (update.TicRateUpdated)
        {
            frame.MetricsUpdate.TicRateUpdated = true;
            frame.MetricsUpdate.XTic = update.XTic;
            frame.MetricsUpdate.YTic = update.YTic;
        }
        if (update.TocRateUpdated)
        {
            frame.MetricsUpdate.TocRateUpdated = true;
            frame.MetricsUpdate.XToc = update.XToc;
            frame.MetricsUpdate.YToc = update.YToc;
        }
        if (update.ResultsUpdated)
        {
            frame.MetricsUpdate.ResultsUpdated = true;
            frame.MetricsUpdate.ResultsText = update.ResultsText;
        }
    }

    private void AppendGraphSeries(AnalysisFrame frame)
    {
        if (frame.ScopeX.Count != 0)
        {
            var pcmSeries = new GraphSeriesFrame
            {
                Id = AnalysisGraphSeries.ScopePcm,
                X = frame.ScopeX,
                Y = frame.ScopePcm,
            };
            frame.ScopeSeries.Add(pcmSeries);

            var thresholdSeries = new GraphSeriesFrame
            {
                Id = AnalysisGraphSeries.ScopeThreshold,
                X = frame.ScopeX,
                Y = frame.ScopeThreshold,
            };
            frame.ScopeSeries.Add(thresholdSeries);
        }

        if (frame.MetricsUpdate.TicRateUpdated)
        {
            var ticSeries = new GraphSeriesFrame
            {
                Id = AnalysisGraphSeries.RateTic,
                X = frame.MetricsUpdate.XTic,
                Y = frame.MetricsUpdate.YTic,
                Replace = true,
            };
            frame.RateSeries.Add(ticSeries);
        }

        if (frame.MetricsUpdate.TocRateUpdated)
        {
            var tocSeries = new GraphSeriesFrame
            {
                Id = AnalysisGraphSeries.RateToc,
                X = frame.MetricsUpdate.XToc,
                Y = frame.MetricsUpdate.YToc,
                Replace = true,
            };
            frame.RateSeries.Add(tocSeries);
        }
    }

    private void UpdateForegroundStats(int slice, AnalysisFrame frame)
    {
        _foregroundSampleCount += (ulong)slice;
        _foregroundFrameCount++;

        double currentTime = _foregroundTimer.ElapsedMilliseconds / 1000.0;
        if (currentTime - _foregroundLastTime > 2)
        {
            double fdelta = currentTime - _foregroundLastTime;
            frame.ForegroundFps = _foregroundFrameCount / fdelta;
            frame.ForegroundSps = _foregroundSampleCount / fdelta;
            frame.ForegroundSpf = (double)_foregroundSampleCount / _foregroundFrameCount;
            frame.ForegroundStatsUpdated = true;

            _foregroundLastTime = currentTime;
            _foregroundFrameCount = 0;
            _foregroundSampleCount = 0;
        }
    }

    public void Dispose()
    {
        Stop();
        _wakeup.Dispose();
    }
}
