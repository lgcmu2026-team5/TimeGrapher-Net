using TimeGrapher.Core.AudioIo;

namespace TimeGrapher.App.Services;

internal interface IRecordingWriter : ISampleWriter, IDisposable
{
    ulong DroppedBlocks { get; }

    bool Open(string filePath, int sampleRate, int channels);
}
