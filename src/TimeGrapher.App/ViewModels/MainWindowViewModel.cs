using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

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
    private readonly AsyncRelayCommand _startCommand;
    private readonly RelayCommand _pauseCommand;
    private readonly RelayCommand _stopCommand;
    private readonly RelayCommand _refreshDevicesCommand;
    private RunUiState _runState = RunUiState.Stopped;
    private bool _modeAllowsSampleRate = true;
    private string _statusText = "";
    private double _gain = 100.0;
    private int _selectedInputDeviceIndex = -1;
    private int _selectedSampleRateIndex = -1;
    private int _selectedModeIndex;
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

    public MainWindowViewModel(
        Func<Task> startAsync,
        Action pauseOrResume,
        Action stop,
        Action refreshDevices)
    {
        _startCommand = new AsyncRelayCommand(startAsync, () => IsStartEnabled);
        _pauseCommand = new RelayCommand(pauseOrResume, () => IsPauseEnabled);
        _stopCommand = new RelayCommand(stop, () => IsStopEnabled);
        _refreshDevicesCommand = new RelayCommand(refreshDevices, () => AreRunParametersEnabled);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand StartCommand => _startCommand;
    public ICommand PauseCommand => _pauseCommand;
    public ICommand StopCommand => _stopCommand;
    public ICommand RefreshDevicesCommand => _refreshDevicesCommand;

    public ObservableCollection<string> InputDeviceNames { get; } = new();
    public ObservableCollection<string> SampleRateLabels { get; } = new();
    public ObservableCollection<string> ModeNames { get; } = new();
    public ObservableCollection<string> AveragingPeriodLabels { get; } = new();
    public ObservableCollection<string> BphLabels { get; } = new();
    public ObservableCollection<string> SimBphLabels { get; } = new();

    public RunUiState RunState => _runState;

    public bool AreRunParametersEnabled => _runState == RunUiState.Stopped;

    public bool IsStartEnabled => _runState == RunUiState.Stopped;

    public bool IsPauseEnabled => _runState is RunUiState.Running or RunUiState.Paused;

    public bool IsStopEnabled => _runState is RunUiState.Running or RunUiState.Paused;

    public bool IsSampleRateEnabled => AreRunParametersEnabled && _modeAllowsSampleRate;

    public string PauseButtonText => _runState == RunUiState.Paused ? "Resume" : "Pause";

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

    public int SelectedModeIndex
    {
        get => _selectedModeIndex;
        set => SetProperty(ref _selectedModeIndex, value);
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

    public void SetModeAllowsSampleRate(bool value)
    {
        if (_modeAllowsSampleRate == value)
        {
            return;
        }

        _modeAllowsSampleRate = value;
        OnPropertyChanged(nameof(IsSampleRateEnabled));
    }

    public void SetInputDeviceNames(IEnumerable<string> values) => ReplaceItems(InputDeviceNames, values);

    public void SetSampleRateLabels(IEnumerable<string> values) => ReplaceItems(SampleRateLabels, values);

    public void SetModeNames(IEnumerable<string> values) => ReplaceItems(ModeNames, values);

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

        _runState = value;
        OnPropertyChanged(nameof(RunState));
        OnPropertyChanged(nameof(AreRunParametersEnabled));
        OnPropertyChanged(nameof(IsStartEnabled));
        OnPropertyChanged(nameof(IsPauseEnabled));
        OnPropertyChanged(nameof(IsStopEnabled));
        OnPropertyChanged(nameof(IsSampleRateEnabled));
        OnPropertyChanged(nameof(PauseButtonText));
        _startCommand.NotifyCanExecuteChanged();
        _pauseCommand.NotifyCanExecuteChanged();
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
