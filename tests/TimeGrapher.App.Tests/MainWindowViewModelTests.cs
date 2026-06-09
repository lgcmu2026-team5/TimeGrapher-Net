using TimeGrapher.App.ViewModels;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void InitialStateEnablesStartAndSettingsOnly()
    {
        var vm = CreateViewModel();

        Assert.Equal(RunUiState.Stopped, vm.RunState);
        Assert.True(vm.IsStartEnabled);
        Assert.True(vm.IsPlayPauseEnabled);
        Assert.False(vm.IsPauseEnabled);
        Assert.False(vm.IsStopEnabled);
        Assert.True(vm.AreRunParametersEnabled);
        Assert.True(vm.IsSampleRateEnabled);
        Assert.True(vm.IsGainEnabled);
        Assert.Equal("Start", vm.PlayPauseButtonText);
        Assert.True(vm.IsPlayPauseButtonShowingPlay);
        Assert.False(vm.IsPlayPauseButtonShowingPause);
        Assert.Equal("Pause", vm.PauseButtonText);
        Assert.True(vm.IsPauseButtonShowingPause);
        Assert.False(vm.IsPauseButtonShowingResume);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.True(vm.PlayPauseCommand.CanExecute(null));
        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));
        Assert.True(vm.RefreshDevicesCommand.CanExecute(null));
    }

    [Fact]
    public void RunningPausedAndStoppingStatesExposeExpectedCommands()
    {
        var vm = CreateViewModel();

        vm.SetRunning();

        Assert.False(vm.IsStartEnabled);
        Assert.True(vm.IsPlayPauseEnabled);
        Assert.True(vm.IsPauseEnabled);
        Assert.True(vm.IsStopEnabled);
        Assert.False(vm.AreRunParametersEnabled);
        Assert.False(vm.IsSampleRateEnabled);
        Assert.False(vm.IsGainEnabled);
        Assert.Equal("Pause", vm.PlayPauseButtonText);
        Assert.False(vm.IsPlayPauseButtonShowingPlay);
        Assert.True(vm.IsPlayPauseButtonShowingPause);
        Assert.Equal("Pause", vm.PauseButtonText);
        Assert.True(vm.IsPauseButtonShowingPause);
        Assert.False(vm.IsPauseButtonShowingResume);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.PlayPauseCommand.CanExecute(null));
        Assert.True(vm.PauseCommand.CanExecute(null));
        Assert.True(vm.StopCommand.CanExecute(null));
        Assert.False(vm.RefreshDevicesCommand.CanExecute(null));

        vm.SetPaused();

        Assert.True(vm.IsPauseEnabled);
        Assert.True(vm.IsPlayPauseEnabled);
        Assert.True(vm.IsStopEnabled);
        Assert.Equal("Resume", vm.PlayPauseButtonText);
        Assert.True(vm.IsPlayPauseButtonShowingPlay);
        Assert.False(vm.IsPlayPauseButtonShowingPause);
        Assert.Equal("Resume", vm.PauseButtonText);
        Assert.False(vm.IsPauseButtonShowingPause);
        Assert.True(vm.IsPauseButtonShowingResume);

        vm.SetStopping();

        Assert.False(vm.IsPauseEnabled);
        Assert.False(vm.IsPlayPauseEnabled);
        // Stop stays enabled in Stopping so a failed/timed-out stop can be retried.
        Assert.True(vm.IsStopEnabled);
        Assert.True(vm.StopCommand.CanExecute(null));
        Assert.False(vm.AreRunParametersEnabled);
        Assert.Equal("Pause", vm.PauseButtonText);
        Assert.True(vm.IsPauseButtonShowingPause);
        Assert.False(vm.IsPauseButtonShowingResume);
    }

    [Fact]
    public void PlayPauseCommandStartsThenTogglesPauseResume()
    {
        int starts = 0;
        int pauseToggles = 0;
        var vm = new MainWindowViewModel(
            () =>
            {
                starts++;
                return Task.CompletedTask;
            },
            () => pauseToggles++,
            () => { },
            () => { });

        vm.PlayPauseCommand.Execute(null);

        Assert.Equal(1, starts);
        Assert.Equal(0, pauseToggles);

        vm.SetRunning();
        vm.PlayPauseCommand.Execute(null);

        Assert.Equal(1, starts);
        Assert.Equal(1, pauseToggles);

        vm.SetPaused();
        vm.PlayPauseCommand.Execute(null);

        Assert.Equal(1, starts);
        Assert.Equal(2, pauseToggles);
    }

    [Fact]
    public void LiveOnlyControlsDependOnModeAndStoppedState()
    {
        var vm = CreateViewModel();

        vm.SetModeAllowsSampleRate(false);
        vm.SetModeAllowsGain(false);

        Assert.True(vm.AreRunParametersEnabled);
        Assert.False(vm.IsSampleRateEnabled);
        Assert.False(vm.IsGainEnabled);

        vm.SetModeAllowsSampleRate(true);
        vm.SetModeAllowsGain(true);
        vm.SetRunning();

        Assert.False(vm.AreRunParametersEnabled);
        Assert.False(vm.IsSampleRateEnabled);
        Assert.False(vm.IsGainEnabled);

        vm.SetStopped();

        Assert.True(vm.IsSampleRateEnabled);
        Assert.True(vm.IsGainEnabled);
    }

    [Fact]
    public void SettingsPropertiesExposeOriginalUiDefaults()
    {
        var vm = CreateViewModel();

        Assert.Equal(100.0, vm.Gain);
        Assert.Equal(-1, vm.SelectedInputDeviceIndex);
        Assert.Equal(-1, vm.SelectedSampleRateIndex);
        Assert.Equal(-1, vm.SelectedAveragingPeriodIndex);
        Assert.Equal(0, vm.SelectedBphIndex);
        Assert.Equal(52m, vm.LiftAngle);
        Assert.Equal(-1, vm.SelectedSimBphIndex);
        Assert.Equal(0m, vm.SimErrorRate);
        Assert.Equal(300m, vm.SimAmplitude);
        Assert.Equal(0m, vm.SimBeatError);
        Assert.True(vm.Realistic);
        Assert.Equal("200", vm.HighPassCutoffText);
        Assert.Equal(2m, vm.ScopeScale);
        Assert.False(vm.UseCOnset);
    }

    [Fact]
    public void OptionCollectionsReplacePreviousValues()
    {
        var vm = CreateViewModel();

        vm.SetInputDeviceNames(new[] { "Live: Mic A", "Playback", "Simulation" });
        vm.SetSampleRateLabels(new[] { "48000 Hz", "96000 Hz" });
        vm.SetAveragingPeriodLabels(new[] { "2s", "4s", "12s" });
        vm.SetBphLabels(new[] { "Auto BPH", "18000" });
        vm.SetSimBphLabels(new[] { "18000", "28800" });

        Assert.Equal(new[] { "Live: Mic A", "Playback", "Simulation" }, vm.InputDeviceNames);
        Assert.Equal(new[] { "48000 Hz", "96000 Hz" }, vm.SampleRateLabels);
        Assert.Equal(new[] { "2s", "4s", "12s" }, vm.AveragingPeriodLabels);
        Assert.Equal(new[] { "Auto BPH", "18000" }, vm.BphLabels);
        Assert.Equal(new[] { "18000", "28800" }, vm.SimBphLabels);

        vm.SetSampleRateLabels(new[] { "192000 Hz" });

        Assert.Equal(new[] { "192000 Hz" }, vm.SampleRateLabels);
    }

    [Fact]
    public void SelectionAndValueChangesRaisePropertyChanged()
    {
        var vm = CreateViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedInputDeviceIndex = 1;
        vm.SelectedSampleRateIndex = 0;
        vm.SelectedAveragingPeriodIndex = 6;
        vm.Gain = 250;
        vm.LiftAngle = 53m;

        Assert.Contains(nameof(MainWindowViewModel.SelectedInputDeviceIndex), changed);
        Assert.Contains(nameof(MainWindowViewModel.SelectedSampleRateIndex), changed);
        Assert.Contains(nameof(MainWindowViewModel.SelectedAveragingPeriodIndex), changed);
        Assert.Contains(nameof(MainWindowViewModel.Gain), changed);
        Assert.Contains(nameof(MainWindowViewModel.LiftAngle), changed);
    }

    [Fact]
    public void IsAwaitingBeatSyncRaisesPropertyChanged()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.IsAwaitingBeatSync = true;

        Assert.True(vm.IsAwaitingBeatSync);
        Assert.Contains(nameof(MainWindowViewModel.IsAwaitingBeatSync), raised);
    }

    private static MainWindowViewModel CreateViewModel()
    {
        return new MainWindowViewModel(
            () => Task.CompletedTask,
            () => { },
            () => { },
            () => { });
    }
}
