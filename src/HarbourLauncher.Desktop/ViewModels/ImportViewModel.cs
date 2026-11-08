using System.Windows.Input;
using HarbourLauncher.Core;
using HarbourLauncher.Core.Sharing;

namespace HarbourLauncher.Desktop.ViewModels;

/// <summary>The "import a shared game" prompt for a .marinapack file.</summary>
public sealed class ImportViewModel : ObservableObject
{
    private readonly PortManager manager;
    private readonly UiServices ui;
    private readonly Func<string, bool> isGameBusy;
    private readonly Func<Task> imported;

    private PackInfo? pack;
    private bool isOpen;
    private bool isWorking;
    private bool isIndeterminate = true;
    private double progress;
    private string busyText = "";
    private string? error;

    public ImportViewModel(PortManager manager, UiServices ui, Func<string, bool> isGameBusy, Func<Task> imported)
    {
        this.manager = manager;
        this.ui = ui;
        this.isGameBusy = isGameBusy;
        this.imported = imported;
        ImportCommand = new RelayCommand(ImportAsync);
        CancelCommand = new RelayCommand(() => { if (!IsWorking) IsOpen = false; });
    }

    public ICommand ImportCommand { get; }
    public ICommand CancelCommand { get; }

    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }

    public bool IsWorking
    {
        get => isWorking;
        private set { if (Set(ref isWorking, value)) Raise(nameof(CanAct)); }
    }

    public bool CanAct => !IsWorking;
    public bool IsIndeterminate { get => isIndeterminate; private set => Set(ref isIndeterminate, value); }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public string BusyText { get => busyText; private set => Set(ref busyText, value); }

    public string? Error
    {
        get => error;
        private set { if (Set(ref error, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    // ---- What the pack holds ----

    public string Title => pack is null ? "" : $"Import {pack.Port.Name}?";
    public string GameLine => pack is null ? "" : $"{pack.Port.Game} · {Path.GetFileName(pack.File)}";

    private static string Size(long bytes) =>
        bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / (1024.0 * 1024):N1} MB";

    private bool WillInstallFromPack => pack is not null && pack.Manifest.HasProgram && manager.GetInstalled(pack.Port) is null;

    public string ContentsText
    {
        get
        {
            if (pack is null) return "";
            var m = pack.Manifest;
            string files = m.Files.Count == 1 ? "1 file" : $"{m.Files.Count} files";
            string art = m.Artwork is not null ? " Includes custom artwork." : "";
            string game = m.HasProgram
                ? $"The game: {pack.Port.Name}{(m.Tag is { } t ? " " + t : "")} ({Size(pack.ProgramBytes)}).\n"
                : "";
            return $"{game}Saves, settings and mods: {files} ({Size(pack.Bytes)}), shared {m.ExportedAt.ToLocalTime():d}.{art}";
        }
    }

    public string WhatHappensText
    {
        get
        {
            if (pack is null) return "";
            var installed = manager.GetInstalled(pack.Port);
            string name = pack.Port.Name;
            if (installed is not null)
            {
                string version = installed.IsVersionKnown ? $" {installed.Tag}" : "";
                return $"You already have {name}{version}, so it stays as it is; only the saves, settings and mods are added. " +
                       "Files the pack replaces are backed up first, to .marina\\import-backup.";
            }
            return pack.Manifest.HasProgram
                ? $"{name} will be installed from the pack, with no download. Updating it later is up to you."
                : $"This pack doesn't include the game, so the latest {name} will be downloaded from GitHub first.";
        }
    }

    public bool ShowTrustWarning => WillInstallFromPack;

    public string TrustText => pack is null ? ""
        : $"This pack includes {pack.Port.Name}'s program, which runs when you press Play. Only import packs from people you trust.";

    public string RomText => pack is null ? ""
        : $"The pack has no game data. If {pack.Port.Name} hasn't been set up on this PC yet, it will ask for your own ROM the first time it starts.";

    /// <summary>Opens a pack and shows the prompt, or a notice if the file can't be used.</summary>
    public void Open(string file)
    {
        if (IsWorking) return;
        try
        {
            pack = manager.ReadPack(file);
        }
        catch (Exception e) when (e is PackException or IOException or UnauthorizedAccessException)
        {
            ui.ShowNotice?.Invoke(e.Message);
            return;
        }
        Error = null;
        Raise(nameof(Title), nameof(GameLine), nameof(ContentsText), nameof(WhatHappensText), nameof(RomText),
              nameof(ShowTrustWarning), nameof(TrustText));
        IsOpen = true;
    }

    private async Task ImportAsync()
    {
        if (pack is null || IsWorking) return;
        if (isGameBusy(pack.Port.Id))
        {
            Error = $"Wait for {pack.Port.Name} to finish installing or updating first.";
            return;
        }

        Error = null;
        IsIndeterminate = true;
        BusyText = "Importing…";
        IsWorking = true;
        try
        {
            var result = await manager.ImportPackAsync(pack.File, new Progress<InstallProgress>(p =>
            {
                switch (p.Stage)
                {
                    case InstallStage.Checking: IsIndeterminate = true; BusyText = "Looking up the latest release…"; break;
                    case InstallStage.Downloading: IsIndeterminate = false; Progress = p.Fraction * 100; BusyText = $"Downloading {pack.Port.Name}… {p.Fraction:P0}"; break;
                    case InstallStage.Installing: IsIndeterminate = true; BusyText = WillInstallFromPack ? $"Installing {pack.Port.Name} and your files…" : "Importing saves, settings and mods…"; break;
                    case InstallStage.Done: IsIndeterminate = false; Progress = 100; BusyText = "Done"; break;
                }
            }));
            IsOpen = false;
            string backup = result.BackupFolder is not null ? " Files it replaced are in .marina\\import-backup." : "";
            string how = result.InstalledFromPack ? "Installed from the pack, with" : result.InstalledGame ? "Downloaded the game, then added" : "Added";
            ui.ShowNotice?.Invoke($"Imported {pack.Port.Name}. {how} {result.FileCount} saves, settings and mod files.{backup}");
            await imported();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = $"The import didn't finish; your existing files were left as they were. {e.Message}";
        }
        finally
        {
            IsWorking = false;
        }
    }
}
