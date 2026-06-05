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

using TimeGrapher.App.Rendering;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.AudioIo;
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

    private const string PLAYBACK_OR_SIM_PCM = "Playback/Sim";

    private const string PREF_NAME_WELSHI = "Welshi USB";
    private const string PREF_NAME_CHINESE_GENERIC = "Chinese Generic USB";

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
    private WavStreamWriter? mWavWriter;
    private GraphFrameRenderer mGraphFrameRenderer = null!;
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

        mGraphFrameRenderer = new GraphFrameRenderer(ScopePlot, RatePlot, SoundImage, Results, FontFamily.Name);

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

        LoadBPH();
        LoadSimBPH();
        LoadAudioDevices();
        mGraphFrameRenderer.CreateGraphs(ERROR_RATE_Y_SCALE, ERROR_RATE_X_DATA_POINTS);
        LoadAverageingPeriod();
        Results.Text = "RATE ------ s/d   AMPLITUDE ---   BEAT ERROR ---- ms   BEAT ----- bph";

        Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // ~MainWindow: StopAnalysisThread(); plus stop any running input worker.
        StopAudioWorker();
        StopPlaybackWorkerImmediate();
        StopSimWorkerImmediate();
        StopAnalysisThread();
    }

    private void ConfigureSoundCard()
    {
        // Windows path (Q_OS_WIN). LinuxAudio is not ported (Windows target).
        SystemAudioControl.SetSoundParameters(WINDOWS_SOUND_ENDPOINT_NAME, WINDOWS_SOUND_MIC_NAME, WINDOWS_SOUND_MIC_PERCENT_VOLUME);
    }

    private void LoadAudioDevices()
    {
        mSuppressEvents = true;

        IReadOnlyList<string> inputDevices = AudioCaptureWorker.EnumerateInputDevices();
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
        int deviceNumber = CurrentInputDeviceNumber();
        StopAnalysisThread();
        Reset();

        // Recreate the master buffer at the current sample rate.
        mRawAudio = new MasterAudioBuffer(mCurrentSamplesPerSecond);
        StartAnalysisThread();

        mAudioWorker = new AudioCaptureWorker(mRawAudio);
        // AudioDataReady -> analysis worker (DataReady is notify-only).
        mAudioWorker.DataReady += OnInputDataReady;
        mAudioWorker.Start(deviceNumber, mCurrentSamplesPerSecond, (float)(MicrophoneHorizontalSlider.Value / 1000.0));
    }

    private void StopAudioThread()
    {
        // LocalStopAudio -> StopAudioRecording.
        StopAudioWorker();
    }

    private void StopAudioWorker()
    {
        if (mAudioWorker != null)
        {
            mAudioWorker.DataReady -= OnInputDataReady;
            mAudioWorker.Stop();
            mAudioWorker.Dispose();
            mAudioWorker = null;
        }
    }

    private void StartPlaybackThread(string fileName)
    {
        StopAnalysisThread();
        Reset();

        mRawAudio = new MasterAudioBuffer(mCurrentSamplesPerSecond);
        StartAnalysisThread();

        mPlaybackWorker = new PlaybackWorker(mRawAudio, mCurrentSamplesPerSecond);
        mPlaybackWorker.DataReady += OnInputDataReady;
        mPlaybackWorker.DoneReadingFile += OnPlaybackDoneReadingFile;
        mPlaybackWorker.Start(fileName);
    }

    private void StartSimThread(WatchSynthStreamConfig cfg)
    {
        StopAnalysisThread();
        Reset();

        mRawAudio = new MasterAudioBuffer(mCurrentSamplesPerSecond);
        StartAnalysisThread();

        mSimWorker = new SimWorker(mRawAudio, mCurrentSamplesPerSecond);
        mSimWorker.DataReady += OnInputDataReady;
        mSimWorker.SimDone += OnSimDone;
        mSimWorker.Start(cfg);
    }

    private void StopPlaybackThread()
    {
        // requestInterruption(): cancel; the worker reports completion via DoneReadingFile,
        // but on_StopPushButton_clicked also calls StopAnalysisThread()/AudioCloseCheck() directly.
        StopPlaybackWorkerImmediate();
    }

    private void StopSimThread()
    {
        StopSimWorkerImmediate();
    }

    private void StopPlaybackWorkerImmediate()
    {
        if (mPlaybackWorker != null)
        {
            mPlaybackWorker.DataReady -= OnInputDataReady;
            mPlaybackWorker.DoneReadingFile -= OnPlaybackDoneReadingFile;
            mPlaybackWorker.Stop();
            mPlaybackWorker.Dispose();
            mPlaybackWorker = null;
        }
    }

    private void StopSimWorkerImmediate()
    {
        if (mSimWorker != null)
        {
            mSimWorker.DataReady -= OnInputDataReady;
            mSimWorker.SimDone -= OnSimDone;
            mSimWorker.Stop();
            mSimWorker.Dispose();
            mSimWorker = null;
        }
    }

    private void StartAnalysisThread()
    {
        mAnalysisSessionId++;

        var analysisConfig = new AnalysisWorker.Config
        {
            SampleRate = mCurrentSamplesPerSecond,
            LiftAngle = mLiftAngle,
            AveragingPeriod = mAveragingPeriod,
            UseCOnset = UseConsetCheckBox.IsChecked == true,
            SessionId = mAnalysisSessionId,
            AutoBph = BPHComboBox.SelectedIndex == 0,
        };
        if (!analysisConfig.AutoBph)
        {
            analysisConfig.ManualBph = ManualAutoBPH[BPHComboBox.SelectedIndex];
        }
        analysisConfig.HpfCutoffHz = ParseDouble(HighLineEdit.Text);
        // sound_image_size = ui->SoundImage->size() (.ui geometry 1019x654).
        analysisConfig.SoundImageWidth = (int)SoundImage.Width;
        analysisConfig.SoundImageHeight = (int)SoundImage.Height;
        analysisConfig.WavWriter = mWavWriter;

        mAnalysisWorker = new AnalysisWorker(mRawAudio!, analysisConfig);
        mAnalysisWorker.AnalysisFrameReady += OnAnalysisFrameReady;
        mAnalysisWorker.Start();
    }

    private void StopAnalysisThread()
    {
        if (mAnalysisWorker != null)
        {
            mAnalysisWorker.AnalysisFrameReady -= OnAnalysisFrameReady;
            mAnalysisWorker.Stop();
            mAnalysisWorker.Dispose();
            mAnalysisWorker = null;
            mAnalysisSessionId++;
        }
    }

    // Input worker DataReady (any thread) -> analysis worker. Safe from any thread.
    private void OnInputDataReady()
    {
        mAnalysisWorker?.NotifyDataReady();
    }

    private void OnPlaybackDoneReadingFile()
    {
        // PlaybackDoneReadingFile fires on the playback thread; marshal to UI thread.
        Dispatcher.UIThread.Post(HandlePlaybackDoneReadingFile);
    }

    private void OnSimDone()
    {
        Dispatcher.UIThread.Post(HandleSimDone);
    }

    private void HandlePlaybackDoneReadingFile()
    {
        SetGuiStopMode();
        if (ModeComboBox.SelectedIndex == PLAYBACK)
        {
            SetAudioDevice(mDeviceNameBeforePlaybackOrSim);
            SetAudioRate(mRateBeforePlaybackOrSim);
        }
        StopPlaybackWorkerImmediate();
        StopAnalysisThread();
        AudioCloseCheck();
        StatusBarText.Text = "Stopped";
    }

    private void HandleSimDone()
    {
        SetGuiStopMode();
        if (ModeComboBox.SelectedIndex == SIM)
        {
            SetAudioDevice(mDeviceNameBeforePlaybackOrSim);
            SetAudioRate(mRateBeforePlaybackOrSim);
        }
        StopSimWorkerImmediate();
        StopAnalysisThread();
        AudioCloseCheck();
        StatusBarText.Text = "Stopped";
    }

    // AnalysisFrameReady fires on the analysis thread; marshal to UI thread.
    private void OnAnalysisFrameReady(AnalysisFrame frame)
    {
        Dispatcher.UIThread.Post(() => HandleAnalysisFrame(frame));
    }

    private void HandleAnalysisFrame(AnalysisFrame frame)
    {
        if (frame.SessionId != mAnalysisSessionId)
        {
            return;
        }

        mGraphFrameRenderer.RenderFrame(frame, mCurrentSamplesPerSecond, (int)(ScopeScaleSpinBox.Value ?? 0m));

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
    }

    private void Reset()
    {
        Console.Error.WriteLine("RESET");

        mGraphFrameRenderer.Reset(mCurrentSamplesPerSecond, ERROR_RATE_Y_SCALE, ERROR_RATE_X_DATA_POINTS);

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
                mWavWriter = new WavStreamWriter();
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

    private void AudioCloseCheck()
    {
        if (mWavWriter != null)
        {
            mWavWriter.Close();
            mWavWriter.Dispose();
            mWavWriter = null;
        }
    }

    // OpenFile: validate WAV header (48K/mono/32-bit float). Returns true if acceptable.
    private bool OpenFile(string fileName)
    {
        if (!File.Exists(fileName))
        {
            StatusBarText.Text = $"File {ToNativeSeparators(fileName)} could not be opened";
            return false;
        }

        try
        {
            mCurrentDir = Path.GetDirectoryName(Path.GetFullPath(fileName)) ?? mCurrentDir;
        }
        catch { /* keep current dir */ }

        var header = new WaveHeader();
        try
        {
            using FileStream file = File.OpenRead(fileName);
            using var reader = new BinaryReader(file);

            header.RiffId = reader.ReadBytes(4);          // "RIFF"
            header.FileSize = reader.ReadUInt32();
            header.WaveId = reader.ReadBytes(4);          // "WAVE"
            header.FmtId = reader.ReadBytes(4);           // "fmt "
            header.FmtSize = reader.ReadUInt32();
            header.AudioFormat = reader.ReadUInt16();
            header.NumChannels = reader.ReadUInt16();
            header.SampleRate = reader.ReadUInt32();
            header.ByteRate = reader.ReadUInt32();
            header.BlockAlign = reader.ReadUInt16();
            header.BitsPerSample = reader.ReadUInt16();

            // Skip any extra fmt bytes if fmtSize > 16.
            if (header.FmtSize > 16)
                file.Seek((long)(header.FmtSize - 16), SeekOrigin.Current);

            // Look for the "data" chunk (it might not be immediately after fmt).
            var chunkId = new byte[4];
            while (file.Position < file.Length)
            {
                if (reader.Read(chunkId, 0, 4) < 4) break;
                uint chunkSize = reader.ReadUInt32();
                if (chunkId[0] == (byte)'d' && chunkId[1] == (byte)'a' && chunkId[2] == (byte)'t' && chunkId[3] == (byte)'a')
                {
                    header.DataSize = chunkSize;
                    break;
                }
                file.Seek(chunkSize, SeekOrigin.Current);
            }
        }
        catch
        {
            // Open/read failure: treat like Qt's "could not be opened" / truncated header.
            // (Validation below rejects an empty/partial header anyway.)
            StatusBarText.Text = $"File {ToNativeSeparators(fileName)} could not be opened";
        }

        GetAudioRate(out mRateBeforePlaybackOrSim);
        GetAudioDevice(out mDeviceNameBeforePlaybackOrSim);
        if (!SetAudioDevice(PLAYBACK_OR_SIM_PCM))
        {
            Console.Error.WriteLine("SetAudioDevice Failed");
        }
        if (!SetAudioRate((int)header.SampleRate))
        {
            Console.Error.WriteLine("SetAudioRate Failed");
        }

        bool riffOk = header.RiffId.Length == 4 &&
                      header.RiffId[0] == (byte)'R' && header.RiffId[1] == (byte)'I' &&
                      header.RiffId[2] == (byte)'F' && header.RiffId[3] == (byte)'F';

        if (!riffOk || (header.SampleRate != (uint)mCurrentSamplesPerSecond) ||
            (header.NumChannels != 1) || (header.BitsPerSample != 32) ||
            (header.AudioFormat != 3))
        {
            StatusBarText.Text = $"File {fileName} Not a 48K, single channel 32-bit Float WAV file";
            _ = ShowCriticalDialog("Error", "Invalid PCM Wave File");
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
        else
        {
            IReadOnlyList<int> supported = AudioCaptureWorker.GetSupportedSampleRates(deviceNumber);
            // Keep only the standard rates that the device reports as supported,
            // preserving the standard-rate ordering (matches the original probe loop).
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
        InputDeviceComboBox.IsEnabled = false;
        SampleRatesComboBox.IsEnabled = false;
        BPHComboBox.IsEnabled = false;
        ModeComboBox.IsEnabled = false;
        StartPushButton.IsEnabled = false;
        StopPushButton.IsEnabled = true;
        RefreshPushButton.IsEnabled = false;
        AveragingPeriodComboBox.IsEnabled = false;
        LiftAngleSpinBox.IsEnabled = false;
        SimAmplitudeSpinBox.IsEnabled = false;
        SimBeatErrorSpinBox.IsEnabled = false;
        SimBPHComboBox.IsEnabled = false;
        SimErrorRateSpinBox.IsEnabled = false;
        RealisticCheckBox.IsEnabled = false;
        UseConsetCheckBox.IsEnabled = false;
        HighLineEdit.IsEnabled = false;
    }

    private void SetGuiStopMode()
    {
        StopPushButton.IsEnabled = false;
        ModeComboBox.IsEnabled = true;
        RefreshPushButton.IsEnabled = true;
        StartPushButton.IsEnabled = true;
        InputDeviceComboBox.IsEnabled = true;
        if (CurrentText(ModeComboBox) != ModeStrings[PLAYBACK])
        {
            SampleRatesComboBox.IsEnabled = true;
        }
        AveragingPeriodComboBox.IsEnabled = true;
        LiftAngleSpinBox.IsEnabled = true;
        BPHComboBox.IsEnabled = true;
        LiftAngleSpinBox.IsEnabled = true;
        SimAmplitudeSpinBox.IsEnabled = true;
        SimBeatErrorSpinBox.IsEnabled = true;
        SimBPHComboBox.IsEnabled = true;
        SimErrorRateSpinBox.IsEnabled = true;
        RealisticCheckBox.IsEnabled = true;
        UseConsetCheckBox.IsEnabled = true;
        HighLineEdit.IsEnabled = true;
    }

    private async Task LiveStart()
    {
        if (!await RecordSessionCheck()) return;
        StartAudioThread();
        SetGuiRunMode();
        StatusBarText.Text = "Running";
    }

    private async Task PlaybackStart()
    {
        bool status = false;

        if (!await RecordSessionCheck()) return;

        // Equivalent to the QFileDialog re-open loop: keep prompting until a valid file is opened
        // or the user cancels.
        string? selected = null;
        while (true)
        {
            string? picked = await ShowOpenWavDialog();
            if (picked == null) break; // dialog rejected/cancelled
            selected = picked;
            if (status = OpenFile(picked)) break;
        }
        if (!status || selected == null) return;
        StartPlaybackThread(selected);
        SetGuiRunMode();
        StatusBarText.Text = "Running";
    }

    private async Task SimStart()
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

        if (!await RecordSessionCheck()) return;
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

    private async void OnStartPushButtonClicked(object? sender, RoutedEventArgs e)
    {
        string mode = CurrentText(ModeComboBox);
        if (mode == ModeStrings[LIVE])
        {
            ConfigureSoundCard();
            await LiveStart();
        }
        else if (mode == ModeStrings[PLAYBACK])
        {
            await PlaybackStart();
        }
        else if (mode == ModeStrings[SIM])
        {
            await SimStart();
        }
    }

    private void OnStopPushButtonClicked(object? sender, RoutedEventArgs e)
    {
        SetGuiStopMode();

        string mode = CurrentText(ModeComboBox);
        if (mode == ModeStrings[LIVE])
        {
            StopAudioThread();
            StopAnalysisThread();
            AudioCloseCheck();
        }
        else if (mode == ModeStrings[PLAYBACK])
        {
            StopPlaybackThread();
            StopAnalysisThread();
            AudioCloseCheck();

            SetAudioDevice(mDeviceNameBeforePlaybackOrSim);
            SetAudioRate(mRateBeforePlaybackOrSim);
        }
        else if (mode == ModeStrings[SIM])
        {
            StopSimThread();
            StopAnalysisThread();
            AudioCloseCheck();
            SetAudioDevice(mDeviceNameBeforePlaybackOrSim);
            SetAudioRate(mRateBeforePlaybackOrSim);
        }

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

    // WAV header fields (WaveHeader.h) used by OpenFile validation.
    private sealed class WaveHeader
    {
        public byte[] RiffId = Array.Empty<byte>();
        public uint FileSize;
        public byte[] WaveId = Array.Empty<byte>();
        public byte[] FmtId = Array.Empty<byte>();
        public uint FmtSize;
        public ushort AudioFormat;
        public ushort NumChannels;
        public uint SampleRate;
        public uint ByteRate;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public uint DataSize;
    }
}
