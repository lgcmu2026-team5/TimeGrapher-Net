using System.ComponentModel;
using TimeGrapher.App.ViewModels;

namespace TimeGrapher.App.Services;

internal sealed class MainWindowSelectionCoordinator
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IMainWindowSelectionOperations _operations;
    private readonly MainWindowSelectionOptions _options;
    private int _suppressDepth;

    public MainWindowSelectionCoordinator(
        MainWindowViewModel viewModel,
        IMainWindowSelectionOperations operations,
        MainWindowSelectionOptions options)
    {
        _viewModel = viewModel;
        _operations = operations;
        _options = options;
    }

    public int CurrentInputDeviceNumber
    {
        get
        {
            int index = _viewModel.SelectedInputDeviceIndex;
            return index >= 0 && index < _operations.InputDeviceNumbers.Count
                ? _operations.InputDeviceNumbers[index]
                : -1;
        }
    }

    public string CurrentInputDeviceText => ItemText(_viewModel.InputDeviceNames, _viewModel.SelectedInputDeviceIndex);

    public string CurrentModeText => ItemText(_viewModel.ModeNames, _viewModel.SelectedModeIndex);

    private bool IsSuppressed => _suppressDepth > 0;

    public IDisposable SuppressEvents()
    {
        _suppressDepth++;
        return new SuppressionScope(this);
    }

    public void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.SelectedModeIndex):
                OnSelectedModeChanged();
                break;
            case nameof(MainWindowViewModel.SelectedInputDeviceIndex):
                OnSelectedInputDeviceChanged();
                break;
            case nameof(MainWindowViewModel.SelectedSampleRateIndex):
                OnSelectedSampleRateChanged();
                break;
            case nameof(MainWindowViewModel.Gain):
                OnGainChanged();
                break;
        }
    }

    public void SetSelectedInputDeviceIndex(int index, bool forceChanged = false)
    {
        if (_viewModel.SelectedInputDeviceIndex == index)
        {
            if (forceChanged)
            {
                OnSelectedInputDeviceChanged();
            }
            return;
        }

        _viewModel.SelectedInputDeviceIndex = index;
    }

    public void SetSelectedSampleRateIndex(int index, bool forceChanged = false)
    {
        if (_viewModel.SelectedSampleRateIndex == index)
        {
            if (forceChanged)
            {
                OnSelectedSampleRateChanged();
            }
            return;
        }

        _viewModel.SelectedSampleRateIndex = index;
    }

    public void SetSelectedModeIndex(int index, bool forceChanged = false)
    {
        if (_viewModel.SelectedModeIndex == index)
        {
            if (forceChanged)
            {
                OnSelectedModeChanged();
            }
            return;
        }

        _viewModel.SelectedModeIndex = index;
    }

    public bool SetAudioRate(int rate)
    {
        for (int i = 0; i < _operations.AvailableSampleRateCount; i++)
        {
            if (_operations.GetAvailableSampleRate(i) == rate)
            {
                SetSelectedSampleRateIndex(i);
                return true;
            }
        }

        return false;
    }

    public bool SetAudioDevice(string name)
    {
        int index = FindText(_viewModel.InputDeviceNames, name, matchContains: false);
        if (index == -1)
        {
            return false;
        }

        SetSelectedInputDeviceIndex(index);
        return true;
    }

    private void OnSelectedModeChanged()
    {
        if (IsSuppressed)
        {
            return;
        }

        string mode = CurrentModeText;
        if (mode != _options.LiveModeName)
        {
            SetAudioDevice(_options.PlaybackOrSimulationDeviceName);
        }

        _viewModel.SetModeAllowsSampleRate(mode != _options.PlaybackModeName);
        if (mode != _options.LiveModeName)
        {
            return;
        }

        foreach (string preferredName in _options.PreferredLiveDeviceNames)
        {
            int index = FindText(_viewModel.InputDeviceNames, preferredName, matchContains: true);
            if (index != -1)
            {
                SetSelectedInputDeviceIndex(index);
                return;
            }
        }

        for (int i = 0; i < _viewModel.InputDeviceNames.Count; ++i)
        {
            if (ItemText(_viewModel.InputDeviceNames, i) != _options.PlaybackOrSimulationDeviceName)
            {
                SetSelectedInputDeviceIndex(i);
                return;
            }
        }
    }

    private void OnSelectedInputDeviceChanged()
    {
        if (IsSuppressed)
        {
            return;
        }

        int deviceNumber;
        if (CurrentInputDeviceText != _options.PlaybackOrSimulationDeviceName)
        {
            deviceNumber = CurrentInputDeviceNumber;

            int index = FindText(_viewModel.ModeNames, _options.LiveModeName, matchContains: false);
            if (index != -1)
            {
                SetSelectedModeIndex(index);
            }
        }
        else
        {
            deviceNumber = -1;
            if (CurrentModeText == _options.LiveModeName)
            {
                int index = FindText(_viewModel.ModeNames, _options.PlaybackModeName, matchContains: false);
                if (index != -1)
                {
                    SetSelectedModeIndex(index);
                }
            }
        }

        _operations.PopulateSampleRates(deviceNumber);
    }

    private void OnSelectedSampleRateChanged()
    {
        if (IsSuppressed)
        {
            return;
        }

        int index = _viewModel.SelectedSampleRateIndex;
        if (index < 0 || index >= _operations.AvailableSampleRateCount)
        {
            return;
        }

        int sampleRate = _operations.GetAvailableSampleRate(index);
        _operations.SetCurrentSampleRate(sampleRate);
    }

    private void OnGainChanged()
    {
        _operations.SetAudioInputVolume((float)(_viewModel.Gain / 1000.0));
    }

    internal static string ItemText(IReadOnlyList<string> items, int index)
    {
        return index >= 0 && index < items.Count ? items[index] : "";
    }

    internal static int FindText(IReadOnlyList<string> items, string text, bool matchContains)
    {
        for (int i = 0; i < items.Count; i++)
        {
            string itemText = ItemText(items, i);
            if (matchContains)
            {
                if (itemText.Contains(text, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            else if (itemText == text)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class SuppressionScope : IDisposable
    {
        private MainWindowSelectionCoordinator? _owner;

        public SuppressionScope(MainWindowSelectionCoordinator owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            MainWindowSelectionCoordinator? owner = _owner;
            if (owner == null)
            {
                return;
            }

            owner._suppressDepth--;
            _owner = null;
        }
    }
}
