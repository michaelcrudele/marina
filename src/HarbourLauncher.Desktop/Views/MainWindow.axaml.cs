using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HarbourLauncher.Desktop.ViewModels;

namespace HarbourLauncher.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Coming back to the launcher (e.g. after closing a game) refreshes "last played".
        Activated += (_, _) => (DataContext as LibraryViewModel)?.RefreshLastPlayed();
    }

    /// <summary>Shows the window on top of everything else, restoring it if it was minimised.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
        // Windows doesn't always let an app raise itself; briefly making it topmost does.
        Topmost = true;
        Topmost = false;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is LibraryViewModel library)
        {
            library.Ui.PickImageAsync = PickImageAsync;
            library.Ui.PickPackAsync = PickPackAsync;
            library.Ui.SavePackAsync = SavePackAsync;
            library.Ui.SaveBackupAsync = SaveBackupAsync;
        }
    }

    private static readonly FilePickerFileType PackType = new("Marina game pack") { Patterns = ["*.marinapack"] };
    private static readonly FilePickerFileType BackupType = new("Marina backup") { Patterns = ["*.marinabackup"] };
    // Also opens .hlpack / .hlbackup files made before the launcher was renamed Marina.
    private static readonly FilePickerFileType PackOrBackupType =
        new("Marina pack or backup") { Patterns = ["*.marinapack", "*.marinabackup", "*.hlpack", "*.hlbackup"] };

    private async Task<string?> SaveBackupAsync(string title, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "marinabackup",
            FileTypeChoices = [BackupType],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickPackAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [PackOrBackupType, PackType, BackupType],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> SavePackAsync(string title, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "marinapack",
            FileTypeChoices = [PackType],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickImageAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"] },
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel library) return;

        IStorageFolder? start = null;
        try
        {
            if (Directory.Exists(library.Settings.Folder))
                start = await StorageProvider.TryGetFolderFromPathAsync(library.Settings.Folder);
        }
        catch (Exception)
        {
            // Just open the picker in its usual place.
        }

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to install games",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            library.Settings.Folder = path;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing mid-install or mid-move would leave a half-copied folder, so wait for it.
        if (DataContext is LibraryViewModel library && (library.IsAnyBusy || library.Settings.IsSaving))
        {
            e.Cancel = true;
            library.Notice = "The launcher is still installing or moving games. Close it once that finishes.";
        }
        base.OnClosing(e);
    }
}
