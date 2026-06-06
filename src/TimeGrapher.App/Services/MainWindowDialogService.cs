using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace TimeGrapher.App.Services;

internal sealed class MainWindowDialogService : ITimeGrapherDialogService
{
    private readonly Window _owner;

    public MainWindowDialogService(Window owner)
    {
        _owner = owner;
    }

    public async Task<RecordSessionChoice> AskRecordSessionAsync()
    {
        var dialog = new Window
        {
            Title = "Record Session",
            Width = 360,
            Height = 150,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var result = RecordSessionChoice.No;

        var yes = new Button { Content = "Yes", Width = 80, IsDefault = false };
        var no = new Button { Content = "No", Width = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        yes.Click += (_, _) => { result = RecordSessionChoice.Yes; dialog.Close(); };
        no.Click += (_, _) => { result = RecordSessionChoice.No; dialog.Close(); };
        cancel.Click += (_, _) => { result = RecordSessionChoice.Cancel; dialog.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Record Session", FontWeight = FontWeight.Bold });
        panel.Children.Add(new TextBlock { Text = "Do you want to record this session ?", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(buttons);
        dialog.Content = panel;

        await dialog.ShowDialog(_owner);
        return result;
    }

    public async Task<string?> PickOpenWavAsync(string currentDirectory)
    {
        IStorageProvider sp = _owner.StorageProvider;
        IStorageFolder? startFolder = null;
        try
        {
            startFolder = await sp.TryGetFolderFromPathAsync(currentDirectory);
        }
        catch
        {
        }

        IReadOnlyList<IStorageFile> files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Document",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("WAV Files") { Patterns = new[] { "*.wav" } },
            },
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickSaveWavAsync()
    {
        IStorageProvider sp = _owner.StorageProvider;
        IStorageFile? file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Output File",
            DefaultExtension = "wav",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Wav Files") { Patterns = new[] { "*.wav" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } },
            },
        });

        return file?.TryGetLocalPath();
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 320,
            Height = 130,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.Close();
        var panel = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(ok);
        dialog.Content = panel;
        await dialog.ShowDialog(_owner);
    }
}
