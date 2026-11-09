using System.Windows.Input;
using HarbourLauncher.Core;
using HarbourLauncher.Core.Backup;

namespace HarbourLauncher.Desktop.ViewModels;

/// <summary>The "uninstall game" dialog, with the option to save a backup first.</summary>
public sealed class UninstallViewModel : ObservableObject
{
    private readonly PortManager manager;
    private readonly UiServices ui;
    private readonly Action<GameViewModel> uninstalled;

    private GameViewModel? game;
    private long folderBytes;
    private long backupBytes;
    private bool backUpFirst = true;
    private CancellationTokenSource? cancel;
    private bool isOpen;
    private bool isWorking;
    private bool isIndeterminate;
    private double progress;
    private string busyText = "";
    private string? error;

    public UninstallViewModel(PortManager manager, UiServices ui, Action<GameViewModel> uninstalled)
    {
        this.manager = manager;
        this.ui = ui;
        this.uninstalled = uninstalled;
        ConfirmCommand = new RelayCommand(ConfirmAsync);
        CancelCommand = new RelayCommand(() => { if (IsWorking) cancel?.Cancel(); else IsOpen = false; });
    }

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }

    public bool IsWorking
    {
        get => isWorking;
        private set { if (Set(ref isWorking, value)) Raise(nameof(CanEdit), nameof(CancelLabel)); }
    }

    public bool CanEdit => !IsWorking;
    public string CancelLabel => IsWorking ? "Stop" : "Cancel";
    public bool IsIndeterminate { get => isIndeterminate; private set => Set(ref isIndeterminate, value); }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public string BusyText { get => busyText; private set => Set(ref busyText, value); }

    public string? Error
    {
        get => error;
        private set { if (Set(ref error, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    public bool BackUpFirst
    {
        get => backUpFirst;
        set
        {
            if (Set(ref backUpFirst, value))
                Raise(nameof(ConfirmLabel), nameof(ShowDangerButton), nameof(ShowBackupButton), nameof(ShowNoBackupWarning));
        }
    }

    public string Title => game is null ? "" : $"Uninstall {game.Name}?";

    public string WhatGetsDeleted => game is null ? "" :
        $"This deletes {manager.InstallFolder(game.Port)} and everything in it" +
        (folderBytes < 0 ? "" : $" ({ShareViewModel.FormatSize(folderBytes)})") +
        ": the game, your saves (every save profile) and settings, mods, and the game data made from your ROM.";

    public string BackupOptionText => backupBytes < 0
        ? "Back up first (measuring…)"
        : $"Back up first (about {ShareViewModel.FormatSize(backupBytes)})";

    public string BackupOptionDetail =>
        "Saves everything to a .marinabackup file, e.g. on a USB drive. Restore it any time with the import button.";

    public bool ShowNoBackupWarning => !BackUpFirst;
    public string NoBackupWarning => game is null ? "" : $"Without a backup, your {game.Name} saves are gone for good.";

    public string ConfirmLabel => BackUpFirst ? "Back up and uninstall…" : "Uninstall";
    public bool ShowBackupButton => BackUpFirst;
    public bool ShowDangerButton => !BackUpFirst;

    public string ArtworkNote => "Your custom artwork for this game is kept, in case you reinstall it.";

    /// <summary>Shows the dialog straight away; sizes fill in once the folder has been measured (off the UI thread).</summary>
    public void Open(GameViewModel target)
    {
        if (IsWorking) return;
        if (target.IsBusy)
        {
            ui.ShowNotice?.Invoke($"Wait for {target.Name} to finish what it's doing first.");
            return;
        }

        game = target;
        folderBytes = -1;
        backupBytes = -1;
        Error = null;
        Progress = 0;
        BackUpFirst = true;
        Raise(nameof(Title), nameof(WhatGetsDeleted), nameof(BackupOptionText), nameof(NoBackupWarning),
              nameof(ConfirmLabel), nameof(ShowBackupButton), nameof(ShowDangerButton), nameof(ShowNoBackupWarning));
        IsOpen = true;
        _ = MeasureAsync(target);
    }

    private async Task MeasureAsync(GameViewModel target)
    {
        try
        {
            var (folder, backup) = await Task.Run(() =>
                (manager.GetGameFolderSize(target.Port), manager.GetBackupContents(target.Port).Bytes));
            if (game != target) return; // The dialog moved on to another game.
            folderBytes = folder;
            backupBytes = backup;
            Raise(nameof(WhatGetsDeleted), nameof(BackupOptionText));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (game == target) Error = $"Couldn't read {target.Name}'s folder. {e.Message}";
        }
    }

    private async Task ConfirmAsync()
    {
        if (game is null || IsWorking) return;
        var target = game;

        string? backupFile = null;
        if (BackUpFirst)
        {
            if (ui.SaveBackupAsync is null) return;
            backupFile = await ui.SaveBackupAsync($"Back up {target.Name} before uninstalling",
                                                  $"{target.Name} backup {DateTime.Now:yyyy-MM-dd}.marinabackup");
            if (backupFile is null) return; // Picker cancelled: nothing happens.
        }

        Error = null;
        cancel = new CancellationTokenSource();
        IsWorking = true;
        target.BeginExternalWork(BackUpFirst ? "Backing up…" : "Uninstalling…");
        string? savedTo = null;
        try
        {
            var token = cancel.Token;
            if (backupFile is not null)
            {
                IsIndeterminate = false;
                Progress = 0;
                BusyText = "Backing up… 0%";
                var report = new Progress<double>(f => { Progress = f * 100; BusyText = $"Backing up… {f:P0}"; });
                var saved = await Task.Run(() => manager.BackUpGame(target.Port, backupFile, report, token), token);
                savedTo = saved.File;
            }

            // Past this point the uninstall isn't stopped half way.
            IsIndeterminate = false;
            Progress = 0;
            BusyText = "Removing the game… 0%";
            var deleting = new Progress<double>(f => { Progress = f * 100; BusyText = $"Removing the game… {f:P0}"; });
            var result = await manager.UninstallGameAsync(target.Port, deleting);

            IsOpen = false;
            string backupNote = savedTo is not null ? $" Backup saved to {savedTo}." : "";
            ui.ShowNotice?.Invoke($"Uninstalled {target.Name} and freed {ShareViewModel.FormatSize(result.FreedBytes)}.{backupNote}");
        }
        catch (OperationCanceledException)
        {
            Error = "Stopped. Nothing was uninstalled, and no backup was saved.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or BackupException or GameRunningException)
        {
            Error = savedTo is null && backupFile is not null
                ? $"The backup didn't finish, so {target.Name} wasn't uninstalled. {e.Message}"
                : e.Message;
        }
        finally
        {
            target.EndExternalWork();
            IsWorking = false;
            cancel.Dispose();
            cancel = null;
        }
        if (!IsOpen) uninstalled(target);
    }
}
