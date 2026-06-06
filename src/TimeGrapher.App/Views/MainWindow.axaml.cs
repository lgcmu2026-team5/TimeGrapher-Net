using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using TimeGrapher.App;
using TimeGrapher.App.Audio;
using TimeGrapher.App.Rendering;
using TimeGrapher.App.Services;
using TimeGrapher.App.Tabs;
using TimeGrapher.App.ViewModels;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.AudioIo;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Shared;
using TimeGrapher.Core.Sim;

namespace TimeGrapher.App.Views;

public partial class MainWindow : Window
{
    // Mode indices (MainWindow.cpp #define LIVE/PLAYBACK/SIM).
    private const int LIVE = 0;
    private const int PLAYBACK = 1;
    private const int SIM = 2;

    private const int ERROR_RATE_Y_SCALE = 10;
    private const int ERROR_RATE_X_DATA_POINTS = 250;
    private const int DEFAULT_SOUND_IMAGE_WIDTH = 1019;
    private const int DEFAULT_SOUND_IMAGE_HEIGHT = 654;
    private const int WORKER_STOP_TIMEOUT_MS = 2000;
    private const string PLAYBACK_OR_SIM_PCM = "Playback/Sim";

    private const string PREF_NAME_WELSHI = "Welshi USB";
    private const string PREF_NAME_CHINESE_GENERIC = "Chinese Generic USB";

    private enum StopOutcome
    {
        Stopped,
        Stopping,
    }

    private static StopOutcome CombineStopOutcome(StopOutcome left, StopOutcome right)
    {
        if (left == StopOutcome.Stopping || right == StopOutcome.Stopping)
        {
            return StopOutcome.Stopping;
        }

        return StopOutcome.Stopped;
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

    private static readonly string[] ModeStrings =
    {
        "Live",
        "Playback",
        "Sim",
    };

    private static readonly int[] AveragingPeriodList = { 2, 4, 8, 10, 12, 20, 20, 30, 40, 50, 60, 120, 240 };

    // --- Members (mirror MainWindow.h) ---
    private IRecordingWriter? mWavWriter;
    private readonly ITimeGrapherDialogService mDialogs;
    private readonly RecordingSessionService mRecordingSessionService;
    private readonly PlaybackFileService mPlaybackFileService;
    private GraphFrameRenderer mGraphFrameRenderer = null!;
    private AnalysisFrameRouter mFrameRouter = null!;
    private InfoTabRegistry mInfoTabRegistry = null!;
    private MasterAudioBuffer? mRawAudio;
    private AnalysisWorker? mAnalysisWorker;
    private IAudioInputWorker? mInputWorker;
    private readonly int[] mAvalableRates = new int[5];
    private int mNumberofRates;
    private string mCurrentDir;
    private int mCurrentSamplesPerSecond;
    private int mRateBeforePlaybackOrSim;
    private string mDeviceNameBeforePlaybackOrSim = "";
    private double mBackgroundLastFPS;
    private double mBackgroundLastSPF;
    private double mBackgroundLastSPS;
    private double mForegroundLastFPS;
    private double mForegroundLastSPF;
    private double mForegroundLastSPS;
    private ulong mAnalysisSessionId;
    private ulong mRunSessionToken;
    private DateTime mNextAnalysisRenderUtc = DateTime.MinValue;
    private AnalysisFrame? mPendingAnalysisFrame;
    private AnalysisFrame? mLastAnalysisFrame;
    private readonly object mPendingAnalysisFrameLock = new();
    private bool mAnalysisFrameRenderScheduled;
    private ulong mRenderGeneration;
    private ulong mDroppedAnalysisFrames;
    private Action? mInputDataReadyHandler;
    private Action? mInputCompletionDetach;
    private bool mIsClosing;
    private readonly MainWindowViewModel mViewModel;
    private readonly MainWindowSelectionCoordinator mSelectionCoordinator;
    private readonly RunSelectionResolver mRunSelectionResolver;
    private readonly RunCommandService mRunCommandService;

    // Parallel to InputDeviceComboBox items: device number for live devices, -1 for "Playback/Sim".
    private readonly List<int> mInputDeviceNumbers = new();

    public MainWindow()
    {
        InitializeComponent();
        mViewModel = new MainWindowViewModel(StartRunAsync, TogglePauseRun, StopRun, LoadAudioDevices);
        mSelectionCoordinator = new MainWindowSelectionCoordinator(
            mViewModel,
            new MainWindowSelectionOperations(this),
            new MainWindowSelectionOptions(
                ModeStrings[LIVE],
                ModeStrings[PLAYBACK],
                PLAYBACK_OR_SIM_PCM,
                PreferredAudioDevices,
                AveragingPeriodList));
        mRunSelectionResolver = new RunSelectionResolver(
            mViewModel,
            AveragingPeriodList,
            BphCatalog.ManualAutoBph,
            BphCatalog.ManualBph);
        mDialogs = new MainWindowDialogService(this);
        mRecordingSessionService = new RecordingSessionService(mDialogs, new QueuedRecordingWriterFactory());
        mPlaybackFileService = new PlaybackFileService(mDialogs);
        mRunCommandService = new RunCommandService(mViewModel, new RunCommandOperations(this));
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
        mBackgroundLastFPS = 0.0;
        mBackgroundLastSPF = 0.0;
        mBackgroundLastSPS = 0.0;

        Title = "TimeGrapher";

        // Results->setAlignment(Qt::AlignHCenter); set in XAML.
        mInfoTabRegistry = InfoTabRegistry.FromCatalog(GraphicsTabWidget, FontFamily.Name);
        mGraphFrameRenderer = new GraphFrameRenderer(mInfoTabRegistry.Consumers, Results);
        mFrameRouter = mInfoTabRegistry.CreateRouter();

        // Wire events (Qt auto-connected on_* slots + explicit connect()s).
        mViewModel.PropertyChanged += mSelectionCoordinator.OnViewModelPropertyChanged;
        GraphicsTabWidget.SelectionChanged += OnGraphicsTabSelectionChanged;

        LoadBPH();
        LoadSimBPH();
        LoadAudioDevices();
        mGraphFrameRenderer.Initialize(BuildTabResetContext());
        LoadAverageingPeriod();
        Results.Text = "RATE ------ s/d   AMPLITUDE ---   BEAT ERROR ---- ms   BEAT ----- bph";
        SetGuiStopMode();

        Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // ~MainWindow: StopAnalysisThread(); plus stop any running input worker.
        mIsClosing = true;
        mViewModel.PropertyChanged -= mSelectionCoordinator.OnViewModelPropertyChanged;
        InvalidateRunSession();
        StopInputWorker("Input");
        StopAnalysisThread();
        AudioCloseCheck();
    }

    private ulong BeginRunSession()
    {
        unchecked
        {
            mRunSessionToken++;
            if (mRunSessionToken == 0)
            {
                mRunSessionToken = 1;
            }
            return mRunSessionToken;
        }
    }

    private void InvalidateRunSession()
    {
        _ = BeginRunSession();
    }

    private void ConfigureSoundCard()
    {
        LiveAudioBackend.ConfigurePreferredInput();
    }

    private void LoadAudioDevices()
    {
        IReadOnlyList<LiveAudioDevice> inputDevices = Array.Empty<LiveAudioDevice>();
        if (LiveAudioBackend.CanCapture)
        {
            try
            {
                inputDevices = LiveAudioBackend.EnumerateInputDevices();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Live audio device enumeration failed: " + ex.Message);
            }
        }
        else
        {
            Console.Error.WriteLine("Live audio capture is not available on this platform; using Playback/Sim only.");
        }

        var deviceNames = new List<string>();
        mInputDeviceNumbers.Clear();

        int renameLen = RenameAudioDevices.Length;
        for (int dev = 0; dev < inputDevices.Count; dev++)
        {
            LiveAudioDevice device = inputDevices[dev];
            string description = device.Name;
            for (int i = 0; i < renameLen; i++)
            {
                if (description.Contains(RenameAudioDevices[i][0], StringComparison.Ordinal))
                {
                    description = RenameAudioDevices[i][1];
                    break;
                }
            }
            deviceNames.Add(description);
            mInputDeviceNumbers.Add(device.Number);
        }

        deviceNames.Add(PLAYBACK_OR_SIM_PCM);
        mInputDeviceNumbers.Add(-1);
        using (mSelectionCoordinator.SuppressEvents())
        {
            mViewModel.SetInputDeviceNames(deviceNames);
        }

        int len = PreferredAudioDevices.Length;
        int selected = -1;
        for (int i = 0; i < len; i++)
        {
            int index = MainWindowSelectionCoordinator.FindText(
                mViewModel.InputDeviceNames,
                PreferredAudioDevices[i],
                matchContains: true);
            if (index != -1) // -1 means the text was not found
            {
                selected = index;
                break;
            }
        }

        // setCurrentIndex(index) triggers on_InputDeviceComboBox_currentIndexChanged once.
        // (Avalonia ComboBox does not auto-select on add, unlike Qt; explicitly select to
        //  reach the same final state where PopulateSampleRates has run for the chosen device.)
        if (selected != -1)
        {
            mSelectionCoordinator.SetSelectedInputDeviceIndex(selected, forceChanged: true);
        }
        else if (mViewModel.InputDeviceNames.Count > 0)
        {
            // No preferred device matched: fall back to index 0 (Qt's auto-selected first item).
            if (mViewModel.SelectedInputDeviceIndex == 0)
                mSelectionCoordinator.SetSelectedInputDeviceIndex(0, forceChanged: true); // re-run logic; index unchanged
            else
                mSelectionCoordinator.SetSelectedInputDeviceIndex(0);
        }

        LoadMode();
    }

    private void LoadAverageingPeriod()
    {
        int length = AveragingPeriodList.Length;
        var labels = new List<string>(length);
        for (int i = 0; i < length; i++)
        {
            string name = AveragingPeriodList[i].ToString(CultureInfo.InvariantCulture) + "s";
            labels.Add(name);
        }
        using (mSelectionCoordinator.SuppressEvents())
        {
            mViewModel.SetAveragingPeriodLabels(labels);
            mViewModel.SelectedAveragingPeriodIndex = -1;
        }

        int defaultIndex = mRunSelectionResolver.DefaultAveragingPeriodIndex;
        mViewModel.SelectedAveragingPeriodIndex = defaultIndex == -1 ? 0 : defaultIndex;
    }

    private void LoadBPH()
    {
        IReadOnlyList<int> manualAutoBph = BphCatalog.ManualAutoBph;
        int length = manualAutoBph.Count;
        var labels = new List<string>(length);
        for (int i = 0; i < length; i++)
        {
            int bph = manualAutoBph[i];
            string name = bph != 0
                ? bph.ToString(CultureInfo.InvariantCulture)
                : "Auto BPH";
            labels.Add(name);
        }
        using (mSelectionCoordinator.SuppressEvents())
        {
            mViewModel.SetBphLabels(labels);
            mViewModel.SelectedBphIndex = -1;
        }
        mViewModel.SelectedBphIndex = 0; // Auto
    }

    private void LoadSimBPH()
    {
        IReadOnlyList<int> simBph = BphCatalog.ManualBph;
        int length = simBph.Count;
        var labels = new List<string>(length);
        for (int i = 0; i < length; i++)
        {
            string name = simBph[i].ToString(CultureInfo.InvariantCulture);
            labels.Add(name);
        }
        using (mSelectionCoordinator.SuppressEvents())
        {
            mViewModel.SetSimBphLabels(labels);
            mViewModel.SelectedSimBphIndex = -1;
        }
        int defaultIndex = mRunSelectionResolver.DefaultSimulationBphIndex;
        mViewModel.SelectedSimBphIndex = defaultIndex == -1 ? 0 : defaultIndex;
    }

    private void LoadMode()
    {
        int start = 0;
        int len = ModeStrings.Length;

        var labels = new List<string>(len);

        if (mViewModel.InputDeviceNames.Count == 1) // Skip over Live (only "Playback/Sim" present)
        {
            start++;
        }
        for (int i = start; i < len; i++)
        {
            labels.Add(ModeStrings[i]);
        }
        using (mSelectionCoordinator.SuppressEvents())
        {
            mViewModel.SetModeNames(labels);
            mViewModel.SelectedModeIndex = -1;
        }
        mSelectionCoordinator.SetSelectedModeIndex(0);
    }

    // --- Worker lifecycle ---

    private void StartAudioThread()
    {
        int deviceNumber = CurrentInputDeviceNumber();
        if (deviceNumber < 0)
        {
            throw new InvalidOperationException("No live audio device is selected.");
        }

        ulong runSessionToken = BeginRunSession();
        StopAnalysisThread();
        Reset();

        // Recreate the master buffer at the current sample rate.
        mRawAudio = new MasterAudioBuffer(mCurrentSamplesPerSecond);
        StartAnalysisThread();

        ILiveAudioWorker audioWorker = LiveAudioBackend.CreateWorker(mRawAudio);
        AttachInputWorker(audioWorker, runSessionToken);
        audioWorker.Start(deviceNumber, mCurrentSamplesPerSecond, (float)(mViewModel.Gain / 1000.0));
    }

    private StopOutcome StopAudioThread()
    {
        // LocalStopAudio -> StopAudioRecording.
        return StopInputWorker("Audio");
    }

    private StopOutcome StopInputWorker(string workerName)
    {
        IAudioInputWorker? worker = mInputWorker;
        if (worker != null)
        {
            if (mInputDataReadyHandler != null)
            {
                worker.DataReady -= mInputDataReadyHandler;
                mInputDataReadyHandler = null;
            }

            if (!worker.TryStop(TimeSpan.FromMilliseconds(WORKER_STOP_TIMEOUT_MS)))
            {
                mViewModel.StatusText = workerName + " worker did not stop within timeout";
                return StopOutcome.Stopping;
            }

            mInputCompletionDetach?.Invoke();
            mInputCompletionDetach = null;
            worker.Dispose();
            mInputWorker = null;
        }

        return StopOutcome.Stopped;
    }

    private void StartPlaybackThread(string fileName)
    {
        ulong runSessionToken = BeginRunSession();
        StopAnalysisThread();
        Reset();

        mRawAudio = new MasterAudioBuffer(mCurrentSamplesPerSecond);
        StartAnalysisThread();

        var playbackWorker = new PlaybackWorker(mRawAudio, mCurrentSamplesPerSecond);
        Action<PlaybackCompletionReason> doneHandler = reason => OnPlaybackDoneReadingFile(runSessionToken, reason);
        playbackWorker.DoneReadingFile += doneHandler;
        AttachInputWorker(playbackWorker, runSessionToken, () => playbackWorker.DoneReadingFile -= doneHandler);
        if (!playbackWorker.Start(fileName))
        {
            throw new InvalidOperationException("Playback worker is already running.");
        }
    }

    private void StartSimThread(WatchSynthStreamConfig cfg)
    {
        ulong runSessionToken = BeginRunSession();
        StopAnalysisThread();
        Reset();

        mRawAudio = new MasterAudioBuffer(mCurrentSamplesPerSecond);
        StartAnalysisThread();

        var simWorker = new SimWorker(mRawAudio, mCurrentSamplesPerSecond);
        Action<SimCompletionReason> doneHandler = reason => OnSimDone(runSessionToken, reason);
        simWorker.SimDone += doneHandler;
        AttachInputWorker(simWorker, runSessionToken, () => simWorker.SimDone -= doneHandler);
        if (!simWorker.Start(cfg))
        {
            throw new InvalidOperationException("Sim worker is already running.");
        }
    }

    private StopOutcome StopPlaybackThread()
    {
        // requestInterruption(): cancel; the worker reports completion via DoneReadingFile,
        // but on_StopPushButton_clicked also calls StopAnalysisThread()/AudioCloseCheck() directly.
        return StopInputWorker("Playback");
    }

    private StopOutcome StopSimThread()
    {
        return StopInputWorker("Sim");
    }

    private void StartAnalysisThread()
    {
        mAnalysisSessionId++;

        AnalysisWorker.Config analysisConfig = BuildRunSettings().ToWorkerConfig(mAnalysisSessionId, mWavWriter);

        mAnalysisWorker = new AnalysisWorker(mRawAudio!, analysisConfig);
        mAnalysisWorker.AnalysisFrameReady += OnAnalysisFrameReady;
        mAnalysisWorker.Start();
    }

    private StopOutcome StopAnalysisThread(bool completeInput = false)
    {
        if (mAnalysisWorker != null)
        {
            bool stopped = completeInput
                ? mAnalysisWorker.CompleteInput(TimeSpan.FromMilliseconds(WORKER_STOP_TIMEOUT_MS))
                : mAnalysisWorker.TryStop(TimeSpan.FromMilliseconds(WORKER_STOP_TIMEOUT_MS));
            if (stopped)
            {
                mAnalysisWorker.AnalysisFrameReady -= OnAnalysisFrameReady;
                mAnalysisWorker.Dispose();
                mAnalysisWorker = null;
                if (completeInput)
                {
                    mNextAnalysisRenderUtc = DateTime.MinValue;
                }
                else
                {
                    mAnalysisSessionId++;
                    ClearPendingAnalysisFrames();
                }
            }
            else
            {
                mViewModel.StatusText = "Analysis worker did not stop within timeout";
                return StopOutcome.Stopping;
            }
        }
        else
        {
            ClearPendingAnalysisFrames();
        }
        return StopOutcome.Stopped;
    }

    // Input worker DataReady (any thread) -> analysis worker. Safe from any thread.
    private Action CreateDataReadyHandler(ulong runSessionToken)
    {
        return () =>
        {
            if (runSessionToken == mRunSessionToken)
            {
                mAnalysisWorker?.NotifyDataReady();
            }
        };
    }

    private void AttachInputWorker(
        IAudioInputWorker worker,
        ulong runSessionToken,
        Action? detachCompletion = null)
    {
        mInputWorker = worker;
        mInputCompletionDetach = detachCompletion;
        mInputDataReadyHandler = CreateDataReadyHandler(runSessionToken);
        worker.DataReady += mInputDataReadyHandler;
    }

    private void OnPlaybackDoneReadingFile(ulong runSessionToken, PlaybackCompletionReason reason)
    {
        // PlaybackDoneReadingFile fires on the playback thread; marshal to UI thread.
        Dispatcher.UIThread.Post(() => HandlePlaybackDoneReadingFile(runSessionToken, reason));
    }

    private void OnSimDone(ulong runSessionToken, SimCompletionReason reason)
    {
        Dispatcher.UIThread.Post(() => HandleSimDone(runSessionToken, reason));
    }

    private void HandlePlaybackDoneReadingFile(ulong runSessionToken, PlaybackCompletionReason reason)
    {
        CompletePlaybackOrSimulationRun(
            runSessionToken,
            shouldRestoreAudioState: CurrentModeText() == ModeStrings[PLAYBACK],
            stopInputWorker: () => StopInputWorker("Playback"),
            failureStatus: "Playback failed",
            failed: reason == PlaybackCompletionReason.Failed);
    }

    private void HandleSimDone(ulong runSessionToken, SimCompletionReason reason)
    {
        CompletePlaybackOrSimulationRun(
            runSessionToken,
            shouldRestoreAudioState: CurrentModeText() == ModeStrings[SIM],
            stopInputWorker: () => StopInputWorker("Sim"),
            failureStatus: "Simulation failed",
            failed: reason == SimCompletionReason.Failed);
    }

    private void CompletePlaybackOrSimulationRun(
        ulong runSessionToken,
        bool shouldRestoreAudioState,
        Func<StopOutcome> stopInputWorker,
        string failureStatus,
        bool failed)
    {
        if (runSessionToken != mRunSessionToken)
        {
            return;
        }
        InvalidateRunSession();
        SetGuiStoppingMode();
        if (shouldRestoreAudioState)
        {
            RestorePlaybackOrSimulationAudioState();
        }
        StopOutcome outcome = stopInputWorker();
        outcome = CombineStopOutcome(outcome, StopAnalysisThread(completeInput: true));
        bool audioClosed = outcome == StopOutcome.Stopped && AudioCloseCheck();
        if (outcome != StopOutcome.Stopped || !audioClosed)
        {
            SetGuiStoppingMode();
            return;
        }
        SetGuiStopMode();
        mViewModel.StatusText = failed ? failureStatus : "Stopped";
    }

    // AnalysisFrameReady fires on the analysis thread; marshal to UI thread.
    private void OnAnalysisFrameReady(AnalysisFrame frame)
    {
        ulong generation;
        lock (mPendingAnalysisFrameLock)
        {
            if (mPendingAnalysisFrame != null)
            {
                mDroppedAnalysisFrames++;
            }
            mPendingAnalysisFrame = frame;
            generation = mRenderGeneration;
            if (mAnalysisFrameRenderScheduled)
            {
                return;
            }
            mAnalysisFrameRenderScheduled = true;
        }
        Dispatcher.UIThread.Post(() => ProcessPendingAnalysisFrame(generation));
    }

    private async Task DelayPendingAnalysisFrameRender(TimeSpan delay, ulong generation)
    {
        await Task.Delay(delay);
        Dispatcher.UIThread.Post(() => ProcessPendingAnalysisFrame(generation));
    }

    private void ProcessPendingAnalysisFrame(ulong generation)
    {
        if (generation != mRenderGeneration)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (now < mNextAnalysisRenderUtc)
        {
            _ = DelayPendingAnalysisFrameRender(mNextAnalysisRenderUtc - now, generation);
            return;
        }

        AnalysisFrame? frame;
        ulong droppedFrames;
        lock (mPendingAnalysisFrameLock)
        {
            frame = mPendingAnalysisFrame;
            mPendingAnalysisFrame = null;
            droppedFrames = mDroppedAnalysisFrames;
            mDroppedAnalysisFrames = 0;
        }

        if (frame != null)
        {
            HandleAnalysisFrame(frame, droppedFrames);
            mNextAnalysisRenderUtc = DateTime.UtcNow.AddMilliseconds(ActiveInfoTabRefreshIntervalMs());
        }

        lock (mPendingAnalysisFrameLock)
        {
            if (mPendingAnalysisFrame != null)
            {
                _ = DelayPendingAnalysisFrameRender(
                    TimeSpan.FromMilliseconds(ActiveInfoTabRefreshIntervalMs()),
                    generation);
            }
            else
            {
                mAnalysisFrameRenderScheduled = false;
            }
        }
    }

    private void ClearPendingAnalysisFrames()
    {
        lock (mPendingAnalysisFrameLock)
        {
            mPendingAnalysisFrame = null;
            mDroppedAnalysisFrames = 0;
            mAnalysisFrameRenderScheduled = false;
            mRenderGeneration++;
        }
        mLastAnalysisFrame = null;
        mNextAnalysisRenderUtc = DateTime.MinValue;
    }

    private void HandleAnalysisFrame(AnalysisFrame frame, ulong droppedFrames)
    {
        if (frame.SessionId != mAnalysisSessionId)
        {
            return;
        }

        mLastAnalysisFrame = frame;
        mGraphFrameRenderer.UpdateResults(frame);
        mFrameRouter.Route(frame, ActiveInfoTabId(), BuildTabRenderContext());

        bool statusUpdated = false;
        if ((mBackgroundLastFPS != frame.BackgroundFps) ||
            (mBackgroundLastSPS != frame.BackgroundSps) ||
            (mBackgroundLastSPF != frame.BackgroundSpf))
        {
            mBackgroundLastFPS = frame.BackgroundFps;
            mBackgroundLastSPS = frame.BackgroundSps;
            mBackgroundLastSPF = frame.BackgroundSpf;
            statusUpdated = true;
        }
        if (frame.ForegroundStatsUpdated &&
            ((mForegroundLastFPS != frame.ForegroundFps) ||
             (mForegroundLastSPS != frame.ForegroundSps) ||
             (mForegroundLastSPF != frame.ForegroundSpf)))
        {
            mForegroundLastFPS = frame.ForegroundFps;
            mForegroundLastSPS = frame.ForegroundSps;
            mForegroundLastSPF = frame.ForegroundSpf;
            statusUpdated = true;
        }
        if (statusUpdated)
        {
            mViewModel.StatusText = string.Format(
                CultureInfo.InvariantCulture,
                "Backgroud Audio Thread Average - FPS:{0}, SPS:{1}, SPF: {2} Foregroud Audio Handler Average - FPS:{3}, SPS:{4}, SPF: {5}",
                mBackgroundLastFPS.ToString("F0", CultureInfo.InvariantCulture),
                mBackgroundLastSPS.ToString("F0", CultureInfo.InvariantCulture),
                mBackgroundLastSPF.ToString("F0", CultureInfo.InvariantCulture),
                mForegroundLastFPS.ToString("F0", CultureInfo.InvariantCulture),
                mForegroundLastSPS.ToString("F0", CultureInfo.InvariantCulture),
                mForegroundLastSPF.ToString("F0", CultureInfo.InvariantCulture));
        }
        if (frame.InputOverrun)
        {
            mViewModel.StatusText = "Audio input overrun: dropped " +
                                    frame.InputSamplesDropped.ToString(CultureInfo.InvariantCulture) +
                                    " samples before analysis";
        }
        else if (frame.AnalysisLagSamples > (ulong)Math.Max(1, mCurrentSamplesPerSecond / 4))
        {
            double lagMs = frame.AnalysisLagSamples * 1000.0 / Math.Max(1, mCurrentSamplesPerSecond);
            mViewModel.StatusText = string.Format(
                CultureInfo.InvariantCulture,
                "Analysis lag: {0:F0} ms ({1} samples), processing {2:F1} ms",
                lagMs,
                frame.AnalysisLagSamples,
                frame.ProcessingElapsedMs);
        }
        else if (droppedFrames != 0)
        {
            Console.Error.WriteLine("UI render coalesced " +
                                    droppedFrames.ToString(CultureInfo.InvariantCulture) +
                                    " analysis frame(s)");
        }
    }

    private void Reset()
    {
        mGraphFrameRenderer.Reset(BuildTabResetContext());

        mBackgroundLastFPS = 0.0;
        mBackgroundLastSPF = 0.0;
        mBackgroundLastSPS = 0.0;
        mForegroundLastFPS = 0.0;
        mForegroundLastSPF = 0.0;
        mForegroundLastSPS = 0.0;
    }

    private async Task<bool> RecordSessionCheck()
    {
        RecordingSessionStartResult result = await mRecordingSessionService.TryStartAsync(mCurrentSamplesPerSecond);
        if (result.Writer != null)
        {
            mWavWriter = result.Writer;
        }

        return result.ShouldContinue;
    }

    private bool AudioCloseCheck()
    {
        if (mWavWriter != null)
        {
            ulong droppedBlocks = mWavWriter.DroppedBlocks;
            bool closed = mWavWriter.Close();
            if (!closed)
            {
                mViewModel.StatusText = "Failed to close WAV recording cleanly";
                return false;
            }

            mWavWriter.Dispose();
            mWavWriter = null;
            if (droppedBlocks != 0)
            {
                mViewModel.StatusText = "WAV recording dropped " +
                                     droppedBlocks.ToString(CultureInfo.InvariantCulture) +
                                     " block(s)";
            }
        }

        return true;
    }

    private void PopulateSampleRates(int deviceNumber)
    {
        IReadOnlyList<int> standardRates = AudioSampleRates.Standard;

        mNumberofRates = 0;
        var labels = new List<string>(standardRates.Count);

        if (deviceNumber < 0)
        {
            // Audio device is null / "Playback/Sim": offer the standard rates.
            foreach (int rate in standardRates)
            {
                labels.Add(rate.ToString(CultureInfo.InvariantCulture) + " Hz");
                mAvalableRates[mNumberofRates] = rate;
                mNumberofRates++;
            }
        }
        else
        {
            IReadOnlyList<int> supported = LiveAudioBackend.GetCandidateSampleRates(deviceNumber);
            // Capture backend startup remains the authoritative validation point.
            foreach (int rate in standardRates)
            {
                if (supported.Contains(rate) && mNumberofRates < mAvalableRates.Length)
                {
                    labels.Add(rate.ToString(CultureInfo.InvariantCulture) + " Hz");
                    mAvalableRates[mNumberofRates] = rate;
                    mNumberofRates++;
                }
            }
        }
        using (mSelectionCoordinator.SuppressEvents())
        {
            mViewModel.SetSampleRateLabels(labels);
            mViewModel.SelectedSampleRateIndex = -1;
        }
        if (mViewModel.SampleRateLabels.Count > 0)
        {
            mSelectionCoordinator.SetSelectedSampleRateIndex(0);
        }
    }

    private bool SetAudioRate(int rate)
    {
        return mSelectionCoordinator.SetAudioRate(rate);
    }

    private bool SetAudioDevice(string name)
    {
        return mSelectionCoordinator.SetAudioDevice(name);
    }

    private void RestorePlaybackOrSimulationAudioState()
    {
        SetAudioDevice(mDeviceNameBeforePlaybackOrSim);
        SetAudioRate(mRateBeforePlaybackOrSim);
    }

    private void GetAudioRate(out int rate)
    {
        rate = mCurrentSamplesPerSecond;
    }

    private void GetAudioDevice(out string name)
    {
        name = CurrentInputDeviceText();
    }

    private void SetGuiRunMode()
    {
        mViewModel.SetRunning();
    }

    private void SetGuiStartingMode()
    {
        mViewModel.SetStarting();
    }

    private void SetGuiStoppingMode()
    {
        mViewModel.SetStopping();
    }

    private void SetGuiStopMode()
    {
        mViewModel.SetModeAllowsSampleRate(CurrentModeText() != ModeStrings[PLAYBACK]);
        mViewModel.SetStopped();
    }

    private async Task<bool> LiveStart()
    {
        if (!await RecordSessionCheck()) return false;
        try
        {
            StartAudioThread();
        }
        catch (Exception ex)
        {
            InvalidateRunSession();
            StopInputWorker("Audio");
            StopAnalysisThread();
            AudioCloseCheck();
            mViewModel.StatusText = "Failed to start live audio";
            await mDialogs.ShowErrorAsync("Error", "Failed to start live audio: " + ex.Message);
            return false;
        }
        SetGuiRunMode();
        mViewModel.StatusText = "Running";
        return true;
    }

    private async Task<bool> PlaybackStart()
    {
        PlaybackFileSelectionResult selection = await mPlaybackFileService.SelectPlaybackFileAsync(mCurrentDir);
        if (!selection.Selected || selection.FilePath == null)
        {
            if (!string.IsNullOrEmpty(selection.StatusMessage))
            {
                mViewModel.StatusText = selection.StatusMessage;
            }
            return false;
        }

        mCurrentDir = selection.CurrentDirectory;
        GetAudioRate(out mRateBeforePlaybackOrSim);
        GetAudioDevice(out mDeviceNameBeforePlaybackOrSim);
        if (!SetAudioDevice(PLAYBACK_OR_SIM_PCM))
        {
            Console.Error.WriteLine("SetAudioDevice Failed");
        }
        if (!SetAudioRate(selection.SampleRate))
        {
            Console.Error.WriteLine("SetAudioRate Failed");
            return false;
        }
        if (!await RecordSessionCheck())
        {
            RestorePlaybackOrSimulationAudioState();
            return false;
        }
        StartPlaybackThread(selection.FilePath);
        SetGuiRunMode();
        mViewModel.StatusText = "Running";
        return true;
    }

    private async Task<bool> SimStart()
    {
        // RealisticCheckBox -> realistic config; otherwise clean config
        // (MainWindow.cpp: watch_synth_stream_realistic_config / watch_synth_stream_clean_config).
        WatchSynthStreamConfig cfg = mViewModel.Realistic
            ? WatchSynthStreamConfig.Realistic()
            : WatchSynthStreamConfig.Clean();

        SimulationSelection selection = mRunSelectionResolver.GetSimulationSelection(mAvalableRates, mNumberofRates);
        cfg.Bph = selection.Bph;
        cfg.SampleRateHz = (uint)selection.SampleRate;
        cfg.BeatErrorMs = -(double)mViewModel.SimBeatError;
        cfg.PcmPeakAmplitude = 0.40; // normalized float PCM digital output level
        cfg.WatchAmplitudeDegrees = (double)mViewModel.SimAmplitude;
        cfg.LiftAngleDegrees = (double)mViewModel.LiftAngle;
        cfg.RateErrorSPerDay = (double)mViewModel.SimErrorRate;

        if (!await RecordSessionCheck()) return false;
        GetAudioRate(out mRateBeforePlaybackOrSim);
        GetAudioDevice(out mDeviceNameBeforePlaybackOrSim);
        if (!SetAudioDevice(PLAYBACK_OR_SIM_PCM))
        {
            Console.Error.WriteLine("SetAudioDevice Failed");
        }
        if (!SetAudioRate(mRateBeforePlaybackOrSim))
        {
            Console.Error.WriteLine("SetAudioRate Failed");
        }
        StartSimThread(cfg);
        SetGuiRunMode();
        mViewModel.StatusText = "Running";
        return true;
    }

    // --- Event handlers (Qt on_* slots) ---

    private void OnGraphicsTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        mNextAnalysisRenderUtc = DateTime.MinValue;
        lock (mPendingAnalysisFrameLock)
        {
            mPendingAnalysisFrame = null;
            mDroppedAnalysisFrames = 0;
            mAnalysisFrameRenderScheduled = false;
            mRenderGeneration++;
        }

        AnalysisFrame? frame = mLastAnalysisFrame;
        if (frame != null && frame.SessionId == mAnalysisSessionId)
        {
            mGraphFrameRenderer.UpdateResults(frame);
            mFrameRouter.Route(frame, ActiveInfoTabId(), BuildTabRenderContext());
        }
    }

    private async Task StartRunAsync()
    {
        await mRunCommandService.StartAsync();
    }

    private void TogglePauseRun()
    {
        mRunCommandService.TogglePause();
    }

    private void SetWorkersPaused(bool paused)
    {
        mInputWorker?.SetPaused(paused);
    }

    private void StopRun()
    {
        mRunCommandService.Stop();
    }

    // --- Helpers ---

    private int CurrentInputDeviceNumber()
    {
        return mSelectionCoordinator.CurrentInputDeviceNumber;
    }

    private string CurrentInputDeviceText()
    {
        return mSelectionCoordinator.CurrentInputDeviceText;
    }

    private string CurrentModeText()
    {
        return mSelectionCoordinator.CurrentModeText;
    }

    private static double ParseDouble(string? text)
    {
        // QString::toDouble returns 0.0 on failure.
        if (string.IsNullOrEmpty(text)) return 0.0;
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : 0.0;
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
            ScopeSnapshotPointBudget: InfoTabCatalog.ScopeTargetPointBudget);
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

    private AnalysisTabRenderContext BuildTabRenderContext()
    {
        return new AnalysisTabRenderContext(
            SampleRate: mCurrentSamplesPerSecond,
            ScopeScale: Math.Max(1, (int)mViewModel.ScopeScale));
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

    private sealed class MainWindowSelectionOperations : IMainWindowSelectionOperations
    {
        private readonly MainWindow _owner;

        public MainWindowSelectionOperations(MainWindow owner)
        {
            _owner = owner;
        }

        public IReadOnlyList<int> InputDeviceNumbers => _owner.mInputDeviceNumbers;

        public int AvailableSampleRateCount => _owner.mNumberofRates;

        public int GetAvailableSampleRate(int index)
        {
            return _owner.mAvalableRates[index];
        }

        public void PopulateSampleRates(int deviceNumber)
        {
            _owner.PopulateSampleRates(deviceNumber);
        }

        public void SetCurrentSampleRate(int sampleRate)
        {
            _owner.mCurrentSamplesPerSecond = sampleRate;
        }

        public void SetAudioInputVolume(float normalizedVolume)
        {
            if (_owner.mInputWorker is ILiveAudioWorker liveWorker)
            {
                liveWorker.SetVolume(normalizedVolume);
            }
        }
    }

    private sealed class RunCommandOperations : IRunCommandOperations
    {
        private readonly MainWindow _owner;

        public RunCommandOperations(MainWindow owner)
        {
            _owner = owner;
        }

        public bool IsClosing => _owner.mIsClosing;

        public bool HasActiveWorker => _owner.mInputWorker != null;

        public RunCommandMode CurrentMode
        {
            get
            {
                string mode = _owner.CurrentModeText();
                if (mode == ModeStrings[LIVE])
                {
                    return RunCommandMode.Live;
                }

                if (mode == ModeStrings[PLAYBACK])
                {
                    return RunCommandMode.Playback;
                }

                if (mode == ModeStrings[SIM])
                {
                    return RunCommandMode.Simulation;
                }

                return RunCommandMode.Unknown;
            }
        }

        public void ConfigureLiveAudio()
        {
            _owner.ConfigureSoundCard();
        }

        public Task<bool> StartLiveAsync()
        {
            return _owner.LiveStart();
        }

        public Task<bool> StartPlaybackAsync()
        {
            return _owner.PlaybackStart();
        }

        public Task<bool> StartSimulationAsync()
        {
            return _owner.SimStart();
        }

        public void SetWorkersPaused(bool paused)
        {
            _owner.SetWorkersPaused(paused);
        }

        public void CleanupFailedStart()
        {
            _owner.InvalidateRunSession();
            _owner.StopInputWorker("Input");
            _owner.StopAnalysisThread();
            _owner.AudioCloseCheck();
        }

        public Task ShowStartFailureAsync(Exception exception)
        {
            return _owner.mDialogs.ShowErrorAsync("Error", "Failed to start: " + exception.Message);
        }

        public RunCommandStopOutcome StopLive()
        {
            StopOutcome outcome = CombineStopOutcome(_owner.StopAudioThread(), _owner.StopAnalysisThread());
            return MapStopOutcome(outcome);
        }

        public RunCommandStopOutcome StopPlayback()
        {
            StopOutcome outcome = CombineStopOutcome(_owner.StopPlaybackThread(), _owner.StopAnalysisThread());
            return MapStopOutcome(outcome);
        }

        public RunCommandStopOutcome StopSimulation()
        {
            StopOutcome outcome = CombineStopOutcome(_owner.StopSimThread(), _owner.StopAnalysisThread());
            return MapStopOutcome(outcome);
        }

        public bool CloseAudio()
        {
            return _owner.AudioCloseCheck();
        }

        public void InvalidateRunSession()
        {
            _owner.InvalidateRunSession();
        }

        public void RestorePlaybackOrSimulationAudioState()
        {
            _owner.RestorePlaybackOrSimulationAudioState();
        }

        private static RunCommandStopOutcome MapStopOutcome(StopOutcome outcome)
        {
            return outcome == StopOutcome.Stopped
                ? RunCommandStopOutcome.Stopped
                : RunCommandStopOutcome.Stopping;
        }
    }

}
