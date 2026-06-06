namespace TimeGrapher.App.Services;

internal readonly record struct PlaybackFileSelectionResult(
    bool Selected,
    string? FilePath,
    string CurrentDirectory,
    int SampleRate,
    string? StatusMessage);
