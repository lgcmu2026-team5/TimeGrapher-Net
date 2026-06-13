using TimeGrapher.App.ViewModels;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void InitialStateEnablesStartAndSettingsOnly()
    {
        var vm = CreateViewModel();

        Assert.Equal(RunUiState.Stopped, vm.RunState);
        Assert.True(vm.IsPlayPauseEnabled);
        Assert.False(vm.IsStopEnabled);
        Assert.True(vm.AreRunParametersEnabled);
        Assert.True(vm.IsSampleRateEnabled);
        Assert.True(vm.IsGainEnabled);
        Assert.Equal("Start", vm.PlayPauseButtonText);
        Assert.True(vm.IsPlayPauseButtonShowingPlay);
        Assert.False(vm.IsPlayPauseButtonShowingPause);
        Assert.True(vm.PlayPauseCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));
        Assert.True(vm.RefreshDevicesCommand.CanExecute(null));
    }

    [Fact]
    public void RunningPausedAndStoppingStatesExposeExpectedCommands()
    {
        var vm = CreateViewModel();

        vm.SetRunning();

        Assert.True(vm.IsPlayPauseEnabled);
        Assert.True(vm.IsStopEnabled);
        Assert.False(vm.AreRunParametersEnabled);
        Assert.False(vm.IsSampleRateEnabled);
        Assert.True(vm.IsGainEnabled); // live knob: stays adjustable mid-run
        Assert.Equal("Pause", vm.PlayPauseButtonText);
        Assert.False(vm.IsPlayPauseButtonShowingPlay);
        Assert.True(vm.IsPlayPauseButtonShowingPause);
        Assert.True(vm.PlayPauseCommand.CanExecute(null));
        Assert.True(vm.StopCommand.CanExecute(null));
        Assert.False(vm.RefreshDevicesCommand.CanExecute(null));

        vm.SetPaused();

        Assert.True(vm.IsPlayPauseEnabled);
        Assert.True(vm.IsStopEnabled);
        Assert.Equal("Resume", vm.PlayPauseButtonText);
        Assert.True(vm.IsPlayPauseButtonShowingPlay);
        Assert.False(vm.IsPlayPauseButtonShowingPause);

        vm.SetStopping();

        Assert.False(vm.IsPlayPauseEnabled);
        // Stop stays enabled in Stopping so a failed/timed-out stop can be retried.
        Assert.True(vm.IsStopEnabled);
        Assert.True(vm.StopCommand.CanExecute(null));
        Assert.False(vm.AreRunParametersEnabled);
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
    public void SampleRateRequiresStoppedStateWhileGainIsModeGatedOnly()
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
        Assert.True(vm.IsGainEnabled); // live knob: stays adjustable mid-run

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
        Assert.False(vm.PllEventVeto);
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
    public void PositionSelectionKeepsTheAlwaysVisibleLabelInSync()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Default: CH (dial up), already visible before any selection.
        Assert.Equal((int)WatchPosition.CH, vm.SelectedPositionIndex);
        Assert.Equal("POS CH", vm.PositionLabel);

        vm.SelectedPositionIndex = (int)WatchPosition.P6H;

        Assert.Equal("POS 6H", vm.PositionLabel);
        Assert.Contains(nameof(MainWindowViewModel.SelectedPositionIndex), raised);
        Assert.Contains(nameof(MainWindowViewModel.PositionLabel), raised);
    }

    [Fact]
    public void ResetSequenceCommandRaisesTheForwardingEvent()
    {
        var vm = CreateViewModel();
        int requests = 0;
        vm.ResetSequenceRequested += () => requests++;

        vm.ResetSequenceCommand.Execute(null);

        Assert.Equal(1, requests);
    }

    [Fact]
    public void ReviewBarShowsOnlyWhilePaused()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(vm.IsReviewBarVisible);

        vm.SetRunning();
        Assert.False(vm.IsReviewBarVisible);

        vm.SetPaused();
        Assert.True(vm.IsReviewBarVisible);
        Assert.Contains(nameof(MainWindowViewModel.IsReviewBarVisible), raised);

        vm.SetRunning();
        Assert.False(vm.IsReviewBarVisible);
    }

    [Fact]
    public void LeavingPauseClearsTheReviewCursor()
    {
        var vm = CreateViewModel();
        vm.UpdateReviewMaximum(100.0);
        vm.SetRunning();
        vm.SetPaused();
        vm.ReviewCursorTimeS = 42.0;

        vm.SetRunning();

        Assert.Null(vm.ReviewCursorTimeS);

        // Stop from pause must not leak a stale cursor into the next run either.
        vm.SetPaused();
        vm.ReviewCursorTimeS = 17.0;
        vm.SetStopped();

        Assert.Null(vm.ReviewCursorTimeS);
    }

    [Theory]
    // The transitions production actually takes out of pause: stop is
    // Paused -> Stopping (RunCommandService.Stop), resume is Paused -> Running
    // (TogglePause). Stopped is kept as the defensive direct hop so a future
    // regression cannot hide behind conditioning the clear on the target state.
    [InlineData((int)RunUiState.Stopping)]
    [InlineData((int)RunUiState.Running)]
    [InlineData((int)RunUiState.Stopped)]
    public void ReviewCursorClearsWhileStillPausedSoTheReRouteGateSeesIt(int exitStateValue)
    {
        // MainWindow re-renders the kept frame on cursor changes only while
        // RunState == Paused; the clearing notification must therefore arrive
        // BEFORE the state flips, or the dotted cursor stays on screen after a
        // stop from a scrubbed pause. (int-typed theory data: a public test
        // method cannot expose the internal RunUiState as a parameter type.)
        var exitState = (RunUiState)exitStateValue;
        var vm = CreateViewModel();
        vm.UpdateReviewMaximum(100.0);
        vm.SetRunning();
        vm.SetPaused();
        vm.ReviewCursorTimeS = 42.0;

        RunUiState? stateAtClear = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.ReviewCursorTimeS) &&
                vm.ReviewCursorTimeS == null)
            {
                stateAtClear = vm.RunState;
            }
        };

        switch (exitState)
        {
            case RunUiState.Stopping:
                vm.SetStopping();
                break;
            case RunUiState.Running:
                vm.SetRunning();
                break;
            default:
                vm.SetStopped();
                break;
        }

        Assert.Equal(RunUiState.Paused, stateAtClear);
        Assert.Equal(exitState, vm.RunState);
    }

    [Fact]
    public void ReviewCursorClampsToTheCapturedRange()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        vm.UpdateReviewMaximum(60.0);
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ReviewCursorTimeS = -5.0;
        Assert.Equal(0.0, vm.ReviewCursorTimeS);

        vm.ReviewCursorTimeS = 120.0;
        Assert.Equal(60.0, vm.ReviewCursorTimeS);

        Assert.Contains(nameof(MainWindowViewModel.ReviewCursorTimeS), raised);
        Assert.Contains(nameof(MainWindowViewModel.ReviewSliderValueS), raised);
        Assert.Contains(nameof(MainWindowViewModel.ReviewReadoutText), raised);
    }

    [Fact]
    public void ReviewStepCommandsScrubFromTheLatestReading()
    {
        var vm = CreateViewModel();
        vm.UpdateReviewMaximum(90.0);

        // Live (null cursor): the first step back starts at the newest reading.
        vm.ReviewStepBackCommand.Execute(null);
        Assert.Equal(89.0, vm.ReviewCursorTimeS);

        vm.ReviewStepBackCommand.Execute(null);
        Assert.Equal(88.0, vm.ReviewCursorTimeS);

        vm.ReviewStepForwardCommand.Execute(null);
        vm.ReviewStepForwardCommand.Execute(null);
        vm.ReviewStepForwardCommand.Execute(null);
        Assert.Equal(90.0, vm.ReviewCursorTimeS); // clamped at the captured end

        vm.ReviewLiveCommand.Execute(null);
        Assert.Null(vm.ReviewCursorTimeS);
    }

    [Fact]
    public void ReviewSliderEchoOfTheEffectiveValueStaysLive()
    {
        var vm = CreateViewModel();
        vm.UpdateReviewMaximum(30.0);

        // The slider re-applies its value when its Maximum binding moves; an
        // echo of the current effective value must not enter review mode.
        vm.ReviewSliderValueS = 30.0;
        Assert.Null(vm.ReviewCursorTimeS);

        vm.ReviewSliderValueS = 12.5;
        Assert.Equal(12.5, vm.ReviewCursorTimeS);
        Assert.Equal(12.5, vm.ReviewSliderValueS);
    }

    [Fact]
    public void ReviewMaximumIsMonotonicAndDrivesTheReadout()
    {
        var vm = CreateViewModel();

        Assert.Equal("LIVE 00:00", vm.ReviewReadoutText);

        vm.UpdateReviewMaximum(754.0);
        Assert.Equal(754.0, vm.ReviewMaximumS);
        Assert.Equal("LIVE 12:34", vm.ReviewReadoutText);

        // Late or history-less frames never shrink the captured range.
        vm.UpdateReviewMaximum(0.0);
        Assert.Equal(754.0, vm.ReviewMaximumS);

        vm.ReviewCursorTimeS = 83.4;
        Assert.Equal("REVIEW 01:23 / 12:34", vm.ReviewReadoutText);
    }

    [Fact]
    public void ResetReviewClearsCursorAndCapturedRange()
    {
        var vm = CreateViewModel();
        vm.UpdateReviewMaximum(120.0);
        vm.ReviewCursorTimeS = 60.0;

        vm.ResetReview();

        Assert.Null(vm.ReviewCursorTimeS);
        Assert.Equal(0.0, vm.ReviewMaximumS);
        Assert.Equal(0.0, vm.ReviewSliderValueS);
        Assert.Equal("LIVE 00:00", vm.ReviewReadoutText);
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
