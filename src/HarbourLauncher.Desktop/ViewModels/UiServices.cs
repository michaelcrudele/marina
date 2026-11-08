namespace HarbourLauncher.Desktop.ViewModels;

/// <summary>Things only the window can do, handed to the view models once it exists.</summary>
public sealed class UiServices
{
    /// <summary>Shows an image file picker with this title; returns the chosen path, or null.</summary>
    public Func<string, Task<string?>>? PickImageAsync { get; set; }

    /// <summary>Shows an open picker for .marinapack and .marinabackup files with this title; returns the chosen path, or null.</summary>
    public Func<string, Task<string?>>? PickPackAsync { get; set; }

    /// <summary>Shows a .marinapack save picker (title, suggested file name); returns the chosen path, or null.</summary>
    public Func<string, string, Task<string?>>? SavePackAsync { get; set; }

    /// <summary>Shows a .marinabackup save picker (title, suggested file name); returns the chosen path, or null.</summary>
    public Func<string, string, Task<string?>>? SaveBackupAsync { get; set; }

    /// <summary>Opens the "back up game" dialog for this game.</summary>
    public Action<GameViewModel>? OpenBackup { get; set; }

    /// <summary>Opens the "uninstall game" dialog for this game.</summary>
    public Action<GameViewModel>? OpenUninstall { get; set; }

    /// <summary>Opens the "share game" dialog for this game.</summary>
    public Action<GameViewModel>? OpenShare { get; set; }

    /// <summary>Starts the newly installed Marina and closes this one.</summary>
    public Action? RestartApp { get; set; }

    /// <summary>Shows a message across the top of the window.</summary>
    public Action<string>? ShowNotice { get; set; }
}
