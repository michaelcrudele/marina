using System.Collections.ObjectModel;
using System.Windows.Input;
using HarbourLauncher.Core;
using HarbourLauncher.Core.Backup;

namespace HarbourLauncher.Desktop.ViewModels;

/// <summary>One line in the backup dialog, e.g. "Mods · 2.21 GB".</summary>
public sealed record BackupPartRow(string Label, string SizeText, string Detail);

/// <summary>The "back up game" dialog: shows what the backup holds, then writes a .marinabackup (e.g. to a USB drive).</summary>
public sealed class BackupViewModel : ObservableObject
{
    private readonly PortManager manager;
    private readonly UiServices ui;
    private GameViewModel? game;
    private BackupContents? contents;
    private CancellationTokenSource? cancel;
    private bool isOpen;
    private bool isWorking;
    private double progress;
    private string? error;

    public BackupViewModel(PortManager manager, UiServices ui)
    {
        this.manager = manager;
        this.ui = ui;
        SaveCommand = new RelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => { if (IsWorking) cancel?.Cancel(); else IsOpen = false; });
    }

    public ObservableCollection<BackupPartRow> Parts { get; } = [];
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }

    public bool IsWorking
    {
        get => isWorking;
        private set { if (Set(ref isWorking, value)) Raise(nameof(CanSave), nameof(CancelLabel)); }
    }

    public bool CanSave => !IsWorking;
    public string CancelLabel => IsWorking ? "Stop" : "Cancel";
    public double Progress { get => progress; private set { if (Set(ref progress, value)) Raise(nameof(ProgressText)); } }
    public string ProgressText => $"Backing up… {Progress / 100:P0}";

    public string? Error
    {
        get => error;
        private set { if (Set(ref error, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    public string Title => game is null ? "" : $"Back up {game.Name}";
    public string TotalText => contents is null ? "" : $"Backup size: about {ShareViewModel.FormatSize(contents.Bytes)}";

    public void Open(GameViewModel target)
    {
        if (IsWorking) return;
        if (target.IsBusy)
        {
            ui.ShowNotice?.Invoke($"Wait for {target.Name} to finish what it's doing first.");
            return;
        }
        try
        {
            contents = manager.GetBackupContents(target.Port);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ui.ShowNotice?.Invoke($"Couldn't read {target.Name}'s folder. {e.Message}");
            return;
        }

        game = target;
        Parts.Clear();
        foreach (var part in contents.Parts)
            Parts.Add(new BackupPartRow(part.Label, ShareViewModel.FormatSize(part.Bytes),
                                        part.FileCount == 1 ? "1 file" : $"{part.FileCount} files"));
        Error = null;
        Progress = 0;
        Raise(nameof(Title), nameof(TotalText));
        IsOpen = true;
    }

    private async Task SaveAsync()
    {
        if (game is null || IsWorking || ui.SaveBackupAsync is null) return;
        var target = game;
        string? file = await ui.SaveBackupAsync(Title, $"{target.Name} backup {DateTime.Now:yyyy-MM-dd}.marinabackup");
        if (file is null) return;

        Error = null;
        Progress = 0;
        cancel = new CancellationTokenSource();
        IsWorking = true;
        target.BeginExternalWork("Backing up…");
        try
        {
            var token = cancel.Token;
            var report = new Progress<double>(f => Progress = f * 100);
            var result = await Task.Run(() => manager.BackUpGame(target.Port, file, report, token), token);
            IsOpen = false;
            ui.ShowNotice?.Invoke($"Backed up {target.Name} to {result.File} ({ShareViewModel.FormatSize(result.Bytes)}).");
        }
        catch (OperationCanceledException)
        {
            Error = "Stopped. No backup was saved.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or BackupException or GameRunningException)
        {
            Error = $"Couldn't save the backup. {e.Message}";
        }
        finally
        {
            target.EndExternalWork();
            IsWorking = false;
            cancel.Dispose();
            cancel = null;
        }
    }
}

/// <summary>The "restore backup" dialog for a .marinabackup file.</summary>
public sealed class RestoreViewModel : ObservableObject
{
    private readonly PortManager manager;
    private readonly UiServices ui;
    private readonly Func<string, bool> isGameBusy;
    private readonly Func<Task> restored;
    private BackupInfo? backup;
    private CancellationTokenSource? cancel;
    private bool isOpen;
    private bool isWorking;
    private double progress;
    private string? error;

    public RestoreViewModel(PortManager manager, UiServices ui, Func<string, bool> isGameBusy, Func<Task> restored)
    {
        this.manager = manager;
        this.ui = ui;
        this.isGameBusy = isGameBusy;
        this.restored = restored;
        RestoreCommand = new RelayCommand(RestoreAsync);
        CancelCommand = new RelayCommand(() => { if (IsWorking) cancel?.Cancel(); else IsOpen = false; });
    }

    public ICommand RestoreCommand { get; }
    public ICommand CancelCommand { get; }

    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }

    public bool IsWorking
    {
        get => isWorking;
        private set { if (Set(ref isWorking, value)) Raise(nameof(CanRestore), nameof(CancelLabel)); }
    }

    public bool CanRestore => !IsWorking;
    public string CancelLabel => IsWorking ? "Stop" : "Cancel";
    public double Progress { get => progress; private set { if (Set(ref progress, value)) Raise(nameof(ProgressText)); } }
    public string ProgressText => $"Restoring… {Progress / 100:P0}";

    public string? Error
    {
        get => error;
        private set { if (Set(ref error, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    public string Title => backup is null ? "" : $"Restore {backup.Port.Name}?";

    public string DetailsText
    {
        get
        {
            if (backup is null) return "";
            var m = backup.Manifest;
            string version = m.Tag is { } t ? $" {t}" : "";
            return $"{backup.Port.Name}{version}, backed up on {m.Machine} on {m.CreatedAt.ToLocalTime():g}. " +
                   $"{ShareViewModel.FormatSize(m.Bytes)}: the game, its data, saves, settings and mods.";
        }
    }

    public string WhatHappensText
    {
        get
        {
            if (backup is null) return "";
            string folder = manager.InstallFolder(backup.Port);
            bool exists = Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();
            if (!exists)
                return $"{backup.Port.Name} will be restored to {folder}, ready to play.";
            var installed = manager.GetInstalled(backup.Port);
            string current = installed is { IsVersionKnown: true } ? $" {installed.Tag}" : "";
            return $"You already have {backup.Port.Name}{current} here. It won't be deleted: its folder is renamed to " +
                   $"\"{backup.Port.Id} (before restore …)\" in your games folder, and the backup goes in its place.";
        }
    }

    public string TrustText => "Only restore backups you made: a backup includes the game's program.";

    public void Open(string file)
    {
        if (IsWorking) return;
        try
        {
            backup = manager.ReadBackup(file);
        }
        catch (Exception e) when (e is BackupException or IOException or UnauthorizedAccessException)
        {
            ui.ShowNotice?.Invoke(e.Message);
            return;
        }
        Error = null;
        Progress = 0;
        Raise(nameof(Title), nameof(DetailsText), nameof(WhatHappensText));
        IsOpen = true;
    }

    private async Task RestoreAsync()
    {
        if (backup is null || IsWorking) return;
        if (isGameBusy(backup.Port.Id))
        {
            Error = $"Wait for {backup.Port.Name} to finish what it's doing first.";
            return;
        }

        Error = null;
        Progress = 0;
        cancel = new CancellationTokenSource();
        IsWorking = true;
        try
        {
            var token = cancel.Token;
            var result = await manager.RestoreBackupAsync(backup.File, new Progress<double>(f => Progress = f * 100), token);
            IsOpen = false;
            string kept = result.PreviousFolder is not null
                ? $" The copy that was already here is in \"{Path.GetFileName(result.PreviousFolder)}\"; delete it once you're happy."
                : "";
            ui.ShowNotice?.Invoke($"Restored {backup.Port.Name}.{kept}");
            await restored();
        }
        catch (OperationCanceledException)
        {
            Error = "Stopped. Nothing was changed.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                      or BackupException or GameRunningException)
        {
            Error = $"The restore didn't finish, so nothing was changed. {e.Message}";
        }
        finally
        {
            IsWorking = false;
            cancel.Dispose();
            cancel = null;
        }
    }
}
