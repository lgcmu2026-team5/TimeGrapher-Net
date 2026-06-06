using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

using TimeGrapher.App;
using TimeGrapher.App.Rendering;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.AudioIo;
using TimeGrapher.Core.Shared;
using TimeGrapher.Core.Sim;
using TimeGrapher.Platform.WindowsAudio;

namespace TimeGrapher.App.Views;

public partial class MainWindow : Window
{
    // Mode indices (MainWindow.cpp #define LIVE/PLAYBACK/SIM).
    private const int LIVE = 0;
    private const int PLAYBACK = 1;
    private const int SIM = 2;

    private const int ERROR_RATE_Y_SCALE = 10;
    private const int ERROR_RATE_X_DATA_POINTS = 250;
    private const int UI_RENDER_INTERVAL_MS = InfoTabCatalog.DefaultUiRefreshIntervalMs;
    private const int DEFAULT_SOUND_IMAGE_WIDTH = 1019;
    private const int DEFAULT_SOUND_IMAGE_HEIGHT = 654;
    private const int WORKER_STOP_TIMEOUT_MS = 2000;

    private static bool WindowsAudioAvailable => OperatingSystem.IsWindows();

    private const string PLAYBACK_OR_SIM_PCM = "Playback/Sim";

    private const string PREF_NAME_WELSHI = "Welshi USB";
    private const string PREF_NAME_CHINESE_GENERIC = "Chinese Generic USB";

    private enum StopOutcome
    {
        Stopped,
        Stopping,
        Failed,
    }

    private static StopOutcome CombineStopOutcome(StopOutcome left, StopOutcome right)
    {
        if (left == StopOutcome.Failed || right == StopOutcome.Failed)
        {
            return StopOutcome.Failed;
        }

        if (left == StopOutcome.Stopping || right == StopOutcome.Stopping)
        {
            return StopOutcome.Stopping;
        }

        return StopOutcome.Stopped;
    }

    // ConfigureSoundCard constants (Windows path).
    private const string WINDOWS_SOUND_ENDPOINT_NAME = "USB PnP Sound Device";
    private const string WINDOWS_SOUND_MIC_NAME = "USB PnP Sound Device";
    private const int WINDOWS_SOUND_MIC_PERCENT_VOLUME = 50;

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

    private static readonly int[] ManualAutoBPH =
    {
        0, // Auto
        3600,  6000,  7200,  7380,  7440,  7800,  9000,  9100, 10800, 11880,
        12000, 12342, 12480, 12600, 13320, 13440, 13500, 14000, 14040, 14160,
        14200, 14280, 14400, 14520, 14580, 14760, 14850, 15000, 15360, 15600,
        16200, 16320, 16800, 17196, 11258, 17280, 17186, 17897, 18000, 18049,
        18514, 19332, 19440, 19800, 20160, 20222, 20944, 21000, 21031, 21306,
        21600, 25200, 28800, 32400, 36000, 43200,
    };

    private static readonly int[] SimBPH =
    {
        3600,  6000,  7200,  7380,  7440,  7800,  9000,  9100, 10800, 11880,
        12000, 12342, 12480, 12600, 13320, 13440, 13500, 14000, 14040, 14160,
        14200, 14280, 14400, 14520, 14580, 14760, 14850, 15000, 15360, 15600,
        16200, 16320, 16800, 17196, 11258, 17280, 17186, 17897, 18000, 18049,
        18514, 19332, 19440, 19800, 20160, 20222, 20944, 21000, 21031, 21306,
        21600, 25200, 28800, 32400, 36000, 43200,
    };

    private static readonly int[] AveragingPeriodList = { 2, 4, 8, 10, 12, 20, 20, 30, 40, 50, 60, 120, 240 };

    // --- Members (mirror MainWindow.h) ---
    private QueuedWavStreamWriter? mWavWriter;
    private GraphFrameRenderer mGraphFrameRenderer = null!;
    private AnalysisFrameRouter mFrameRouter = null!;
    private InfoTabRegistry mInfoTabRegistry = null!;
    private MasterAudioBuffer? mRawAudio;
    private AnalysisWorker? mAnalysisWorker;
    private AudioCaptureWorker? mAudioWorker;
    private PlaybackWorker? mPlaybackWorker;
    private SimWorker? mSimWorker;
    private readonly int[] mAvalableRates = new int[5];
    private int mNumberofRates;
    private double mLiftAngle;
    private int mAveragingPeriod;
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
    private Action<PlaybackCompletionReason>? mPlaybackDoneHandler;
    private Action<SimCompletionReason>? mSimDoneHandler;
    private Action? mAudioDataReadyHandler;
    private Action? mPlaybackDataReadyHandler;
    private Action? mSimDataReadyHandler;
    private bool mStartInProgress;
    private bool mIsClosing;

    // Parallel to InputDeviceComboBox items: device number for live devices, -1 for "Playback/Sim".
    private readonly List<int> mInputDeviceNumbers = new();

    // True while populating combos so programmatic changes don't run user-change logic
    // (Qt connects on_* slots only after setupUi; mirror that by suppressing during init/loads).
    private bool mSuppressEvents;

    public MainWindow()
    {
        InitializeComponent();

        // Default working directory: current dir, then ../../samples if it exists (MainWindow ctor).
        mCurrentDir = Directory.GetCurrentDirectory();
        try
        {
            string samples = Path.GetFullPath(Path.Combine(mCurrentDir, "..", "..", "samples"));
            if (Directory.Exists(samples)) mCurrentDir = samples;
        }
        catch { /* keep current dir */ }

        mCurrentSamplesPerSecond = 48000;
        mLiftAngle = 52;

        mBackgroundLastFPS = 0.0;
        mBackgroundLastSPF = 0.0;
        mBackgroundLastSPS = 0.0;

        Title = "TimeGrapher";

        StopPushButton.IsEnabled = false;

        // Results->setAlignment(Qt::AlignHCenter); set in XAML.
        // LiftAngleSpinBox->setValue(mLiftAngle);
        LiftAngleSpinBox.Value = (decimal)mLiftAngle;

        mInfoTabRegistry = InfoTabRegistry.FromCatalog(GraphicsTabWidget, FontFamily.Name);
        mGraphFrameRenderer = new GraphFrameRenderer(mInfoTabRegistry.Consumers, Results);
        mFrameRouter = mInfoTabRegistry.CreateRouter();

        // Wire events (Qt auto-connected on_* slots + explicit connect()s).
        RefreshPushButton.Click += OnRefreshPushButtonClicked;
        StartPushButton.Click += OnStartPushButtonClicked;
        StopPushButton.Click += OnStopPushButtonClicked;
        MicrophoneHorizontalSlider.PropertyChanged += OnMicrophoneSliderPropertyChanged;
        LiftAngleSpinBox.PropertyChanged += OnLiftAngleSpinBoxPropertyChanged;
        AveragingPeriodComboBox.SelectionChanged += OnAveragingPeriodComboBoxChanged;
        ModeComboBox.SelectionChanged += OnModeComboBoxChanged;
        InputDeviceComboBox.SelectionChanged += OnInputDeviceComboBoxChanged;
        SampleRatesComboBox.SelectionChanged += OnSampleRatesComboBoxChanged;
        GraphicsTabWidget.SelectionChanged += OnGraphicsTabSelectionChanged;

        LoadBPH();
        LoadSimBPH();
        LoadAudioDevices();
        mGraphFrameRenderer.Initialize(BuildTabResetContext());
        LoadAverageingPeriod();
        Results.Text = "RATE ------ s/d   AMPLITUDE ---   BEAT ERROR ---- ms   BEAT ----- bph";

        Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // ~MainWindow: StopAnalysisThread(); plus stop any running input worker.
        mIsClosing = true;
        InvalidateRunSession();
        StopAudioWorker();
        StopPlaybackWorkerImmediate();
        StopSimWorkerImmediate();
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
        if (!WindowsAudioAvailable)
        {
            return;
        }

        // Windows path (Q_OS_WIN). LinuxAudio is not ported yet.
        SystemAudioControl.SetSoundParameters(WINDOWS_SOUND_ENDPOINT_NAME, WINDOWS_SOUND_MIC_NAME, WINDOWS_SOUND_MIC_PERCENT_VOLUME);
    }

    private void LoadAudioDevices()
    {
        mSuppressEvents = true;

        IReadOnlyList<string> inputDevices = Array.Empty<string>();
        if (WindowsAudioAvailable)
        {
            try
            {
                inputDevices = AudioCaptureWorker.EnumerateInputDevices();
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

        InputDeviceComboBox.Items.Clear();
        mInputDeviceNumbers.Clear();

        int renameLen = RenameAudioDevices.Length;
        for (int dev = 0; dev < inputDevices.Count; dev++)
        {
            string description = inputDevices[dev];
            for (int i = 0; i < renameLen; i++)
            {
                if (description.Contains(RenameAudioDevices[i][0], StringComparison.Ordinal))
                {
                    description = RenameAudioDevices[i][1];
                    break;
                }
            }
            InputDeviceComboBox.Items.Add(description);
            mInputDeviceNumbers.Add(dev);
            Console.Error.WriteLine("Device Name - " + description);
        }

        InputDeviceComboBox.Items.Add(PLAYBACK_OR_SIM_PCM);
        mInputDeviceNumbers.Add(-1);
        Console.Error.WriteLine("Device Name - " + PLAYBACK_OR_SIM_PCM);

        int len = PreferredAudioDevices.Length;
        int selected = -1;
        for (int i = 0; i < len; i++)
        {
            int index = FindText(InputDeviceComboBox, PreferredAudioDevices[i], matchContains: true);
            if (index != -1) // -1 means the text was not found
            {
                selected = index;
                break;
            }
        }

        mSuppressEvents = false;

        // setCurrentIndex(index) triggers on_InputDeviceComboBox_currentIndexChanged once.
        // (Avalonia ComboBox does not auto-select on add, unlike Qt; explicitly select to
        //  reach the same final state where PopulateSampleRates has run for the chosen device.)
        if (selected != -1)
        {
            InputDeviceComboBox.SelectedIndex = selected;
        }
        else if (InputDeviceComboBox.ItemCount > 0)
        {
            // No preferred device matched: fall back to index 0 (Qt's auto-selected first item).
            if (InputDeviceComboBox.SelectedIndex == 0)
                OnInputDeviceComboBoxChanged(InputDeviceComboBox, null!); // re-run logic; index unchanged
            else
                InputDeviceComboBox.SelectedIndex = 0;
        }

        LoadMode();
    }

    private void LoadAverageingPeriod()
    {
        mSuppressEvents = true;
        int length = AveragingPeriodList.Length;
        for (int i = 0; i < length; i++)
        {
            string name = AveragingPeriodList[i].ToString(CultureInfo.InvariantCulture) + "s";
            AveragingPeriodComboBox.Items.Add(name);
        }
        mSuppressEvents = false;

        AveragingPeriodComboBox.SelectedIndex = 4; // 20 Seconds (also sets mAveragingPeriod via handler)
    }

    private void LoadBPH()
    {
        mSuppressEvents = true;
        int length = ManualAutoBPH.Length;
        for (int i = 0; i < length; i++)
        {
            string name = ManualAutoBPH[i] != 0
                ? ManualAutoBPH[i].ToString(CultureInfo.InvariantCulture)
                : "Auto BPH";
            BPHComboBox.Items.Add(name);
        }
        mSuppressEvents = false;
        BPHComboBox.SelectedIndex = 0; // Auto
    }

    private void LoadSimBPH()
    {
        mSuppressEvents = true;
        int length = SimBPH.Length;
        for (int i = 0; i < length; i++)
        {
            string name = SimBPH[i].ToString(CultureInfo.InvariantCulture);
            SimBPHComboBox.Items.Add(name);
        }
        mSuppressEvents = false;
        SimBPHComboBox.SelectedIndex = 52;
    }

    private void LoadMode()
    {
        int start = 0;
        int len = ModeStrings.Length;

        mSuppressEvents = true;
        ModeComboBox.Items.Clear();

        if (InputDeviceComboBox.ItemCount == 1) // Skip over Live (only "Playback/Sim" present)
        {
            start++;
        }
        for (int i = start; i < len; i++)
        {
            ModeComboBox.Items.Add(ModeStrings[i]);
        }
        mSuppressEvents = false;
        ModeComboBox.SelectedIndex = 0;
    }

    // --- Worker lifecycle ---

    private void StartAudioThread()
    {
        if (!WindowsAudioAvailable)
        {
            throw new PlatformNotSupportedException("Live audio capture is only implemented for Windows. Use Playback or Sim on this platform.");
        }

        ulong runSessionToken = BeginRunSession();
        int deviceNumber = CurrentInputDeviceNumber();
        StopAnalysisThread();
        Reset();

        // Recreate the master buffer at the current sample rate.
        mRawAudio = new MasterAudioBuffer(mCurrentSamplesPerSecond);
        StartAnalysisThread();

        mAudioWorker = new AudioCaptureWorker(mRawAudio);
        // AudioDataReady -> analysis worker (DataReady is notify-only).
        mAudioDataReadyHandler = CreateDataReadyHandler(runSessionToken);
        mAudioWorker.DataReady += mAudioDataReadyHandler;
        mAudioWorker.Start(deviceNumber, mCurrentSamplesPerSecond, (float)(MicrophoneHorizontalSlider.Value / 1000.0));
    }

    private StopOutcome StopAudioThread()
    {
        // LocalStopAudio -> StopAudioRecording.
        return StopAudioWorker();
    }

    private StopOutcome StopAudioWorker()
    {
        if (mAudioWorker != null)
        {
            if (mAudioDataReadyHandler != null)
            {
                mAudioWorker.DataReady -= mAudioDataReadyHandler;
                mAudioDataReadyHandler = null;
            }

            if (!mAudioWorker.TryStop(TimeSpan.FromMilliseconds(WORKER_STOP_TIMEOUT_MS)))
            {
                StatusBarText.Text = "Audio worker did not stop within timeout";
                return StopOutcome.Stopping;
            }

            mAudioWorker.Dispose();
            mAudioWorker = null;
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

        mPlaybackWorker = new PlaybackWorker(mRawAudio, mCurrentSamplesPerSecond);
        mPlaybackDataReadyHandler = CreateDataReadyHandler(runSessionToken);
        mPlaybackWorker.DataReady += mPlaybackDataReadyHandler;
        mPlaybackDoneHandler = reason => OnPlaybackDoneReadingFile(runSessionToken, reason);
        mPlaybackWorker.DoneReadingFile += mPlaybackDoneHandler;
        if (!mPlaybackWorker.Start(fileName))
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

        mSimWorker = new SimWorker(mRawAudio, mCurrentSamplesPerSecond);
        mSimDataReadyHandler = CreateDataReadyHandler(runSessionToken);
        mSimWorker.DataReady += mSimDataReadyHandler;
        mSimDoneHandler = reason => OnSimDone(runSessionToken, reason);
        mSimWorker.SimDone += mSimDoneHandler;
        if (!mSimWorker.Start(cfg))
        {
            throw new InvalidOperationException("Sim worker is already running.");
        }
    }

    private StopOutcome StopPlaybackThread()
    {
        // requestInterruption(): cancel; the worker reports completion via DoneReadingFile,
        // but on_StopPushButton_clicked also calls StopAnalysisThread()/AudioCloseCheck() directly.
        return StopPlaybackWorkerImmediate();
    }

    private StopOutcome StopSimThread()
    {
        return StopSimWorkerImmediate();
    }

    private StopOutcome StopPlaybackWorkerImmediate()
    {
        if (mPlaybackWorker != null)
        {
            if (mPlaybackDataReadyHandler != null)
            {
                mPlaybackWorker.DataReady -= mPlaybackDataReadyHandler;
                mPlaybackDataReadyHandler = null;
            }
            if (mPlaybackWorker.TryStop(TimeSpan.FromMilliseconds(WORKER_STOP_TIMEOUT_MS)))
            {
                if (mPlaybackDoneHandler != null)
                {
                    mPlaybackWorker.DoneReadingFile -= mPlaybackDoneHandler;
                    mPlaybackDoneHandler = null;
                }
                mPlaybackWorker.Dispose();
                mPlaybackWorker = null;
            }
            else
            {
                StatusBarText.Text = "Playback worker did not stop within timeout";
                return StopOutcome.Stopping;
            }
        }

        return StopOutcome.Stopped;
    }

    private StopOutcome StopSimWorkerImmediate()
    {
        if (mSimWorker != null)
        {
            if (mSimDataReadyHandler != null)
            {
                mSimWorker.DataReady -= mSimDataReadyHandler;
                mSimDataReadyHandler = null;
            }
            if (mSimWorker.TryStop(TimeSpan.FromMilliseconds(WORKER_STOP_TIMEOUT_MS)))
            {
                if (mSimDoneHandler != null)
                {
                    mSimWorker.SimDone -= mSimDoneHandler;
                    mSimDoneHandler = null;
                }
                mSimWorker.Dispose();
                mSimWorker = null;
            }
            else
            {
                StatusBarText.Text = "Sim worker did not stop within timeout";
                return StopOutcome.Stopping;
            }
        }

        return StopOutcome.Stopped;
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
                StatusBarText.Text = "Analysis worker did not stop within timeout";
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
        if (runSessionToken != mRunSessionToken)
        {
            return;
        }
        InvalidateRunSession();
        SetGuiStoppingMode();
        if (ModeComboBox.SelectedIndex == PLAYBACK)
        {
            SetAudioDevice(mDeviceNameBeforePlaybackOrSim);
            SetAudioRate(mRateBeforePlaybackOrSim);
        }
        StopOutcome outcome = StopPlaybackWorkerImmediate();
        outcome = CombineStopOutcome(outcome, StopAnalysisThread(completeInput: true));
        bool audioClosed = outcome == StopOutcome.Stopped && AudioCloseCheck();
        if (outcome != StopOutcome.Stopped || !audioClosed)
        {
            SetGuiStoppingMode();
            return;
        }
        SetGuiStopMode();
        StatusBarText.Text = reason == PlaybackCompletionReason.Failed ? "Playback failed" : "Stopped";
    }

    private void HandleSimDone(ulong runSessionToken, SimCompletionReason reason)
    {
        if (runSessionToken != mRunSessionToken)
        {
            return;
        }
        InvalidateRunSession();
        SetGuiStoppingMode();
        if (ModeComboBox.SelectedIndex == SIM)
        {
            SetAudioDevice(mDeviceNameBeforePlaybackOrSim);
            SetAudioRate(mRateBeforePlaybackOrSim);
        }
        StopOutcome outcome = StopSimWorkerImmediate();
        outcome = CombineStopOutcome(outcome, StopAnalysisThread(completeInput: true));
        bool audioClosed = outcome == StopOutcome.Stopped && AudioCloseCheck();
        if (outcome != StopOutcome.Stopped || !audioClosed)
        {
            SetGuiStoppingMode();
            return;
        }
        SetGuiStopMode();
        StatusBarText.Text = reason == SimCompletionReason.Failed ? "Simulation failed" : "Stopped";
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
            StatusBarText.Text = string.Format(
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
            StatusBarText.Text = "Audio input overrun: dropped " +
                                 frame.InputSamplesDropped.ToString(CultureInfo.InvariantCulture) +
                                 " samples before analysis";
        }
        else if (frame.AnalysisLagSamples > (ulong)Math.Max(1, mCurrentSamplesPerSecond / 4))
        {
            double lagMs = frame.AnalysisLagSamples * 1000.0 / Math.Max(1, mCurrentSamplesPerSecond);
            StatusBarText.Text = string.Format(
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
        Console.Error.WriteLine("RESET");

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
        // QMessageBox Yes/No/Cancel (default No). Implemented as a modal dialog.
        var ret = await ShowRecordSessionDialog();

        if (ret == DialogResult.Yes)
        {
            string? fileName = await ShowSaveWavDialog();
            if (!string.IsNullOrEmpty(fileName))
            {
                mWavWriter = new QueuedWavStreamWriter();
                if (!mWavWriter.Open(fileName, mCurrentSamplesPerSecond, 1))
                {
                    await ShowCriticalDialog("Error", "Failed to open WAV file");
                    mWavWriter.Dispose();
                    mWavWriter = null;
                    return false;
                }
            }
            else
            {
                return false;
            }
            return true;
        }
        else if (ret == DialogResult.No)
        {
            return true;
        }
        else if (ret == DialogResult.Cancel)
        {
            return false;
        }

        return true;
    }

    private bool AudioCloseCheck()
    {
        if (mWavWriter != null)
        {
            ulong droppedBlocks = mWavWriter.DroppedBlocks;
            bool closed = mWavWriter.Close();
            if (!closed)
            {
                StatusBarText.Text = "Failed to close WAV recording cleanly";
                return false;
            }

            mWavWriter.Dispose();
            mWavWriter = null;
            if (droppedBlocks != 0)
            {
                StatusBarText.Text = "WAV recording dropped " +
                                     droppedBlocks.ToString(CultureInfo.InvariantCulture) +
                                     " block(s)";
            }
        }

        return true;
    }

    // OpenFile: validate WAV header (standard-rate/mono/32-bit float). Returns true if acceptable.
    private async Task<bool> OpenFile(string fileName)
    {
        if (!File.Exists(fileName))
        {
            StatusBarText.Text = $"File {ToNativeSeparators(fileName)} could not be opened";
            return false;
        }

        if (!WavProbe.TryReadFormat(fileName, out WavFormatInfo format, out _))
        {
            StatusBarText.Text = $"File {ToNativeSeparators(fileName)} could not be opened";
            return false;
        }

        if (!WavProbe.IsAccepted(format, WavAcceptanceProfile.PlaybackFloatMonoStandardRates))
        {
            StatusBarText.Text = $"File {fileName} Not a standard-rate, single channel 32-bit Float WAV file";
            await ShowCriticalDialog("Error", "Invalid PCM Wave File");
            return false;
        }

        try
        {
            mCurrentDir = Path.GetDirectoryName(Path.GetFullPath(fileName)) ?? mCurrentDir;
        }
        catch { /* keep current dir */ }

        GetAudioRate(out mRateBeforePlaybackOrSim);
        GetAudioDevice(out mDeviceNameBeforePlaybackOrSim);
        if (!SetAudioDevice(PLAYBACK_OR_SIM_PCM))
        {
            Console.Error.WriteLine("SetAudioDevice Failed");
        }
        if (!SetAudioRate(format.SampleRate))
        {
            Console.Error.WriteLine("SetAudioRate Failed");
            return false;
        }

        return true;
    }

    private void PopulateSampleRates(int deviceNumber)
    {
        int[] standardRates = { 48000, 96000, 192000, 384000 };

        mSuppressEvents = true;
        SampleRatesComboBox.Items.Clear();
        mNumberofRates = 0;

        if (deviceNumber < 0)
        {
            // Audio device is null / "Playback/Sim": offer the standard rates.
            Console.Error.WriteLine("Audio Device is Null");
            foreach (int rate in standardRates)
            {
                SampleRatesComboBox.Items.Add(rate.ToString(CultureInfo.InvariantCulture) + " Hz");
                mAvalableRates[mNumberofRates] = rate;
                mNumberofRates++;
            }
        }
        else if (WindowsAudioAvailable)
        {
            IReadOnlyList<int> supported = AudioCaptureWorker.GetCandidateSampleRates(deviceNumber);
            // NAudio cannot probe arbitrary formats up front like Qt did. Show the standard
            // candidates and let AudioCaptureWorker.Start() be the authoritative validation.
            foreach (int rate in standardRates)
            {
                if (supported.Contains(rate) && mNumberofRates < mAvalableRates.Length)
                {
                    SampleRatesComboBox.Items.Add(rate.ToString(CultureInfo.InvariantCulture) + " Hz");
                    mAvalableRates[mNumberofRates] = rate;
                    mNumberofRates++;
                }
            }
        }
        else
        {
            foreach (int rate in standardRates)
            {
                SampleRatesComboBox.Items.Add(rate.ToString(CultureInfo.InvariantCulture) + " Hz");
                mAvalableRates[mNumberofRates] = rate;
                mNumberofRates++;
            }
        }

        mSuppressEvents = false;
        SampleRatesComboBox.SelectedIndex = -1;
        if (SampleRatesComboBox.ItemCount > 0)
            SampleRatesComboBox.SelectedIndex = 0;
    }

    private bool SetAudioRate(int rate)
    {
        int index = FindData(mAvalableRates, mNumberofRates, rate);
        if (index != -1)
        {
            SampleRatesComboBox.SelectedIndex = index;
            return true;
        }
        return false;
    }

    private bool SetAudioDevice(string name)
    {
        int index = FindText(InputDeviceComboBox, name, matchContains: false);
        if (index != -1)
        {
            InputDeviceComboBox.SelectedIndex = index;
            return true;
        }
        return false;
    }

    private void GetAudioRate(out int rate)
    {
        rate = mCurrentSamplesPerSecond;
    }

    private void GetAudioDevice(out string name)
    {
        name = CurrentText(InputDeviceComboBox);
    }

    private void SetGuiRunMode()
    {
        UiModeState.ApplyRunning(StartPushButton, StopPushButton, RunLockedControls());
    }

    private void SetGuiStartingMode()
    {
        UiModeState.ApplyStarting(StartPushButton, StopPushButton, RunLockedControls());
    }

    private void SetGuiStoppingMode()
    {
        UiModeState.ApplyStopping(StartPushButton, StopPushButton, RunLockedControls());
    }

    private void SetGuiStopMode()
    {
        UiModeState.ApplyStopped(
            StartPushButton,
            StopPushButton,
            RunLockedControls(),
            SampleRatesComboBox,
            CurrentText(ModeComboBox) != ModeStrings[PLAYBACK]);
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
            StopAudioWorker();
            StopAnalysisThread();
            AudioCloseCheck();
            StatusBarText.Text = "Failed to start live audio";
            await ShowCriticalDialog("Error", "Failed to start live audio: " + ex.Message);
            return false;
        }
        SetGuiRunMode();
        StatusBarText.Text = "Running";
        return true;
    }

    private async Task<bool> PlaybackStart()
    {
        bool status = false;

        // Equivalent to the QFileDialog re-open loop: keep prompting until a valid file is opened
        // or the user cancels.
        string? selected = null;
        while (true)
        {
            string? picked = await ShowOpenWavDialog();
            if (picked == null) break; // dialog rejected/cancelled
            selected = picked;
            if (status = await OpenFile(picked)) break;
        }
        if (!status || selected == null) return false;
        if (!await RecordSessionCheck())
        {
            SetAudioDevice(mDeviceNameBeforePlaybackOrSim);
            SetAudioRate(mRateBeforePlaybackOrSim);
            return false;
        }
        StartPlaybackThread(selected);
        SetGuiRunMode();
        StatusBarText.Text = "Running";
        return true;
    }

    private async Task<bool> SimStart()
    {
        // RealisticCheckBox -> realistic config; otherwise clean config
        // (MainWindow.cpp: watch_synth_stream_realistic_config / watch_synth_stream_clean_config).
        WatchSynthStreamConfig cfg = RealisticCheckBox.IsChecked == true
            ? WatchSynthStreamConfig.Realistic()
            : WatchSynthStreamConfig.Clean();

        cfg.Bph = SimBPH[SimBPHComboBox.SelectedIndex];
        cfg.SampleRateHz = (uint)mAvalableRates[SampleRatesComboBox.SelectedIndex];
        cfg.BeatErrorMs = -(double)(SimBeatErrorSpinBox.Value ?? 0m);
        cfg.PcmPeakAmplitude = 0.40; // normalized float PCM digital output level
        cfg.WatchAmplitudeDegrees = (double)(SimAmplitudeSpinBox.Value ?? 0m);
        cfg.LiftAngleDegrees = (double)(LiftAngleSpinBox.Value ?? 0m);
        cfg.RateErrorSPerDay = (double)(SimErrorRateSpinBox.Value ?? 0m);

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
        StatusBarText.Text = "Running";
        return true;
    }

    // --- Event handlers (Qt on_* slots) ---

    private void OnModeComboBoxChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (mSuppressEvents) return;
        string arg1 = CurrentText(ModeComboBox);

        if (arg1 != ModeStrings[LIVE])
            SetAudioDevice(PLAYBACK_OR_SIM_PCM);
        if (arg1 == ModeStrings[PLAYBACK])
        {
            SampleRatesComboBox.IsEnabled = false;
        }
        else
        {
            SampleRatesComboBox.IsEnabled = true;
        }
        if (arg1 == ModeStrings[LIVE])
        {
            bool isSet = false;
            int len = PreferredAudioDevices.Length;
            for (int i = 0; i < len; i++)
            {
                int index = FindText(InputDeviceComboBox, PreferredAudioDevices[i], matchContains: true);
                if (index != -1)
                {
                    InputDeviceComboBox.SelectedIndex = index;
                    isSet = true;
                    break;
                }
            }
            if (!isSet)
            {
                for (int i = 0; i < InputDeviceComboBox.ItemCount; ++i)
                {
                    if (ItemText(InputDeviceComboBox, i) != PLAYBACK_OR_SIM_PCM)
                    {
                        InputDeviceComboBox.SelectedIndex = i;
                        break;
                    }
                }
            }
        }
    }

    private void OnRefreshPushButtonClicked(object? sender, RoutedEventArgs e)
    {
        LoadAudioDevices();
    }

    private void OnLiftAngleSpinBoxPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        // Qt's QSpinBox::valueChanged(int). Mirror with Value property changes.
        if (e.Property != NumericUpDown.ValueProperty) return;
        mLiftAngle = (double)(LiftAngleSpinBox.Value ?? 0m);
        Console.Error.WriteLine("Lift Angle Value=" + mLiftAngle.ToString(CultureInfo.InvariantCulture));
    }

    private void OnAveragingPeriodComboBoxChanged(object? sender, SelectionChangedEventArgs e)
    {
        int idx = AveragingPeriodComboBox.SelectedIndex;
        if (idx < 0 || idx >= AveragingPeriodList.Length) return;
        mAveragingPeriod = AveragingPeriodList[idx];
        Console.Error.WriteLine("Averaging Period Value=" + mAveragingPeriod.ToString(CultureInfo.InvariantCulture));
    }

    private void OnMicrophoneSliderPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        // Qt's sliderMoved fires on user drag. Mirror with Value changes:
        // LocalSetAudioInputVolume(sliderPosition()/1000.0) -> SetAudioInputVolume.
        if (e.Property != Slider.ValueProperty) return;
        mAudioWorker?.SetVolume((float)(MicrophoneHorizontalSlider.Value / 1000.0));
    }

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

    private async void OnStartPushButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (mStartInProgress || mIsClosing)
        {
            return;
        }

        mStartInProgress = true;
        SetGuiStartingMode();
        StatusBarText.Text = "Starting";
        bool started = false;

        try
        {
            string mode = CurrentText(ModeComboBox);
            if (mode == ModeStrings[LIVE])
            {
                ConfigureSoundCard();
                started = await LiveStart();
            }
            else if (mode == ModeStrings[PLAYBACK])
            {
                started = await PlaybackStart();
            }
            else if (mode == ModeStrings[SIM])
            {
                started = await SimStart();
            }
        }
        catch (Exception ex)
        {
            InvalidateRunSession();
            StopAudioWorker();
            StopPlaybackWorkerImmediate();
            StopSimWorkerImmediate();
            StopAnalysisThread();
            AudioCloseCheck();
            StatusBarText.Text = "Failed to start";
            await ShowCriticalDialog("Error", "Failed to start: " + ex.Message);
        }
        finally
        {
            mStartInProgress = false;
            if (!started && !mIsClosing)
            {
                SetGuiStopMode();
                if (StatusBarText.Text == "Starting")
                {
                    StatusBarText.Text = "Stopped";
                }
            }
        }
    }

    private void OnStopPushButtonClicked(object? sender, RoutedEventArgs e)
    {
        SetGuiStoppingMode();
        StopOutcome outcome = StopOutcome.Stopped;

        string mode = CurrentText(ModeComboBox);
        if (mode == ModeStrings[LIVE])
        {
            outcome = CombineStopOutcome(outcome, StopAudioThread());
            outcome = CombineStopOutcome(outcome, StopAnalysisThread());
        }
        else if (mode == ModeStrings[PLAYBACK])
        {
            outcome = CombineStopOutcome(outcome, StopPlaybackThread());
            outcome = CombineStopOutcome(outcome, StopAnalysisThread());
        }
        else if (mode == ModeStrings[SIM])
        {
            outcome = CombineStopOutcome(outcome, StopSimThread());
            outcome = CombineStopOutcome(outcome, StopAnalysisThread());
        }

        bool audioClosed = outcome == StopOutcome.Stopped && AudioCloseCheck();
        if (outcome != StopOutcome.Stopped || !audioClosed)
        {
            SetGuiStoppingMode();
            if (StatusBarText.Text == "Running" || StatusBarText.Text == "Starting")
            {
                StatusBarText.Text = "Stopping";
            }
            return;
        }

        InvalidateRunSession();
        if (mode == ModeStrings[PLAYBACK] || mode == ModeStrings[SIM])
        {
            SetAudioDevice(mDeviceNameBeforePlaybackOrSim);
            SetAudioRate(mRateBeforePlaybackOrSim);
        }

        SetGuiStopMode();
        StatusBarText.Text = "Stopped";
    }

    private void OnInputDeviceComboBoxChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (mSuppressEvents) return;

        int deviceNumber;
        if (CurrentText(InputDeviceComboBox) != PLAYBACK_OR_SIM_PCM)
        {
            deviceNumber = CurrentInputDeviceNumber();

            int index = FindText(ModeComboBox, ModeStrings[LIVE], matchContains: false);
            if (index != -1) ModeComboBox.SelectedIndex = index;
        }
        else // PLAYBACK_OR_SIM_PCM
        {
            deviceNumber = -1;
            if (CurrentText(ModeComboBox) == ModeStrings[LIVE])
            {
                int index = FindText(ModeComboBox, ModeStrings[PLAYBACK], matchContains: false);
                if (index != -1) ModeComboBox.SelectedIndex = index;
            }
        }

        PopulateSampleRates(deviceNumber);
    }

    private void OnSampleRatesComboBoxChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (mSuppressEvents) return;
        int index = SampleRatesComboBox.SelectedIndex;
        if (index < 0) return;
        if ((index + 1) > mNumberofRates) return;
        mCurrentSamplesPerSecond = mAvalableRates[index];
        Console.Error.WriteLine("Sample Rate is " + mCurrentSamplesPerSecond.ToString(CultureInfo.InvariantCulture) +
                                " Index " + index.ToString(CultureInfo.InvariantCulture));
    }

    // --- Helpers ---

    private int CurrentInputDeviceNumber()
    {
        int idx = InputDeviceComboBox.SelectedIndex;
        if (idx >= 0 && idx < mInputDeviceNumbers.Count) return mInputDeviceNumbers[idx];
        return -1;
    }

    private static string CurrentText(ComboBox combo)
    {
        return ItemText(combo, combo.SelectedIndex);
    }

    private static string ItemText(ComboBox combo, int index)
    {
        if (index < 0 || index >= combo.ItemCount) return "";
        return combo.Items[index]?.ToString() ?? "";
    }

    // findText: returns combo index whose item text matches (exact or contains).
    private static int FindText(ComboBox combo, string text, bool matchContains)
    {
        for (int i = 0; i < combo.ItemCount; i++)
        {
            string itemText = ItemText(combo, i);
            if (matchContains)
            {
                if (itemText.Contains(text, StringComparison.Ordinal)) return i;
            }
            else
            {
                if (itemText == text) return i;
            }
        }
        return -1;
    }

    // findData: returns index in the available-rates table that equals 'rate'.
    private static int FindData(int[] rates, int count, int rate)
    {
        for (int i = 0; i < count; i++)
        {
            if (rates[i] == rate) return i;
        }
        return -1;
    }

    private static double ParseDouble(string? text)
    {
        // QString::toDouble returns 0.0 on failure.
        if (string.IsNullOrEmpty(text)) return 0.0;
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : 0.0;
    }

    private static string ToNativeSeparators(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private AnalysisRunSettings BuildRunSettings()
    {
        bool autoBph = BPHComboBox.SelectedIndex == 0;
        return new AnalysisRunSettings(
            SampleRate: mCurrentSamplesPerSecond,
            LiftAngle: mLiftAngle,
            AveragingPeriod: mAveragingPeriod,
            UseCOnset: UseConsetCheckBox.IsChecked == true,
            AutoBph: autoBph,
            ManualBph: autoBph ? 0 : ManualAutoBPH[BPHComboBox.SelectedIndex],
            HpfCutoffHz: ParseDouble(HighLineEdit.Text),
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
            ScopeScale: Math.Max(1, (int)(ScopeScaleSpinBox.Value ?? 1m)));
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

    private IReadOnlyList<Control> RunLockedControls()
    {
        return new Control[]
        {
            InputDeviceComboBox,
            SampleRatesComboBox,
            BPHComboBox,
            ModeComboBox,
            RefreshPushButton,
            AveragingPeriodComboBox,
            LiftAngleSpinBox,
            SimAmplitudeSpinBox,
            SimBeatErrorSpinBox,
            SimBPHComboBox,
            SimErrorRateSpinBox,
            RealisticCheckBox,
            UseConsetCheckBox,
            HighLineEdit,
        };
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

    // --- Dialogs (replace QMessageBox / QFileDialog) ---

    private enum DialogResult { Yes, No, Cancel }

    private async Task<DialogResult> ShowRecordSessionDialog()
    {
        var dialog = new Window
        {
            Title = "Record Session",
            Width = 360,
            Height = 150,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var result = DialogResult.No; // default button = No

        var yes = new Button { Content = "Yes", Width = 80, IsDefault = false };
        var no = new Button { Content = "No", Width = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        yes.Click += (_, _) => { result = DialogResult.Yes; dialog.Close(); };
        no.Click += (_, _) => { result = DialogResult.No; dialog.Close(); };
        cancel.Click += (_, _) => { result = DialogResult.Cancel; dialog.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Record Session", FontWeight = FontWeight.Bold });
        panel.Children.Add(new TextBlock { Text = "Do you want to record this session ?", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(buttons);
        dialog.Content = panel;

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task ShowCriticalDialog(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 320,
            Height = 130,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.Close();
        var panel = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(ok);
        dialog.Content = panel;
        await dialog.ShowDialog(this);
    }

    private async Task<string?> ShowOpenWavDialog()
    {
        var sp = StorageProvider;
        IReadOnlyList<IStorageFolder>? startFolder = null;
        try
        {
            var folder = await sp.TryGetFolderFromPathAsync(mCurrentDir);
            if (folder != null) startFolder = new[] { folder };
        }
        catch { /* ignore */ }

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Document",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder?[0],
            FileTypeFilter = new[]
            {
                new FilePickerFileType("WAV Files") { Patterns = new[] { "*.wav" } },
            },
        });

        if (files.Count == 0) return null;
        return files[0].TryGetLocalPath();
    }

    private async Task<string?> ShowSaveWavDialog()
    {
        var sp = StorageProvider;
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Output File",
            DefaultExtension = "wav",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Wav Files") { Patterns = new[] { "*.wav" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } },
            },
        });
        return file?.TryGetLocalPath();
    }

}
