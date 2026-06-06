using TimeGrapher.Core.Shared;
using TimeGrapher.Platform.WindowsAudio;

namespace TimeGrapher.App.Audio;

internal sealed class WindowsLiveAudioWorker : ILiveAudioWorker
{
    private readonly AudioCaptureWorker _inner;

    public WindowsLiveAudioWorker(MasterAudioBuffer buffer)
    {
        _inner = new AudioCaptureWorker(buffer);
    }

    public event Action? DataReady
    {
        add => _inner.DataReady += value;
        remove => _inner.DataReady -= value;
    }

    public void Start(int deviceNumber, int sampleRate, float volume)
    {
        _inner.Start(deviceNumber, sampleRate, volume);
    }

    public void SetVolume(float volume)
    {
        _inner.SetVolume(volume);
    }

    public bool IsPaused => _inner.IsPaused;

    public void SetPaused(bool paused)
    {
        _inner.SetPaused(paused);
    }

    public bool TryStop(TimeSpan timeout)
    {
        return _inner.TryStop(timeout);
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}
