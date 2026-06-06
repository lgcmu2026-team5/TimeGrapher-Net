using TimeGrapher.App.Services;
using TimeGrapher.Core.AudioIo;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class PlaybackFileServiceTests
{
    [Fact]
    public async Task CancelledPickerReturnsNotSelected()
    {
        var dialogs = new FakeDialogs((string?)null);
        var service = new PlaybackFileService(dialogs);

        PlaybackFileSelectionResult result = await service.SelectPlaybackFileAsync("C:\\start");

        Assert.False(result.Selected);
        Assert.Null(result.FilePath);
        Assert.Equal("C:\\start", result.CurrentDirectory);
        Assert.Equal("", result.StatusMessage);
    }

    [Fact]
    public async Task MissingFileThenCancelReturnsLastStatusMessage()
    {
        string missing = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".wav");
        var dialogs = new FakeDialogs(missing, null);
        var service = new PlaybackFileService(dialogs);

        PlaybackFileSelectionResult result = await service.SelectPlaybackFileAsync("C:\\start");

        Assert.False(result.Selected);
        Assert.Contains("could not be opened", result.StatusMessage);
        Assert.Equal(2, dialogs.OpenPickerCalls);
    }

    [Fact]
    public async Task NonStandardWavShowsErrorAndKeepsPrompting()
    {
        string path = CreateFloatMonoWav(sampleRate: 44100);
        try
        {
            var dialogs = new FakeDialogs(path, null);
            var service = new PlaybackFileService(dialogs);

            PlaybackFileSelectionResult result = await service.SelectPlaybackFileAsync(Path.GetTempPath());

            Assert.False(result.Selected);
            Assert.Contains("Not a standard-rate", result.StatusMessage);
            Assert.Equal(new[] { ("Error", "Invalid PCM Wave File") }, dialogs.Errors);
            Assert.Equal(2, dialogs.OpenPickerCalls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task InvalidThenValidFileReturnsValidSelection()
    {
        string missing = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".wav");
        string valid = CreateFloatMonoWav(sampleRate: 96000);
        try
        {
            var dialogs = new FakeDialogs(missing, valid);
            var service = new PlaybackFileService(dialogs);

            PlaybackFileSelectionResult result = await service.SelectPlaybackFileAsync("C:\\start");

            Assert.True(result.Selected);
            Assert.Equal(valid, result.FilePath);
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(valid)), result.CurrentDirectory);
            Assert.Equal(96000, result.SampleRate);
            Assert.Null(result.StatusMessage);
            Assert.Equal(2, dialogs.OpenPickerCalls);
        }
        finally
        {
            File.Delete(valid);
        }
    }

    private static string CreateFloatMonoWav(int sampleRate)
    {
        string path = Path.Combine(Path.GetTempPath(), "timegrapher-playback-file-" + Guid.NewGuid().ToString("N") + ".wav");
        using var writer = new WavStreamWriter();
        Assert.True(writer.Open(path, sampleRate, channels: 1));
        Assert.True(writer.Write(new[] { 0.1f, -0.1f, 0.2f, -0.2f }));
        Assert.True(writer.Close());
        return path;
    }

    private sealed class FakeDialogs : ITimeGrapherDialogService
    {
        private readonly Queue<string?> _openResults;

        public FakeDialogs(params string?[] openResults)
        {
            _openResults = new Queue<string?>(openResults);
        }

        public int OpenPickerCalls { get; private set; }
        public List<(string Title, string Message)> Errors { get; } = new();

        public Task<RecordSessionChoice> AskRecordSessionAsync() => Task.FromResult(RecordSessionChoice.No);

        public Task<string?> PickOpenWavAsync(string currentDirectory)
        {
            _ = currentDirectory;
            OpenPickerCalls++;
            return Task.FromResult(_openResults.Count == 0 ? null : _openResults.Dequeue());
        }

        public Task<string?> PickSaveWavAsync() => Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message)
        {
            Errors.Add((title, message));
            return Task.CompletedTask;
        }
    }
}
