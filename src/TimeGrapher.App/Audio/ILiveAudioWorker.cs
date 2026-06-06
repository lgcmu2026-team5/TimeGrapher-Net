using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Audio;

internal interface ILiveAudioWorker : IAudioInputWorker
{
    void Start(int deviceNumber, int sampleRate, float volume);

    void SetVolume(float volume);
}
