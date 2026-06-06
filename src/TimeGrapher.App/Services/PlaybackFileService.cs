using TimeGrapher.Core.AudioIo;

namespace TimeGrapher.App.Services;

internal sealed class PlaybackFileService
{
    private readonly ITimeGrapherDialogService _dialogs;

    public PlaybackFileService(ITimeGrapherDialogService dialogs)
    {
        _dialogs = dialogs;
    }

    public async Task<PlaybackFileSelectionResult> SelectPlaybackFileAsync(string currentDirectory)
    {
        string statusMessage = "";
        while (true)
        {
            string? picked = await _dialogs.PickOpenWavAsync(currentDirectory);
            if (picked == null)
            {
                return new PlaybackFileSelectionResult(false, null, currentDirectory, 0, statusMessage);
            }

            PlaybackFileValidationResult validation = await ValidateAsync(picked);
            if (!validation.IsValid)
            {
                statusMessage = validation.StatusMessage;
                continue;
            }

            string nextDirectory = currentDirectory;
            try
            {
                nextDirectory = Path.GetDirectoryName(Path.GetFullPath(picked)) ?? currentDirectory;
            }
            catch
            {
            }

            return new PlaybackFileSelectionResult(true, picked, nextDirectory, validation.SampleRate, null);
        }
    }

    private async Task<PlaybackFileValidationResult> ValidateAsync(string fileName)
    {
        if (!File.Exists(fileName))
        {
            return PlaybackFileValidationResult.Invalid(
                $"File {ToNativeSeparators(fileName)} could not be opened");
        }

        if (!WavProbe.TryReadFormat(fileName, out WavFormatInfo format, out _))
        {
            return PlaybackFileValidationResult.Invalid(
                $"File {ToNativeSeparators(fileName)} could not be opened");
        }

        if (!WavProbe.IsAccepted(format, WavAcceptanceProfile.PlaybackFloatMonoStandardRates))
        {
            await _dialogs.ShowErrorAsync("Error", "Invalid PCM Wave File");
            return PlaybackFileValidationResult.Invalid(
                $"File {fileName} Not a standard-rate, single channel 32-bit Float WAV file");
        }

        return PlaybackFileValidationResult.Valid(format.SampleRate);
    }

    private static string ToNativeSeparators(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private readonly record struct PlaybackFileValidationResult(bool IsValid, int SampleRate, string StatusMessage)
    {
        public static PlaybackFileValidationResult Valid(int sampleRate)
        {
            return new PlaybackFileValidationResult(true, sampleRate, "");
        }

        public static PlaybackFileValidationResult Invalid(string statusMessage)
        {
            return new PlaybackFileValidationResult(false, 0, statusMessage);
        }
    }
}
