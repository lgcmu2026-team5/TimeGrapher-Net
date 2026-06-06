namespace TimeGrapher.App.Services;

internal sealed class QueuedRecordingWriterFactory : IRecordingWriterFactory
{
    public IRecordingWriter Create() => new QueuedRecordingWriter();
}
