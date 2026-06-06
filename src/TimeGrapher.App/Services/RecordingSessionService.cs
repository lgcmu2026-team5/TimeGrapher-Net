namespace TimeGrapher.App.Services;

internal readonly record struct RecordingSessionStartResult(bool ShouldContinue, IRecordingWriter? Writer);

internal sealed class RecordingSessionService
{
    private readonly ITimeGrapherDialogService _dialogs;
    private readonly IRecordingWriterFactory _writerFactory;

    public RecordingSessionService(ITimeGrapherDialogService dialogs, IRecordingWriterFactory writerFactory)
    {
        _dialogs = dialogs;
        _writerFactory = writerFactory;
    }

    public async Task<RecordingSessionStartResult> TryStartAsync(int sampleRate)
    {
        RecordSessionChoice choice = await _dialogs.AskRecordSessionAsync();
        if (choice == RecordSessionChoice.No)
        {
            return new RecordingSessionStartResult(true, null);
        }

        if (choice == RecordSessionChoice.Cancel)
        {
            return new RecordingSessionStartResult(false, null);
        }

        string? fileName = await _dialogs.PickSaveWavAsync();
        if (string.IsNullOrEmpty(fileName))
        {
            return new RecordingSessionStartResult(false, null);
        }

        IRecordingWriter writer = _writerFactory.Create();
        if (!writer.Open(fileName, sampleRate, channels: 1))
        {
            await _dialogs.ShowErrorAsync("Error", "Failed to open WAV file");
            writer.Dispose();
            return new RecordingSessionStartResult(false, null);
        }

        return new RecordingSessionStartResult(true, writer);
    }
}
