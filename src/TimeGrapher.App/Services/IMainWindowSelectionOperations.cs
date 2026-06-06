namespace TimeGrapher.App.Services;

internal interface IMainWindowSelectionOperations
{
    IReadOnlyList<int> InputDeviceNumbers { get; }

    int AvailableSampleRateCount { get; }

    int GetAvailableSampleRate(int index);

    void PopulateSampleRates(int deviceNumber);

    void SetCurrentSampleRate(int sampleRate);

    void SetAudioInputVolume(float normalizedVolume);
}
