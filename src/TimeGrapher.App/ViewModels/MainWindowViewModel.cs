using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.ViewModels;

internal enum RunUiState
{
    Stopped,
    Starting,
    Running,
    Paused,
    Stopping,
}

internal sealed class MainWindowViewModel : INotifyPropertyChanged
{
    /// <summary>Review-bar step button increment (stream seconds).</summary>
    public const double ReviewStepS = 1.0;

    private readonly AsyncRelayCommand _playPauseCommand;
    private readonly RelayCommand _stopCommand;
    private readonly RelayCommand _refreshDevicesCommand;
    private readonly RelayCommand _resetSequenceCommand;
    private readonly RelayCommand _reviewStepBackCommand;
    private readonly RelayCommand _reviewStepForwardCommand;
    private readonly RelayCommand _reviewLiveCommand;
    private RunUiState _runState = RunUiState.Stopped;
    private bool _modeAllowsSampleRate = true;
    private bool _modeAllowsGain = true;
    private string _statusText = "";
    private string _latencyText = "";
    private bool _isAwaitingBeatSync;
    private double _gain = 100.0;
    private int _selectedInputDeviceIndex = -1;
    private int _selectedSampleRateIndex = -1;
    private int _selectedAveragingPeriodIndex = -1;
    private int _selectedBphIndex;
    private decimal _liftAngle = 52m;
    private int _selectedSimBphIndex = -1;
    private decimal _simErrorRate;
    private decimal _simAmplitude = 300m;
    private decimal _simBeatError;
    private bool _realistic = true;
    private string _highPassCutoffText = "200";
    private decimal _scopeScale = 2m;
    private bool _useCOnset;
    private bool _pllEventVeto;
    private int _sweepMultiple = 2;
    private int _selectedPositionIndex; // 0 = WatchPosition.CH (dial up)
    private bool _sigmaAveraging;
    private double? _reviewCursorTimeS;
    private double _reviewMaximumS;

    public MainWindowViewModel(
        Func<Task> startAsync,
        Action pauseOrResume,
        Action stop,
        Action refreshDevices)
    {
        _playPauseCommand = new AsyncRelayCommand(async () =>
        {
            if (_runState == RunUiState.Stopped)
            {
                await startAsync();
                return;
            }

            pauseOrResume();
        }, () => IsPlayPauseEnabled);
        _stopCommand = new RelayCommand(stop, () => IsStopEnabled);
        _refreshDevicesCommand = new RelayCommand(refreshDevices, () => AreRunParametersEnabled);
        _resetSequenceCommand = new RelayCommand(() => ResetSequenceRequested?.Invoke());
        _reviewStepBackCommand = new RelayCommand(() => StepReviewCursor(-ReviewStepS));
        _reviewStepForwardCommand = new RelayCommand(() => StepReviewCursor(ReviewStepS));
        _reviewLiveCommand = new RelayCommand(() => ReviewCursorTimeS = null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised when the user asks to restart the multi-position sequence
    /// (clear the per-position aggregates). MainWindow forwards it to the
    /// running analysis worker, the run-control-knob flow.
    /// </summary>
    public event Action? ResetSequenceRequested;

    public ICommand PlayPauseCommand => _playPauseCommand;
    public ICommand StopCommand => _stopCommand;
    public ICommand RefreshDevicesCommand => _refreshDevicesCommand;
    public ICommand ResetSequenceCommand => _resetSequenceCommand;
    public ICommand ReviewStepBackCommand => _reviewStepBackCommand;
    public ICommand ReviewStepForwardCommand => _reviewStepForwardCommand;
    public ICommand ReviewLiveCommand => _reviewLiveCommand;

    public ObservableCollection<string> InputDeviceNames { get; } = new();
    public ObservableCollection<string> SampleRateLabels { get; } = new();
    public ObservableCollection<string> AveragingPeriodLabels { get; } = new();
    public ObservableCollection<string> BphLabels { get; } = new();
    public ObservableCollection<string> SimBphLabels { get; } = new();

    public RunUiState RunState => _runState;

    public bool AreRunParametersEnabled => _runState == RunUiState.Stopped;

    public bool IsPlayPauseEnabled => _runState is RunUiState.Stopped or RunUiState.Running or RunUiState.Paused;

    // Stopping stays enabled so a failed/timed-out stop can be retried instead of wedging the UI.
    public bool IsStopEnabled => _runState is RunUiState.Running or RunUiState.Paused or RunUiState.Stopping;

    public bool IsSampleRateEnabled => AreRunParametersEnabled && _modeAllowsSampleRate;

    // Gain is a live knob (both platform workers forward SetVolume mid-capture,
    // matching the Qt original's slider), so it is gated by mode only, not by
    // run state.
    public bool IsGainEnabled => _modeAllowsGain;

    public string PlayPauseButtonText => _runState switch
    {
        RunUiState.Stopped => "Start",
        RunUiState.Paused => "Resume",
        _ => "Pause",
    };

    public bool IsPlayPauseButtonShowingPause => _runState == RunUiState.Running;

    public bool IsPlayPauseButtonShowingPlay => !IsPlayPauseButtonShowingPause;

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>True while a run is active but the detector has not yet locked the beat.</summary>
    public bool IsAwaitingBeatSync
    {
        get => _isAwaitingBeatSync;
        set => SetProperty(ref _isAwaitingBeatSync, value);
    }

    /// <summary>Latency / missed-beat readout shown on the right of the status bar.</summary>
    public string LatencyText
    {
        get => _latencyText;
        set => SetProperty(ref _latencyText, value);
    }

    public double Gain
    {
        get => _gain;
        set => SetProperty(ref _gain, value);
    }

    public int SelectedInputDeviceIndex
    {
        get => _selectedInputDeviceIndex;
        set => SetProperty(ref _selectedInputDeviceIndex, value);
    }

    public int SelectedSampleRateIndex
    {
        get => _selectedSampleRateIndex;
        set => SetProperty(ref _selectedSampleRateIndex, value);
    }

    public int SelectedAveragingPeriodIndex
    {
        get => _selectedAveragingPeriodIndex;
        set => SetProperty(ref _selectedAveragingPeriodIndex, value);
    }

    public int SelectedBphIndex
    {
        get => _selectedBphIndex;
        set => SetProperty(ref _selectedBphIndex, value);
    }

    public decimal LiftAngle
    {
        get => _liftAngle;
        set => SetProperty(ref _liftAngle, value);
    }

    public int SelectedSimBphIndex
    {
        get => _selectedSimBphIndex;
        set => SetProperty(ref _selectedSimBphIndex, value);
    }

    public decimal SimErrorRate
    {
        get => _simErrorRate;
        set => SetProperty(ref _simErrorRate, value);
    }

    public decimal SimAmplitude
    {
        get => _simAmplitude;
        set => SetProperty(ref _simAmplitude, value);
    }

    public decimal SimBeatError
    {
        get => _simBeatError;
        set => SetProperty(ref _simBeatError, value);
    }

    public bool Realistic
    {
        get => _realistic;
        set => SetProperty(ref _realistic, value);
    }

    public string HighPassCutoffText
    {
        get => _highPassCutoffText;
        set => SetProperty(ref _highPassCutoffText, value);
    }

    public decimal ScopeScale
    {
        get => _scopeScale;
        set => SetProperty(ref _scopeScale, value);
    }

    public bool UseCOnset
    {
        get => _useCOnset;
        set => SetProperty(ref _useCOnset, value);
    }

    /// <summary>
    /// PLL event veto (drops phase-mismatched events before metrics).
    /// Adaptive floor and regime guard are always on; this opt-in adds the
    /// veto, which boosts precision on weak/impulsive signals but can cost
    /// recall under extreme sustained noise.
    /// </summary>
    public bool PllEventVeto
    {
        get => _pllEventVeto;
        set => SetProperty(ref _pllEventVeto, value);
    }

    /// <summary>Scope Sweep window length as a multiple of the beat period (1x / 2x / 4x).</summary>
    public int SweepMultiple
    {
        get => _sweepMultiple;
        set => SetProperty(ref _sweepMultiple, value);
    }

    /// <summary>Beat-Noise Scope 2 Σ averaging on/off (forwarded to the analysis worker).</summary>
    public bool SigmaAveraging
    {
        get => _sigmaAveraging;
        set => SetProperty(ref _sigmaAveraging, value);
    }

    /// <summary>Active watch test position as a <see cref="WatchPosition"/> ordinal (0 = CH, dial up).</summary>
    public int SelectedPositionIndex
    {
        get => _selectedPositionIndex;
        set
        {
            if (SetProperty(ref _selectedPositionIndex, value))
            {
                OnPropertyChanged(nameof(PositionLabel));
            }
        }
    }

    /// <summary>Always-visible status-bar indicator of the active test position ("POS CH").</summary>
    public string PositionLabel => "POS " + ((WatchPosition)_selectedPositionIndex).ShortName();

    /// <summary>
    /// Pause-and-review scrub cursor (stream seconds), the
    /// AnalysisTabRenderContext.ReviewCursorTimeS contract: null = live (no
    /// cursor). Values clamp into the captured 0..<see cref="ReviewMaximumS"/>
    /// range. MainWindow re-renders the kept last frame when this moves while
    /// paused, so scrubbing inspects the recorded data without touching it.
    /// </summary>
    public double? ReviewCursorTimeS
    {
        get => _reviewCursorTimeS;
        set
        {
            double? clamped = value is double timeS ? Math.Clamp(timeS, 0.0, _reviewMaximumS) : null;
            if (SetProperty(ref _reviewCursorTimeS, clamped))
            {
                OnPropertyChanged(nameof(ReviewSliderValueS));
                OnPropertyChanged(nameof(ReviewReadoutText));
            }
        }
    }

    /// <summary>Latest captured stream time (s); the review slider's Maximum.</summary>
    public double ReviewMaximumS => _reviewMaximumS;

    /// <summary>
    /// Slider surface of the cursor: live (null cursor) reads as the latest
    /// captured time. Echo writes of the current effective value are ignored so
    /// the slider re-applying its value (e.g. when its Maximum binding moves)
    /// never enters review mode by itself.
    /// </summary>
    public double ReviewSliderValueS
    {
        get => _reviewCursorTimeS ?? _reviewMaximumS;
        set
        {
            if (value != (_reviewCursorTimeS ?? _reviewMaximumS))
            {
                ReviewCursorTimeS = value;
            }
        }
    }

    /// <summary>Review-bar readout: "REVIEW 01:23 / 12:34" while scrubbed, "LIVE 12:34" otherwise.</summary>
    public string ReviewReadoutText => _reviewCursorTimeS is double timeS
        ? "REVIEW " + FormatStreamTime(timeS) + " / " + FormatStreamTime(_reviewMaximumS)
        : "LIVE " + FormatStreamTime(_reviewMaximumS);

    /// <summary>The review bar shows only while paused (pause gates new readings; live data is never lost).</summary>
    public bool IsReviewBarVisible => _runState == RunUiState.Paused;

    /// <summary>
    /// Grows the captured review range to the newest rendered stream time.
    /// Monotonic within a session: late or history-less frames never shrink the
    /// scrub range; only <see cref="ResetReview"/> (a new session) clears it.
    /// </summary>
    public void UpdateReviewMaximum(double latestTimeS)
    {
        if (latestTimeS <= _reviewMaximumS)
        {
            return;
        }

        _reviewMaximumS = latestTimeS;
        OnPropertyChanged(nameof(ReviewMaximumS));
        OnPropertyChanged(nameof(ReviewSliderValueS));
        OnPropertyChanged(nameof(ReviewReadoutText));
    }

    /// <summary>Clears the scrub cursor and captured range for a new measurement session.</summary>
    public void ResetReview()
    {
        ReviewCursorTimeS = null;
        if (_reviewMaximumS != 0.0)
        {
            _reviewMaximumS = 0.0;
            OnPropertyChanged(nameof(ReviewMaximumS));
            OnPropertyChanged(nameof(ReviewSliderValueS));
            OnPropertyChanged(nameof(ReviewReadoutText));
        }
    }

    private void StepReviewCursor(double deltaS)
    {
        // Stepping from live starts at the newest reading; the setter clamps.
        ReviewCursorTimeS = (_reviewCursorTimeS ?? _reviewMaximumS) + deltaS;
    }

    private static string FormatStreamTime(double seconds)
    {
        int total = Math.Max(0, (int)seconds);
        return $"{total / 60:00}:{total % 60:00}";
    }

    public void SetModeAllowsSampleRate(bool value)
    {
        if (_modeAllowsSampleRate == value)
        {
            return;
        }

        _modeAllowsSampleRate = value;
        OnPropertyChanged(nameof(IsSampleRateEnabled));
    }

    public void SetModeAllowsGain(bool value)
    {
        if (_modeAllowsGain == value)
        {
            return;
        }

        _modeAllowsGain = value;
        OnPropertyChanged(nameof(IsGainEnabled));
    }

    public void SetInputDeviceNames(IEnumerable<string> values) => ReplaceItems(InputDeviceNames, values);

    public void SetSampleRateLabels(IEnumerable<string> values) => ReplaceItems(SampleRateLabels, values);

    public void SetAveragingPeriodLabels(IEnumerable<string> values) => ReplaceItems(AveragingPeriodLabels, values);

    public void SetBphLabels(IEnumerable<string> values) => ReplaceItems(BphLabels, values);

    public void SetSimBphLabels(IEnumerable<string> values) => ReplaceItems(SimBphLabels, values);

    public void SetStarting() => SetRunState(RunUiState.Starting);

    public void SetRunning() => SetRunState(RunUiState.Running);

    public void SetPaused() => SetRunState(RunUiState.Paused);

    public void SetStopping() => SetRunState(RunUiState.Stopping);

    public void SetStopped() => SetRunState(RunUiState.Stopped);

    private void SetRunState(RunUiState value)
    {
        if (_runState == value)
        {
            return;
        }

        // Leaving pause ends review mode. The cursor must clear BEFORE the
        // state mutates: MainWindow's re-route of the kept frame is gated on
        // RunState == Paused, so clearing afterwards never re-renders and a
        // stop from a scrubbed pause would leave the dotted cursor line on
        // screen (resume relied on the next live frame by luck).
        if (_runState == RunUiState.Paused)
        {
            ReviewCursorTimeS = null;
        }

        _runState = value;
        OnPropertyChanged(nameof(RunState));
        OnPropertyChanged(nameof(IsReviewBarVisible));
        OnPropertyChanged(nameof(AreRunParametersEnabled));
        OnPropertyChanged(nameof(IsPlayPauseEnabled));
        OnPropertyChanged(nameof(IsStopEnabled));
        OnPropertyChanged(nameof(IsSampleRateEnabled));
        OnPropertyChanged(nameof(PlayPauseButtonText));
        OnPropertyChanged(nameof(IsPlayPauseButtonShowingPause));
        OnPropertyChanged(nameof(IsPlayPauseButtonShowingPlay));
        _playPauseCommand.NotifyCanExecuteChanged();
        _stopCommand.NotifyCanExecuteChanged();
        _refreshDevicesCommand.NotifyCanExecuteChanged();
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private static void ReplaceItems(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (string value in values)
        {
            target.Add(value);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
