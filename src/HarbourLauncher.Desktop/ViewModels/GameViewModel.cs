using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using HarbourLauncher.Core;
using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;

namespace HarbourLauncher.Desktop.ViewModels;

/// <summary>One game card in the library.</summary>
public sealed class GameViewModel : ObservableObject
{
    private readonly PortManager manager;
    private readonly Action<GameViewModel> changed;
    private readonly UiServices ui;

    private InstallState? installed;
    private ReleaseInfo? latest;
    private bool isChecking;
    private bool isBusy;
    private bool isIndeterminate;
    private double progress;
    private string busyText = "";
    private string? error;
    private Bitmap? artwork;
    private bool cropped;
    private DateTimeOffset? lastPlayed;

    public GameViewModel(PortManager manager, PortDefinition port, UiServices ui, Action<GameViewModel> changed)
    {
        this.manager = manager;
        this.changed = changed;
        this.ui = ui;
        Port = port;
        installed = manager.GetInstalled(port);
        lastPlayed = manager.GetLastPlayed(port);
        Banner = MakeBanner(port.Id);

        PlayCommand = new RelayCommand(Play);
        InstallCommand = new RelayCommand(InstallAsync);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        DismissErrorCommand = new RelayCommand(() => Error = null);
        ChooseArtworkCommand = new RelayCommand(ChooseArtworkAsync);
        RemoveArtworkCommand = new RelayCommand(RemoveArtwork);
        CropArtworkCommand = new RelayCommand(() => SetCropped(true));
        ShowWholeArtworkCommand = new RelayCommand(() => SetCropped(false));
        ShareCommand = new RelayCommand(() => ui.OpenShare?.Invoke(this));
        BackupCommand = new RelayCommand(() => ui.OpenBackup?.Invoke(this));
        UninstallCommand = new RelayCommand(() => ui.OpenUninstall?.Invoke(this));
        LoadArtwork();
    }

    public PortDefinition Port { get; }
    public string Name => Port.Name;
    public string Game => Port.Game;
    public string Badge => Port.Id switch
    {
        "soh" => "OoT",
        "2ship" => "MM",
        "starship" => "SF64",
        "spaghettikart" => "MK64",
        "ghostship" => "SM64",
        "lighthouse" => "BK",
        "paperboat" => "PM64",
        _ => new string(Port.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]).Take(3).ToArray()),
    };
    public IBrush Banner { get; }

    public ICommand PlayCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand DismissErrorCommand { get; }
    public ICommand ChooseArtworkCommand { get; }
    public ICommand RemoveArtworkCommand { get; }
    public ICommand CropArtworkCommand { get; }
    public ICommand ShowWholeArtworkCommand { get; }
    public ICommand ShareCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand UninstallCommand { get; }

    // ---- Artwork ----

    /// <summary>The player's icon or box art, or null for the colour banner.</summary>
    public Bitmap? Artwork => artwork;
    public bool HasArtwork => artwork is not null;
    public bool ShowBadge => artwork is null;

    /// <summary>
    /// Portrait images (box art) fill the card's artwork area; square icons and
    /// wide images are shown whole inside it instead of being cropped.
    /// </summary>
    public bool ArtworkFills => artwork is not null && (IsPortraitArtwork || cropped);
    public bool ArtworkFits => artwork is not null && !ArtworkFills;
    private bool IsPortraitArtwork => artwork is { } a && a.PixelSize.Height >= a.PixelSize.Width * 1.2;

    /// <summary>Wide or square art can be cropped to fill the card instead; portrait art always fills.</summary>
    public bool CanCropArtwork => ArtworkFits;
    public bool CanShowWholeArtwork => artwork is not null && cropped && !IsPortraitArtwork;
    public string ArtworkStatus => HasArtwork ? "Custom image" : "Default banner";

    // ---- State shown on the card ----

    public bool IsInstalled => installed is not null;
    public bool IsNotInstalled => installed is null;
    public bool UpdateAvailable => installed is not null && latest is not null &&
                                   !string.Equals(installed.Tag, latest.Tag, StringComparison.Ordinal);
    public bool IsOffline => latest?.FromStaleCache == true;

    public string VersionText =>
        installed is { IsVersionKnown: false } ? (latest is not null ? $"Version unknown → {latest.Tag}" : "Version unknown")
        : installed is not null ? (UpdateAvailable ? $"Version {installed.Tag} → {latest!.Tag}" : $"Version {installed.Tag}")
        : latest is not null ? $"Latest version {latest.Tag}"
        : isChecking ? "Checking for the latest version…"
        : "Latest version unknown";

    public string? StatusBadge =>
        isChecking && latest is null ? null
        : UpdateAvailable ? "Update available"
        : IsInstalled && latest is not null ? "Up to date"
        : null;

    public bool HasStatusBadge => StatusBadge is not null;
    public bool StatusIsUpdate => UpdateAvailable;

    public string? OfflineText => IsOffline ? $"{latest!.StaleReason ?? "Couldn't reach GitHub"}; showing the last known version." : null;

    /// <summary>Why this game's latest version couldn't be confirmed, or null.</summary>
    public string? StaleReason => IsOffline ? latest!.StaleReason : null;

    /// <summary>When GitHub last confirmed this game's latest version.</summary>
    public DateTimeOffset? CheckedAt => latest?.CheckedAt;
    public bool HasOfflineText => IsOffline;

    /// <summary>"Played today at 8:15 PM", "Played Nov 2 at 8:15 PM", "Not played yet"…</summary>
    public string LastPlayedText => FormatLastPlayed(lastPlayed, DateTimeOffset.Now);

    /// <summary>When the game was last played, or null; the library sorts installed games by this.</summary>
    public DateTimeOffset? LastPlayed => lastPlayed;

    /// <summary>Re-reads when the game was last played, e.g. when the launcher window comes back into focus.</summary>
    public void RefreshLastPlayed()
    {
        lastPlayed = IsInstalled ? manager.GetLastPlayed(Port) : null;
        Raise(nameof(LastPlayedText));
    }

    public static string FormatLastPlayed(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is not { } w) return "Not played yet";
        var day = w.LocalDateTime.Date;
        var today = now.LocalDateTime.Date;
        string time = w.LocalDateTime.ToString("t");
        int daysAgo = (today - day).Days;
        return daysAgo switch
        {
            <= 0 => $"Played today at {time}",
            1 => $"Played yesterday at {time}",
            < 7 => $"Played {day:dddd} at {time}",
            _ when day.Year == today.Year => $"Played {day:MMM d} at {time}",
            _ => $"Played {day:MMM d, yyyy}",
        };
    }

    public bool IsBusy
    {
        get => isBusy;
        private set { if (Set(ref isBusy, value)) RaiseState(); }
    }

    public bool ShowPlay => IsInstalled && !IsBusy;
    public bool ShowUpdate => UpdateAvailable && !IsBusy;
    public bool ShowInstall => IsNotInstalled && !IsBusy;
    public bool ShowActions => !IsBusy;

    public bool IsIndeterminate { get => isIndeterminate; private set => Set(ref isIndeterminate, value); }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public string BusyText { get => busyText; private set => Set(ref busyText, value); }

    public string? Error
    {
        get => error;
        private set { if (Set(ref error, value)) { Raise(nameof(HasError)); changed(this); } }
    }

    public bool HasError => Error is not null;

    // ---- Actions ----

    private void LoadArtwork()
    {
        artwork = null;
        if (manager.GetArtwork(Port) is { } path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                // Decode at banner size: keeps memory low even for huge scans.
                artwork = Bitmap.DecodeToHeight(stream, 640); // 2x the card's artwork height.
            }
            catch (Exception)
            {
                artwork = null; // Unreadable image: fall back to the banner.
            }
        }
        cropped = artwork is not null && manager.IsArtworkCropped(Port);
        Raise(nameof(Artwork), nameof(HasArtwork), nameof(ShowBadge), nameof(ArtworkFills), nameof(ArtworkFits),
              nameof(CanCropArtwork), nameof(CanShowWholeArtwork), nameof(ArtworkStatus));
    }

    private async Task ChooseArtworkAsync()
    {
        if (ui.PickImageAsync is null) return;
        string? file = await ui.PickImageAsync($"Choose artwork for {Name}");
        if (file is null) return;

        try
        {
            manager.SetArtwork(Port, file);
            Error = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Error = $"Couldn't use that image. {e.Message}";
            return;
        }

        LoadArtwork();
        if (!HasArtwork)
        {
            manager.ClearArtwork(Port);
            Error = "That image couldn't be opened. Try saving it as a PNG or JPEG.";
        }
    }

    /// <summary>Shows progress on the card for work done elsewhere (e.g. creating a pack), and blocks updates meanwhile.</summary>
    public void BeginExternalWork(string text)
    {
        Error = null;
        IsIndeterminate = true;
        BusyText = text;
        IsBusy = true;
    }

    public void EndExternalWork() => IsBusy = false;

    private void SetCropped(bool value)
    {
        try
        {
            manager.SetArtworkCropped(Port, value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = $"Couldn't save that choice. {e.Message}";
        }
        LoadArtwork();
    }

    private void RemoveArtwork()
    {
        try
        {
            manager.ClearArtwork(Port);
            Error = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = $"Couldn't remove the artwork. {e.Message}";
        }
        LoadArtwork();
    }

    /// <summary>Re-reads what is installed, e.g. after the games folder changed.</summary>
    public void ReloadInstalled()
    {
        installed = manager.GetInstalled(Port);
        Error = null;
        RaiseState();
        LoadArtwork();
        RefreshLastPlayed();
    }

    /// <summary>Looks up the latest release. Never throws; failures show on the card.</summary>
    /// <param name="force">Ask GitHub now rather than reuse a release confirmed within the last hour.</param>
    public async Task CheckAsync(bool force = false)
    {
        isChecking = true;
        RaiseState();
        try
        {
            var status = await manager.CheckAsync(Port, force);
            installed = status.Installed;
            latest = status.Latest;
            Error = null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            installed = manager.GetInstalled(Port);
            Error = $"Couldn't check for updates. {e.Message}";
        }
        finally
        {
            isChecking = false;
            RaiseState();
            changed(this);
        }
    }

    private async Task InstallAsync()
    {
        if (IsBusy) return;
        bool wasInstalled = IsInstalled;
        Error = null;
        Progress = 0;
        IsIndeterminate = true;
        BusyText = "Looking up the latest release…";
        IsBusy = true;
        try
        {
            var result = await manager.InstallLatestAsync(Port, new Progress<InstallProgress>(OnProgress));
            installed = manager.GetInstalled(Port);
            if (result is not null && (latest is null || latest.Tag != result.Tag))
                await CheckAsync(); // Make sure "latest" matches what was just installed.
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            installed = manager.GetInstalled(Port);
            Error = wasInstalled
                ? $"The update didn't finish, so your previous version was kept. {e.Message}"
                : $"The install didn't finish. {e.Message}";
        }
        finally
        {
            IsBusy = false;
            changed(this);
        }
    }

    private void OnProgress(InstallProgress p)
    {
        switch (p.Stage)
        {
            case InstallStage.Checking:
                IsIndeterminate = true;
                BusyText = "Looking up the latest release…";
                break;
            case InstallStage.Downloading:
                IsIndeterminate = false;
                Progress = p.Fraction * 100;
                BusyText = $"Downloading… {p.Fraction:P0}";
                break;
            case InstallStage.Installing:
                IsIndeterminate = true;
                BusyText = IsInstalled ? "Backing up and updating…" : "Installing…";
                break;
            case InstallStage.Done:
                IsIndeterminate = false;
                Progress = 100;
                BusyText = "Done";
                break;
        }
    }

    private void Play()
    {
        try
        {
            using var process = manager.Launch(Port);
            RefreshLastPlayed();
            changed(this); // Moves this game to the front of the library.
        }
        catch (Exception e)
        {
            Error = $"Couldn't start {Port.Name}. {e.Message}";
        }
    }

    private void OpenFolder()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(manager.InstallFolder(Port)) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Error = $"Couldn't open the folder. {e.Message}";
        }
    }

    private void RaiseState() => Raise(
        nameof(IsInstalled), nameof(IsNotInstalled), nameof(UpdateAvailable), nameof(IsOffline),
        nameof(VersionText), nameof(StatusBadge), nameof(HasStatusBadge), nameof(StatusIsUpdate),
        nameof(OfflineText), nameof(HasOfflineText),
        nameof(ShowPlay), nameof(ShowUpdate), nameof(ShowInstall), nameof(ShowActions));

    /// <summary>A colour wash per game; no artwork is bundled.</summary>
    private static IBrush MakeBanner(string id)
    {
        var (from, to) = id switch
        {
            "soh" => ("#1E7A4C", "#0E3B2A"),
            "2ship" => ("#7A3FB8", "#2D1650"),
            "starship" => ("#2F6FD6", "#122A5C"),
            "spaghettikart" => ("#D2453A", "#5A1714"),
            "ghostship" => ("#E0A12E", "#7A2E12"),
            "lighthouse" => ("#C9822B", "#4A2A10"),
            "paperboat" => ("#3BA7C9", "#18405A"),
            _ => ("#4C5566", "#1E222B"),
        };
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse(from), 0),
                new GradientStop(Color.Parse(to), 1),
            },
        };
    }
}
