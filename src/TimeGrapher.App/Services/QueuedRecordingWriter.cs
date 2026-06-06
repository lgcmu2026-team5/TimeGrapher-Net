using TimeGrapher.Core.AudioIo;

namespace TimeGrapher.App.Services;

internal sealed class QueuedRecordingWriter : IRecordingWriter
{
    private readonly QueuedWavStreamWriter _inner;

    public QueuedRecordingWriter()
    {
        _inner = new QueuedWavStreamWriter();
    }

    public ulong DroppedBlocks => _inner.DroppedBlocks;

    public bool Open(string filePath, int sampleRate, int channels) => _inner.Open(filePath, sampleRate, channels);

    public bool Write(ReadOnlySpan<float> samples) => _inner.Write(samples);

    public bool Close() => _inner.Close();

    public void Dispose() => _inner.Dispose();
}
