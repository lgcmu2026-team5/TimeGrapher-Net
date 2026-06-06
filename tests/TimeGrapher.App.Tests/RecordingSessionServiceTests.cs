using TimeGrapher.App.Services;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class RecordingSessionServiceTests
{
    [Fact]
    public async Task NoChoiceContinuesWithoutWriter()
    {
        var dialogs = new FakeDialogs { Choice = RecordSessionChoice.No };
        var factory = new FakeWriterFactory();
        var service = new RecordingSessionService(dialogs, factory);

        RecordingSessionStartResult result = await service.TryStartAsync(48000);

        Assert.True(result.ShouldContinue);
        Assert.Null(result.Writer);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task CancelChoiceAbortsWithoutWriter()
    {
        var dialogs = new FakeDialogs { Choice = RecordSessionChoice.Cancel };
        var factory = new FakeWriterFactory();
        var service = new RecordingSessionService(dialogs, factory);

        RecordingSessionStartResult result = await service.TryStartAsync(48000);

        Assert.False(result.ShouldContinue);
        Assert.Null(result.Writer);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task YesChoiceWithCancelledSaveAbortsWithoutWriter()
    {
        var dialogs = new FakeDialogs { Choice = RecordSessionChoice.Yes, SavePath = null };
        var factory = new FakeWriterFactory();
        var service = new RecordingSessionService(dialogs, factory);

        RecordingSessionStartResult result = await service.TryStartAsync(48000);

        Assert.False(result.ShouldContinue);
        Assert.Null(result.Writer);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task YesChoiceOpensWriterAtCurrentSampleRate()
    {
        var dialogs = new FakeDialogs { Choice = RecordSessionChoice.Yes, SavePath = "session.wav" };
        var writer = new FakeWriter { OpenResult = true };
        var factory = new FakeWriterFactory { Writer = writer };
        var service = new RecordingSessionService(dialogs, factory);

        RecordingSessionStartResult result = await service.TryStartAsync(96000);

        Assert.True(result.ShouldContinue);
        Assert.Same(writer, result.Writer);
        Assert.Equal("session.wav", writer.OpenedPath);
        Assert.Equal(96000, writer.OpenedSampleRate);
        Assert.Equal(1, writer.OpenedChannels);
        Assert.False(writer.Disposed);
        Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task WriterOpenFailureShowsErrorAndDisposesWriter()
    {
        var dialogs = new FakeDialogs { Choice = RecordSessionChoice.Yes, SavePath = "bad.wav" };
        var writer = new FakeWriter { OpenResult = false };
        var factory = new FakeWriterFactory { Writer = writer };
        var service = new RecordingSessionService(dialogs, factory);

        RecordingSessionStartResult result = await service.TryStartAsync(48000);

        Assert.False(result.ShouldContinue);
        Assert.Null(result.Writer);
        Assert.True(writer.Disposed);
        Assert.Equal(new[] { ("Error", "Failed to open WAV file") }, dialogs.Errors);
    }

    private sealed class FakeDialogs : ITimeGrapherDialogService
    {
        public RecordSessionChoice Choice { get; init; }
        public string? SavePath { get; init; }
        public List<(string Title, string Message)> Errors { get; } = new();

        public Task<RecordSessionChoice> AskRecordSessionAsync() => Task.FromResult(Choice);

        public Task<string?> PickOpenWavAsync(string currentDirectory)
        {
            _ = currentDirectory;
            return Task.FromResult<string?>(null);
        }

        public Task<string?> PickSaveWavAsync() => Task.FromResult(SavePath);

        public Task ShowErrorAsync(string title, string message)
        {
            Errors.Add((title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWriterFactory : IRecordingWriterFactory
    {
        public int CreateCount { get; private set; }
        public FakeWriter Writer { get; init; } = new() { OpenResult = true };

        public IRecordingWriter Create()
        {
            CreateCount++;
            return Writer;
        }
    }

    private sealed class FakeWriter : IRecordingWriter
    {
        public bool OpenResult { get; init; }
        public string? OpenedPath { get; private set; }
        public int OpenedSampleRate { get; private set; }
        public int OpenedChannels { get; private set; }
        public bool Disposed { get; private set; }
        public ulong DroppedBlocks => 0;

        public bool Open(string filePath, int sampleRate, int channels)
        {
            OpenedPath = filePath;
            OpenedSampleRate = sampleRate;
            OpenedChannels = channels;
            return OpenResult;
        }

        public bool Write(ReadOnlySpan<float> samples)
        {
            _ = samples;
            return true;
        }

        public bool Close() => true;

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
