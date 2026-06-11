using TimeGrapher.Core.Shared;
#if TIMEGRAPHER_LINUX_AUDIO
using TimeGrapher.Platform.LinuxAudio;
#endif
#if TIMEGRAPHER_WINDOWS_AUDIO
using TimeGrapher.Platform.WindowsAudio;
#endif

namespace TimeGrapher.App.Audio;

internal static class LiveAudioBackend
{
    private const string WindowsSoundEndpointName = "USB PnP Sound Device";
    private const string WindowsSoundMicName = "USB PnP Sound Device";
    private const int WindowsSoundMicPercentVolume = 50;

    public static bool CanCapture =>
#if TIMEGRAPHER_WINDOWS_AUDIO
        OperatingSystem.IsWindows() ||
#endif
#if TIMEGRAPHER_LINUX_AUDIO
        OperatingSystem.IsLinux();
#else
        false;
#endif

    public static IReadOnlyList<LiveAudioDevice> EnumerateInputDevices()
    {
#if TIMEGRAPHER_WINDOWS_AUDIO
        if (OperatingSystem.IsWindows())
        {
            return AudioCaptureWorker.EnumerateInputDevices();
        }
#endif

#if TIMEGRAPHER_LINUX_AUDIO
        if (OperatingSystem.IsLinux())
        {
            return LinuxLiveAudioWorker.EnumerateInputDevices();
        }
#endif

        return Array.Empty<LiveAudioDevice>();
    }

    public static IReadOnlyList<int> GetCandidateSampleRates(int deviceNumber)
    {
#if TIMEGRAPHER_WINDOWS_AUDIO
        if (OperatingSystem.IsWindows())
        {
            return AudioCaptureWorker.GetCandidateSampleRates(deviceNumber);
        }
#endif

        return AudioSampleRates.Standard;
    }

    public static ILiveAudioWorker CreateWorker(MasterAudioBuffer buffer)
    {
#if TIMEGRAPHER_WINDOWS_AUDIO
        if (OperatingSystem.IsWindows())
        {
            return new AudioCaptureWorker(buffer);
        }
#endif

#if TIMEGRAPHER_LINUX_AUDIO
        if (OperatingSystem.IsLinux())
        {
            return new LinuxLiveAudioWorker(buffer);
        }
#endif

        throw new PlatformNotSupportedException("Live audio capture is not supported on this platform.");
    }

    public static void ConfigurePreferredInput()
    {
#if TIMEGRAPHER_WINDOWS_AUDIO
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SystemAudioControl.SetSoundParameters(
            WindowsSoundEndpointName,
            WindowsSoundMicName,
            WindowsSoundMicPercentVolume);
#endif
    }
}
