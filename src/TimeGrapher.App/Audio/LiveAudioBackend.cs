using TimeGrapher.Core.Shared;
using TimeGrapher.Platform.WindowsAudio;

namespace TimeGrapher.App.Audio;

internal static class LiveAudioBackend
{
    private const string WindowsSoundEndpointName = "USB PnP Sound Device";
    private const string WindowsSoundMicName = "USB PnP Sound Device";
    private const int WindowsSoundMicPercentVolume = 50;

    public static bool CanCapture =>
        OperatingSystem.IsWindows() ||
        OperatingSystem.IsLinux();

    public static IReadOnlyList<LiveAudioDevice> EnumerateInputDevices()
    {
        if (OperatingSystem.IsWindows())
        {
            IReadOnlyList<string> names = AudioCaptureWorker.EnumerateInputDevices();
            var devices = new List<LiveAudioDevice>(names.Count);
            for (int i = 0; i < names.Count; i++)
            {
                devices.Add(new LiveAudioDevice(i, names[i]));
            }

            return devices;
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxLiveAudioWorker.EnumerateInputDevices();
        }

        return Array.Empty<LiveAudioDevice>();
    }

    public static IReadOnlyList<int> GetCandidateSampleRates(int deviceNumber)
    {
        if (OperatingSystem.IsWindows())
        {
            return AudioCaptureWorker.GetCandidateSampleRates(deviceNumber);
        }

        return AudioSampleRates.Standard;
    }

    public static ILiveAudioWorker CreateWorker(MasterAudioBuffer buffer)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsLiveAudioWorker(buffer);
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxLiveAudioWorker(buffer);
        }

        throw new PlatformNotSupportedException("Live audio capture is not supported on this platform.");
    }

    public static void ConfigurePreferredInput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SystemAudioControl.SetSoundParameters(
            WindowsSoundEndpointName,
            WindowsSoundMicName,
            WindowsSoundMicPercentVolume);
    }
}
