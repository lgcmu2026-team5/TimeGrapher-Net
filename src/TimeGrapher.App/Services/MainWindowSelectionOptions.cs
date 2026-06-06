namespace TimeGrapher.App.Services;

internal sealed class MainWindowSelectionOptions
{
    public MainWindowSelectionOptions(
        string liveModeName,
        string playbackModeName,
        string playbackOrSimulationDeviceName,
        IReadOnlyList<string> preferredLiveDeviceNames,
        IReadOnlyList<int> averagingPeriods)
    {
        LiveModeName = liveModeName;
        PlaybackModeName = playbackModeName;
        PlaybackOrSimulationDeviceName = playbackOrSimulationDeviceName;
        PreferredLiveDeviceNames = preferredLiveDeviceNames;
        AveragingPeriods = averagingPeriods;
    }

    public string LiveModeName { get; }

    public string PlaybackModeName { get; }

    public string PlaybackOrSimulationDeviceName { get; }

    public IReadOnlyList<string> PreferredLiveDeviceNames { get; }

    public IReadOnlyList<int> AveragingPeriods { get; }
}
