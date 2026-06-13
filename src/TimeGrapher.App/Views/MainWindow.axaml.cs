using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;

using TimeGrapher.App;
using TimeGrapher.App.Rendering;
using TimeGrapher.App.Services;
using TimeGrapher.App.Tabs;
using TimeGrapher.App.ViewModels;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Views;

public partial class MainWindow : Window
{
    private const int ERROR_RATE_Y_SCALE = 10;
    private const int ERROR_RATE_X_DATA_POINTS = 250;
    private const int DEFAULT_SOUND_IMAGE_WIDTH = 1019;
    private const int DEFAULT_SOUND_IMAGE_HEIGHT = 654;
    private const string APP_FONT_FAMILY = "D2Coding";
    private const string PLAYBACK_SOURCE = "Playback";
    private const string SIMULATION_SOURCE = "Simulation";

    private const string PREF_NAME_WELSHI = "Welshi USB";
    private const string PREF_NAME_CHINESE_GENERIC = "Chinese Generic USB";

    private static RunSessionStopOutcome CombineStopOutcome(RunSessionStopOutcome left, RunSessionStopOutcome right)
    {
        if (left == RunSessionStopOutcome.Stopping || right == RunSessionStopOutcome.Stopping)
        {
            return RunSessionStopOutcome.Stopping;
        }

        return RunSessionStopOutcome.Stopped;
    }

    // RenameAudioDevices[][2]: { match-substring, preferred-display-name }.
    private static readonly string[][] RenameAudioDevices =
    {
        new[] { "USB PnP Sound Device", PREF_NAME_WELSHI },
        new[] { "C-Media USB Headphone Set", PREF_NAME_CHINESE_GENERIC },
        new[] { "CM108 Audio Controller Mono", PREF_NAME_WELSHI },
        new[] { "Audio Adapter Mono", PREF_NAME_CHINESE_GENERIC },
    };

    private static readonly string[] PreferredAudioDevices =
    {
        PREF_NAME_WELSHI,
        PREF_NAME_CHINESE_GENERIC,
        "Cubilux HA-3",
        "CUBILUX CA7",
    };

    private static readonly int[] AveragingPeriodList = { 2, 4, 8, 10, 12, 20, 20, 30, 40, 50, 60, 120, 240 };

    // --- Members (mirror MainWindow.h) ---
    private IRecordingWriter? mWavWriter;
    private readonly ITimeGrapherDialogService mDialogs;
    private readonly RecordingSessionService mRecordingSessionService;
    private readonly PlaybackFileService mPlaybackFileService;
    private GraphFrameRenderer mGraphFrameRenderer = null!;
    private AnalysisFrameRouter mFrameRouter = null!;
    private AnalysisFrameRenderScheduler mFrameRenderScheduler = null!;
    private InfoTabRegistry mInfoTabRegistry = null!;
    private readonly int[] mAvailableRates = new int[5];
    private int mNumberOfRates;
    private string mCurrentDir;
    private int mCurrentSamplesPerSecond;
    private int mRateBeforePlaybackOrSim;
    private string mDeviceNameBeforePlaybackOrSim = "";
    private readonly AnalysisRunStatusReporter mRunStatusReporter = new();
    private readonly LatencyStatsTracker mLatencyStats = new();
    private AnalysisFrame? mLastAnalysisFrame;
    private bool mIsClosing;
    private readonly MainWindowViewModel mViewModel;
    private readonly MainWindowSelectionCoordinator mSelectionCoordinator;
    private readonly RunSelectionResolver mRunSelectionResolver;
    private readonly RunCommandService mRunCommandService;
    private readonly RunSessionController mRunSessionController;

    private readonly List<int> mInputDeviceNumbers = new();

    public MainWindow()
    {
        InitializeComponent();
        ConfigurePlatformWindow();
        mViewModel = new MainWindowViewModel(StartRunAsync, TogglePauseRun, StopRun, LoadAudioDevices);
        mSelectionCoordinator = new MainWindowSelectionCoordinator(
            mViewModel,
            new MainWindowSelectionOperations(this),
            new MainWindowSelectionOptions(
                PLAYBACK_SOURCE,
                SIMULATION_SOURCE));
        mRunSelectionResolver = new RunSelectionResolver(
            mViewModel,
            AveragingPeriodList,
            BphCatalog.ManualAutoBph,
            BphCatalog.ManualBph);
        mDialogs = new MainWindowDialogService(this);
        mRecordingSessionService = new RecordingSessionService(mDialogs, new QueuedRecordingWriterFactory());
        mPlaybackFileService = new PlaybackFileService(mDialogs);
        mRunCommandService = new RunCommandService(mViewModel, new RunCommandOperations(this));
        mRunSessionController = new RunSessionController(
            sessionId => BuildRunSettings().ToWorkerConfig(sessionId, mWavWriter),
            Reset,
            ClearPendingAnalysisFrames,
            () => mFrameRenderScheduler.ResetTiming(),
            OnAnalysisFrameReady,
            status => mViewModel.StatusText = status);
        DataContext = mViewModel;

        // Default working directory: current dir, then ../../samples if it exists (MainWindow ctor).
        mCurrentDir = Directory.GetCurrentDirectory();
        try
        {
            string samples = Path.GetFullPath(Path.Combine(mCurrentDir, "..", "..", "samples"));
            if (Directory.Exists(samples)) mCurrentDir = samples;
        }
        catch { /* keep current dir */ }

        mCurrentSamplesPerSecond = 48000;

        string appTitle = BuildAppTitle();
        Title = appTitle;
        AppTitleText.Text = appTitle;

        // Results->setAlignment(Qt::AlignHCenter); set in XAML.
        mInfoTabRegistry = InfoTabRegistry.FromCatalog(GraphicsTabWidget, APP_FONT_FAMILY, mViewModel);
        mGraphFrameRenderer = new GraphFrameRenderer(mInfoTabRegistry.Consumers, Results);
        mGraphFrameRenderer.ApplyTheme(CurrentPlotTheme());
        mFrameRouter = mInfoTabRegistry.CreateRouter();
        mFrameRenderScheduler = new AnalysisFrameRenderScheduler(
            action => Dispatcher.UIThread.Post(action),
            ActiveInfoTabRefreshIntervalMs,
            HandleAnalysisFrame);

        // Wire events (Qt auto-connected on_* slots + explicit connect()s).
        mViewModel.PropertyChanged += mSelectionCoordinator.OnViewModelPropertyChanged;
        mViewModel.PropertyChanged += OnRunControlPropertyChanged;
        mViewModel.PropertyChanged += OnReviewCursorPropertyChanged;
        mViewModel.ResetSequenceRequested += OnResetSequenceRequested;
        GraphicsTabWidget.SelectionChanged += OnGraphicsTabSelectionChanged;

        LoadBph();
        LoadSimBph();
        LoadAudioDevices();
        mGraphFrameRenderer.Initialize(BuildTabResetContext());
        LoadAveragingPeriod();
        mGraphFrameRenderer.SetResults(GraphFrameRenderer.PlaceholderResults);
        SetGuiStopMode();

        Closed += OnWindowClosed;
    }

    private void ConfigurePlatformWindow()
    {
        if (OperatingSystem.IsWindows())
        {
            SystemDecorations = SystemDecorations.None;
            CanResize = false;
            MaximizeWindowButton.IsVisible = true;
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            SystemDecorations = SystemDecorations.None;
            CanResize = false;
            MaximizeWindowButton.IsVisible = false;
            WindowState = WindowState.Normal;
            return;
        }

        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        MaximizeWindowButton.IsVisible = false;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeWindowButtonClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnThemeToggleButtonClick(object? sender, RoutedEventArgs e)
    {
        Avalonia.Application? application = Avalonia.Application.Current;
        if (application == null)
        {
            return;
        }

        ThemeVariant nextTheme = application.RequestedThemeVariant == ThemeVariant.Dark
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

        application.RequestedThemeVariant = nextTheme;
        PlotThemePalette nextPalette = PlotThemeFor(nextTheme);
        mGraphFrameRenderer.ApplyTheme(nextPalette);
        mRunSessionController.SetSoundBackgroundColor(nextPalette.ScopeBg);
    }

    private static PlotThemePalette CurrentPlotTheme()
    {
        ThemeVariant requestedTheme = Avalonia.Application.Current?.RequestedThemeVariant ?? ThemeVariant.Light;
        return PlotThemeFor(requestedTheme);
    }

    private static PlotThemePalette PlotThemeFor(ThemeVariant theme)
    {
        return PlotThemePalette.FromResources(theme);
    }

    private void OnMaximizeWindowButtonClick(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseWindowButtonClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    // AnalysisFrameReady fires on the analysis thread; marshal to UI thread.
    private void OnAnalysisFrameReady(AnalysisFrame frame)
    {
        mFrameRenderScheduler.Enqueue(frame);
    }

    private void ClearPendingAnalysisFrames()
    {
        mFrameRenderScheduler.Reset();
        mLastAnalysisFrame = null;
    }

    private void HandleAnalysisFrame(AnalysisFrame frame, ulong droppedFrames)
    {
        if (frame.SessionId != mRunSessionController.AnalysisSessionId)
        {
            return;
        }

        mLastAnalysisFrame = frame;
        // The drained final frame of a completed run arrives after the GUI
        // reached Stopped (completeInput keeps the session id alive); it must
        // not re-raise the waiting overlay.
        if (mViewModel.RunState != RunUiState.Stopped)
        {
            mViewModel.IsAwaitingBeatSync = !frame.BeatSynced;
        }
        mGraphFrameRenderer.UpdateResults(frame);
        mFrameRouter.Route(frame, ActiveInfoTabId(), BuildTabRenderContext(frame));

        // Grow the review scrub range to the newest captured reading.
        mViewModel.UpdateReviewMaximum(frame.MetricsHistory?.LatestTimeS ?? 0.0);

        // Display leg of the latency evidence: stamped after the frame rendered.
        long displayTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        mLatencyStats.Observe(frame, droppedFrames, displayTicks);
        if (mLatencyStats.TryFormatStatus(displayTicks) is string latencyText)
        {
            mViewModel.LatencyText = latencyText;
        }

        AnalysisRunStatusReporter.Report report =
            mRunStatusReporter.Describe(frame, droppedFrames, FrameSampleRate(frame));
        if (report.StatusText != null)
        {
            mViewModel.StatusText = report.StatusText;
        }
        if (report.ConsoleWarning != null)
        {
            Console.Error.WriteLine(report.ConsoleWarning);
        }
    }

    private void Reset()
    {
        mGraphFrameRenderer.Reset(BuildTabResetContext());

        mRunStatusReporter.Reset();
        mLatencyStats.Reset();
        mViewModel.LatencyText = "";
        mViewModel.ResetReview();
    }

    // --- Event handlers (Qt on_* slots) ---

    // Analysis-worker run-control knobs forwarded from view-model properties.
    // Kept in MainWindow (not the selection coordinator, which owns input-device/
    // sample-rate/gain selection): the SetSoundBackgroundColor flow.
    private void OnRunControlPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SweepMultiple))
        {
            mRunSessionController.SetSweepMultiple(mViewModel.SweepMultiple);
        }

        if (e.PropertyName == nameof(MainWindowViewModel.SelectedPositionIndex))
        {
            mRunSessionController.SetActivePosition((WatchPosition)mViewModel.SelectedPositionIndex);
        }

        if (e.PropertyName == nameof(MainWindowViewModel.SigmaAveraging))
        {
            mRunSessionController.SetSigmaAveraging(mViewModel.SigmaAveraging);
        }
    }

    // Positions "Reset Sequence": clear the running worker's
    // per-position aggregates so a new measurement cycle starts.
    private void OnResetSequenceRequested()
    {
        mRunSessionController.ResetPositionAggregates();
    }

    // Review-cursor moves while paused re-render the kept last frame at the new
    // scrub time (the OnGraphicsTabSelectionChanged re-route): the input workers
    // are gated and the analysis is drained during pause, so no new frame would
    // otherwise carry the moved cursor to the active tab.
    private void OnReviewCursorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.ReviewCursorTimeS) ||
            mViewModel.RunState != RunUiState.Paused)
        {
            return;
        }

        AnalysisFrame? frame = mLastAnalysisFrame;
        if (frame == null || frame.SessionId != mRunSessionController.AnalysisSessionId)
        {
            return;
        }

        // Pause-exit: the cursor clears to null while RunState is still Paused
        // (the SetRunState ordering contract). Fan the cursor-less render out
        // to every tab once — tabs visited during the scrubbed pause drew the
        // dotted cursor, and after a stop the kept frame is invalidated, so the
        // active-tab route alone would leave dead cursors on the others.
        if (mViewModel.ReviewCursorTimeS == null)
        {
            mFrameRouter.RenderToAll(frame, BuildTabRenderContext(frame));
            return;
        }

        mFrameRouter.Route(frame, ActiveInfoTabId(), BuildTabRenderContext(frame));
    }

    private void OnGraphicsTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // ResetTiming (not Reset) so a pending coalesced frame — and its merged
        // transient signals — survives the tab switch and renders right after.
        mFrameRenderScheduler.ResetTiming();

        AnalysisFrame? frame = mLastAnalysisFrame;
        if (frame != null && frame.SessionId == mRunSessionController.AnalysisSessionId)
        {
            mGraphFrameRenderer.UpdateResults(frame);
            mFrameRouter.Route(frame, ActiveInfoTabId(), BuildTabRenderContext(frame));
        }
    }

    // --- Helpers ---

    internal static double ParseDouble(string? text)
    {
        // QString::toDouble returns 0.0 on failure. NumberStyles.Float matches
        // its C-locale grammar; Any would also take group separators ("0,5" ->
        // 5.0) and parenthesized negation ("(500)" -> -500).
        if (string.IsNullOrEmpty(text)) return 0.0;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0;
    }

    private AnalysisRunSettings BuildRunSettings()
    {
        AnalysisSelection selection = mRunSelectionResolver.GetAnalysisSelection();
        return new AnalysisRunSettings(
            SampleRate: mCurrentSamplesPerSecond,
            LiftAngle: (double)mViewModel.LiftAngle,
            AveragingPeriod: selection.AveragingPeriod,
            UseCOnset: mViewModel.UseCOnset,
            AutoBph: selection.AutoBph,
            ManualBph: selection.ManualBph,
            HpfCutoffHz: ParseDouble(mViewModel.HighPassCutoffText),
            SoundImageWidth: EffectivePixelWidth(SoundImageControl(), DEFAULT_SOUND_IMAGE_WIDTH),
            SoundImageHeight: EffectivePixelHeight(SoundImageControl(), DEFAULT_SOUND_IMAGE_HEIGHT),
            ScopeSnapshotPointBudget: InfoTabCatalog.ScopeTargetPointBudget,
            PllEventVeto: mViewModel.PllEventVeto);
    }

    private Control SoundImageControl()
    {
        return mInfoTabRegistry.SoundImageControl is Control control ? control : GraphicsTabWidget;
    }

    private AnalysisTabResetContext BuildTabResetContext()
    {
        return new AnalysisTabResetContext(
            SampleRate: mCurrentSamplesPerSecond,
            RateErrorYScale: ERROR_RATE_Y_SCALE,
            RateDataPoints: ERROR_RATE_X_DATA_POINTS);
    }

    private AnalysisTabRenderContext BuildTabRenderContext(AnalysisFrame frame)
    {
        return new AnalysisTabRenderContext(
            SampleRate: FrameSampleRate(frame),
            ScopeScale: Math.Max(1, (int)mViewModel.ScopeScale),
            ReviewCursorTimeS: mViewModel.ReviewCursorTimeS);
    }

    /// <summary>
    /// Rate the frame's analysis run was configured with. Falls back to the current
    /// UI rate for frames that predate the SampleRate field. Using the frame's own
    /// rate keeps the final playback/sim frames correct after the device rate has
    /// already been restored.
    /// </summary>
    private int FrameSampleRate(AnalysisFrame frame)
    {
        return frame.SampleRate > 0 ? frame.SampleRate : mCurrentSamplesPerSecond;
    }

    // "TimeGrapher v{Major}.{Minor}.{Build}" from the assembly version (set in Directory.Build.props).
    private static string BuildAppTitle()
    {
        System.Version? v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "TimeGrapher" : $"TimeGrapher v{v.Major}.{v.Minor}.{v.Build}";
    }

    private string ActiveInfoTabId()
    {
        if (GraphicsTabWidget.SelectedItem is TabItem { Tag: string tabId } &&
            InfoTabCatalog.TryGet(tabId, out _))
        {
            return tabId;
        }

        return InfoTabCatalog.RateScopeTabId;
    }

    private int ActiveInfoTabRefreshIntervalMs()
    {
        return InfoTabCatalog.Get(ActiveInfoTabId()).RefreshIntervalMs;
    }

    private static int EffectivePixelWidth(Control control, int fallback)
    {
        double value = control.Bounds.Width > 0 ? control.Bounds.Width : control.Width;
        return Math.Max(1, (int)Math.Round(double.IsNaN(value) || value <= 0 ? fallback : value));
    }

    private static int EffectivePixelHeight(Control control, int fallback)
    {
        double value = control.Bounds.Height > 0 ? control.Bounds.Height : control.Height;
        return Math.Max(1, (int)Math.Round(double.IsNaN(value) || value <= 0 ? fallback : value));
    }
}
