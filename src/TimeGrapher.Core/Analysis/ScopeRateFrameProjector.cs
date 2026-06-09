using System.Globalization;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Analysis;

public sealed class ScopeRateFrameProjector
{
    private const int ScopeSnapshotSeconds = 2;

    private readonly int _sampleRate;
    private readonly bool _useCOnset;
    private readonly int _scopeSnapshotPointBudget;
    private readonly List<double> _scopeWindowX = new();
    private readonly List<double> _scopeWindowPcm = new();
    private readonly List<double> _scopeWindowThreshold = new();
    private readonly List<ScopeVerticalMarker> _scopeWindowVerticalMarkers = new();
    private readonly List<ScopeHorizontalMarker> _scopeWindowHorizontalMarkers = new();
    private readonly List<ScopeTextMarker> _scopeWindowTextMarkers = new();
    // Latest tic/toc rate series as immutable snapshots. Rate data changes only on
    // beat events (~8 Hz at 28800 BPH) while frames are produced per audio block
    // (50-100 Hz), so the snapshot is rebuilt once per actual update and the SAME
    // object is reattached to the frames in between — every frame still carries
    // the full latest bundle, without re-copying 250-point lists per frame.
    // Consumers only read GraphSeriesFrame (init-only, IReadOnlyList), so sharing
    // one instance across frames is safe.
    private GraphSeriesFrame? _latestTicRateSeries;
    private GraphSeriesFrame? _latestTocRateSeries;
    private string _latestResultsText = "";
    private ulong _localGraphTicks;
    private double _lastA;
    private bool _haveLastA;
    private bool _hasLatestResultsText;

    private int _strideScale = 1;

    public ScopeRateFrameProjector(int sampleRate, bool useCOnset, int scopeSnapshotPointBudget)
    {
        _sampleRate = sampleRate;
        _useCOnset = useCOnset;
        _scopeSnapshotPointBudget = scopeSnapshotPointBudget;
    }

    /// <summary>
    /// Deadline-degradation knob: coarsen the scope decimation stride by an integer
    /// factor (1 = the configured point budget). Analysis thread only.
    /// </summary>
    public void SetScopeStrideScale(int scale)
    {
        _strideScale = Math.Max(1, scale);
    }

    public void Project(DetectorMetricsBlockUpdate update, AnalysisFrame frame)
    {
        DetectorResultSnapshot result = update.Result;
        frame.BeatSynced = result.SyncStatus == TgSyncStatus.Synced;
        double threshold = result.OnsetThreshold;
        ulong scopeStride = (ulong)ScopeSnapshotStride();
        for (int i = 0; i < result.ProcessedPcmLen; i++)
        {
            if ((_localGraphTicks % scopeStride) == 0)
            {
                _scopeWindowX.Add(_localGraphTicks);
                _scopeWindowPcm.Add(result.ProcessedPcm[i]);
                _scopeWindowThreshold.Add(threshold);
            }
            _localGraphTicks++;
        }

        foreach (DetectedEventUpdate eventUpdate in update.Events)
        {
            if (eventUpdate.Event.Type == TgEventType.A)
            {
                AppendAEvent(eventUpdate.EventSample, eventUpdate.Event.PeakValue, eventUpdate.MetricsUpdate, frame);
            }
            else if (eventUpdate.Event.Type == TgEventType.C)
            {
                AppendCEvent(eventUpdate.Event, eventUpdate.EventSample, eventUpdate.MetricsUpdate, frame);
            }
            else
            {
                Console.Error.WriteLine("Unknown Event Type");
            }
        }
    }

    public void AppendSnapshot(AnalysisFrame frame)
    {
        TrimScopeWindow();
        frame.GraphTickEnd = _localGraphTicks;

        if (_scopeWindowX.Count != 0)
        {
            var scopeX = new List<double>(_scopeWindowX);
            frame.AddScopeSeries(new GraphSeriesFrame
            {
                Id = AnalysisGraphSeries.ScopePcm,
                X = scopeX,
                Y = new List<double>(_scopeWindowPcm),
                Replace = true,
            });

            frame.AddScopeSeries(new GraphSeriesFrame
            {
                Id = AnalysisGraphSeries.ScopeThreshold,
                X = scopeX,
                Y = new List<double>(_scopeWindowThreshold),
                Replace = true,
            });
        }

        frame.SetScopeMarkers(_scopeWindowVerticalMarkers, _scopeWindowHorizontalMarkers, _scopeWindowTextMarkers);

        if (_hasLatestResultsText)
        {
            frame.MetricsUpdate.SetResults(_latestResultsText);
        }

        if (_latestTicRateSeries != null)
        {
            frame.AddRateSeries(_latestTicRateSeries);
        }

        if (_latestTocRateSeries != null)
        {
            frame.AddRateSeries(_latestTocRateSeries);
        }
    }

    private void AppendAEvent(double eventSample, float peakValue, WatchMetricsUpdate metricsUpdate, AnalysisFrame frame)
    {
        _scopeWindowVerticalMarkers.Add(new ScopeVerticalMarker
        {
            X = eventSample,
            Height = peakValue,
            Color = Argb.Green,
        });

        if (_haveLastA)
        {
            double delta = eventSample - _lastA;
            _scopeWindowHorizontalMarkers.Add(new ScopeHorizontalMarker
            {
                Direction = HorizontalMarkerDirection.Outward,
                XLeft = _lastA,
                XRight = eventSample,
                Height = peakValue / 2.0,
                Color = Argb.Black,
            });

            _scopeWindowTextMarkers.Add(new ScopeTextMarker
            {
                X = _lastA + (delta / 2.0),
                Height = peakValue / 2.0,
                Text = " " + (delta * 1000.0 / _sampleRate).ToString("F2", CultureInfo.InvariantCulture) + " ms ",
                Color = Argb.Black,
                Alignment = MarkerTextAlignment.CenterTop,
            });
        }

        _lastA = eventSample;
        _haveLastA = true;
        AppendMetricsUpdate(metricsUpdate, frame);
    }

    private void AppendCEvent(TgEvent ev, double eventSample, WatchMetricsUpdate metricsUpdate, AnalysisFrame frame)
    {
        if (_useCOnset && !ev.OnsetValid)
        {
            Console.Error.WriteLine("Invalid C Onset using C peak");
        }

        _scopeWindowVerticalMarkers.Add(new ScopeVerticalMarker
        {
            X = eventSample,
            Height = ev.PeakValue,
            Color = Argb.Red,
        });

        _scopeWindowHorizontalMarkers.Add(new ScopeHorizontalMarker
        {
            Direction = HorizontalMarkerDirection.Inward,
            XLeft = _lastA,
            XRight = eventSample,
            Length = InwardMarkerLength(_sampleRate),
            Height = ev.PeakValue,
            Color = Argb.Black,
        });

        _scopeWindowTextMarkers.Add(new ScopeTextMarker
        {
            X = eventSample + InwardMarkerLength(_sampleRate),
            Height = ev.PeakValue,
            Text = metricsUpdate.CMarkerText,
            Color = Argb.Black,
            Alignment = MarkerTextAlignment.LeftTop,
        });

        AppendMetricsUpdate(metricsUpdate, frame);
    }

    private void AppendMetricsUpdate(WatchMetricsUpdate update, AnalysisFrame frame)
    {
        if (update.TicRateUpdated)
        {
            _latestTicRateSeries = new GraphSeriesFrame
            {
                Id = AnalysisGraphSeries.RateTic,
                X = new List<double>(update.XTic),
                Y = new List<double>(update.YTic),
                Replace = true,
            };
            frame.MetricsUpdate.SetTicRate(update.XTic, update.YTic);
        }
        if (update.TocRateUpdated)
        {
            _latestTocRateSeries = new GraphSeriesFrame
            {
                Id = AnalysisGraphSeries.RateToc,
                X = new List<double>(update.XToc),
                Y = new List<double>(update.YToc),
                Replace = true,
            };
            frame.MetricsUpdate.SetTocRate(update.XToc, update.YToc);
        }
        if (update.ResultsUpdated)
        {
            _latestResultsText = update.ResultsText;
            _hasLatestResultsText = true;
            frame.MetricsUpdate.SetResults(update.ResultsText);
        }
    }

    private int ScopeSnapshotStride()
    {
        int baseStride = Math.Max(1, _sampleRate / 48000);
        int maxWindowSamples = Math.Max(1, ScopeSnapshotSeconds * _sampleRate);
        int pointBudget = Math.Max(1, _scopeSnapshotPointBudget);
        int budgetStride = (int)Math.Ceiling(maxWindowSamples / (double)pointBudget);
        return Math.Max(baseStride, budgetStride) * _strideScale;
    }

    private void TrimScopeWindow()
    {
        double minX = 0.0;
        ulong historySamples = (ulong)(ScopeSnapshotSeconds * _sampleRate);
        if (_localGraphTicks > historySamples)
        {
            minX = _localGraphTicks - historySamples;
        }

        int removeCount = 0;
        while (removeCount < _scopeWindowX.Count && _scopeWindowX[removeCount] < minX)
        {
            removeCount++;
        }
        if (removeCount > 0)
        {
            _scopeWindowX.RemoveRange(0, removeCount);
            _scopeWindowPcm.RemoveRange(0, removeCount);
            _scopeWindowThreshold.RemoveRange(0, removeCount);
        }

        _scopeWindowVerticalMarkers.RemoveAll(marker => marker.X < minX);
        _scopeWindowHorizontalMarkers.RemoveAll(marker =>
            Math.Max(marker.XLeft, marker.XRight) + marker.Length < minX);
        _scopeWindowTextMarkers.RemoveAll(marker => marker.X < minX);
    }

    private static double InwardMarkerLength(int sampleRate)
    {
        return 500.0 * (sampleRate / 48000.0);
    }
}
