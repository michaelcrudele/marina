using System.Collections.ObjectModel;
using System.Windows.Input;
using HarbourLauncher.Core;
using HarbourLauncher.Core.SelfUpdate;

namespace HarbourLauncher.Desktop.ViewModels;

/// <summary>The library window: every port, split into installed and available.</summary>
public sealed class LibraryViewModel : ObservableObject
{
    private readonly PortManager manager;
    private readonly List<GameViewModel> games;
    private bool isChecking;
    private string summary = "Checking for updates…";
    private string? notice;

    public LibraryViewModel(PortManager manager, SelfUpdater? updater = null)
    {
        this.manager = manager;
        games = manager.Catalog.Ports.Select(p => new GameViewModel(manager, p, Ui, OnGameChanged)).ToList();
        RefreshCommand = new RelayCommand(() => RefreshAsync(force: true)); // The button always asks GitHub.
        DismissNoticeCommand = new RelayCommand(() => Notice = null);
        Settings = new SettingsViewModel(manager, () => IsAnyBusy, OnGamesFolderChangedAsync, LookForGamesFromSettings);
        Found = new FoundGamesViewModel(manager, OnGamesAddedAsync);
        Import = new ImportViewModel(manager, Ui, id => games.Any(g => g.Port.Id == id && g.IsBusy), OnGamesAddedAsync);
        ImportCommand = new RelayCommand(PickPackAsync);
        Ui.ShowNotice = message => Notice = message;
        Share = new ShareViewModel(manager, Ui);
        Ui.OpenShare = Share.Open;
        Backup = new BackupViewModel(manager, Ui);
        Ui.OpenBackup = Backup.Open;
        Uninstall = new UninstallViewModel(manager, Ui, OnGameUninstalled);
        Ui.OpenUninstall = Uninstall.Open;
        SaveProfiles = new SaveProfilesViewModel(manager, Ui);
        Ui.OpenSaveProfiles = SaveProfiles.Open;
        Restore = new RestoreViewModel(manager, Ui, id => games.Any(g => g.Port.Id == id && g.IsBusy), OnGamesAddedAsync);
        OpenSettingsCommand = new RelayCommand(Settings.Open);
        AppUpdate = new AppUpdateViewModel(updater, Ui, () => IsAnyBusy);
        Regroup();
    }

    public UiServices Ui { get; } = new();

    /// <summary>Every game in catalog order (the artwork list in Settings).</summary>
    public IReadOnlyList<GameViewModel> AllGames => games;

    public ObservableCollection<GameViewModel> Installed { get; } = [];
    public ObservableCollection<GameViewModel> Available { get; } = [];

    public bool HasInstalled => Installed.Count > 0;
    public bool HasNoInstalled => Installed.Count == 0;
    public bool HasAvailable => Available.Count > 0;

    public ICommand RefreshCommand { get; }
    public ICommand DismissNoticeCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    public SettingsViewModel Settings { get; }
    public FoundGamesViewModel Found { get; }
    public ImportViewModel Import { get; }
    public ShareViewModel Share { get; }
    public BackupViewModel Backup { get; }
    public RestoreViewModel Restore { get; }
    public UninstallViewModel Uninstall { get; }
    public SaveProfilesViewModel SaveProfiles { get; }
    public AppUpdateViewModel AppUpdate { get; }
    public ICommand ImportCommand { get; }

    /// <summary>Opens a .marinapack (import) or .marinabackup (restore), e.g. one passed on the command line or chosen in the picker.</summary>
    public void OpenPack(string file)
    {
        if (file.EndsWith(".marinabackup", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".hlbackup", StringComparison.OrdinalIgnoreCase)) Restore.Open(file);
        else Import.Open(file);
    }

    private async Task PickPackAsync()
    {
        if (Ui.PickPackAsync is null) return;
        if (await Ui.PickPackAsync("Import a shared game or restore a backup") is { } file)
            OpenPack(file);
    }

    public bool IsChecking
    {
        get => isChecking;
        private set { if (Set(ref isChecking, value)) Raise(nameof(CanRefresh)); }
    }

    public bool CanRefresh => !IsChecking;

    public string Summary { get => summary; private set => Set(ref summary, value); }

    /// <summary>A message shown across the top of the window, or null.</summary>
    public string? Notice
    {
        get => notice;
        set { if (Set(ref notice, value)) Raise(nameof(HasNotice)); }
    }

    public bool HasNotice => Notice is not null;

    /// <summary>Updates every card's "last played" line (e.g. after playing a game and coming back).</summary>
    public void RefreshLastPlayed()
    {
        foreach (var game in Installed) game.RefreshLastPlayed();
        SortInstalled();
    }

    public bool IsAnyBusy => games.Any(g => g.IsBusy) || Import.IsWorking || Share.IsWorking
                             || Backup.IsWorking || Restore.IsWorking || Uninstall.IsWorking;

    /// <summary>On start: offer any games installed by hand, then check for updates (to the games and to Marina).</summary>
    public async Task StartAsync()
    {
        LookForExistingGames(includeDismissed: false);
        await Task.WhenAll(RefreshAsync(), AppUpdate.CheckOnStartupAsync());
    }

    /// <summary>Shows the "found games" prompt if there is anything to add. Returns how many were found.</summary>
    public int LookForExistingGames(bool includeDismissed)
    {
        IReadOnlyList<FoundGame> found;
        try
        {
            found = manager.FindExistingGames(includeDismissed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        Found.Show(found);
        return found.Count;
    }

    private void LookForGamesFromSettings()
    {
        if (LookForExistingGames(includeDismissed: true) == 0)
            Notice = $"No other games were found in {manager.GamesFolder}. Each game needs its own folder there, with its .exe inside.";
    }

    /// <summary>
    /// After an uninstall only that game changed, and nothing about the latest
    /// releases did, so there's no need to re-read every game or ask GitHub again.
    /// </summary>
    private void OnGameUninstalled(GameViewModel game)
    {
        game.ReloadInstalled();
        Regroup();
        UpdateSummary();
    }

    private async Task OnGamesAddedAsync()
    {
        foreach (var game in games)
            game.ReloadInstalled();
        Regroup();
        await RefreshAsync();
    }

    /// <param name="force">
    /// Ask GitHub about every game now. Otherwise releases confirmed in the last
    /// hour are reused, which keeps Marina well inside GitHub's limit of 60 checks an hour.
    /// </param>
    public async Task RefreshAsync(bool force = false)
    {
        if (IsChecking) return;
        IsChecking = true;
        Summary = "Checking for updates…";
        try
        {
            await Task.WhenAll(games.Select(g => g.CheckAsync(force)));
        }
        finally
        {
            IsChecking = false;
            UpdateSummary();
        }
    }

    /// <summary>After the games folder changes, re-read what is installed there.</summary>
    private async Task OnGamesFolderChangedAsync()
    {
        foreach (var game in games)
            game.ReloadInstalled();
        Regroup();
        LookForExistingGames(includeDismissed: false);
        await RefreshAsync();
    }

    private void OnGameChanged(GameViewModel game)
    {
        bool inRightGroup = game.IsInstalled ? Installed.Contains(game) : Available.Contains(game);
        if (!inRightGroup) Regroup();
        else SortInstalled();
        UpdateSummary();
    }

    /// <summary>
    /// Installed games, most recently played first; games never played come
    /// after them, in catalog order (as do ties).
    /// </summary>
    private List<GameViewModel> InstalledInOrder() => games
        .Where(g => g.IsInstalled)
        .OrderByDescending(g => g.LastPlayed ?? DateTimeOffset.MinValue)
        .ThenBy(g => games.IndexOf(g))
        .ToList();

    /// <summary>Rebuilds both groups: installed by last played, available in catalog order.</summary>
    private void Regroup()
    {
        Installed.Clear();
        Available.Clear();
        foreach (var game in InstalledInOrder()) Installed.Add(game);
        foreach (var game in games.Where(g => !g.IsInstalled)) Available.Add(game);
        Raise(nameof(HasInstalled), nameof(HasNoInstalled), nameof(HasAvailable));
    }

    /// <summary>
    /// Re-sorts the installed games in place. Cards are moved, not rebuilt, so
    /// nothing flickers and a card keeps its state (progress, errors) when it moves.
    /// </summary>
    private void SortInstalled()
    {
        var order = InstalledInOrder();
        if (order.Count != Installed.Count) { Regroup(); return; }
        for (int i = 0; i < order.Count; i++)
        {
            int at = Installed.IndexOf(order[i]);
            if (at != i) Installed.Move(at, i);
        }
    }

    private void UpdateSummary()
    {
        if (IsChecking) return;
        int installed = games.Count(g => g.IsInstalled);
        int updates = games.Count(g => g.UpdateAvailable);
        bool failed = games.Any(g => g.HasError);
        bool offline = games.Any(g => g.IsOffline);
        string? reason = games.Select(g => g.StaleReason).FirstOrDefault(r => r is not null);
        var checkedAt = games.Select(g => g.CheckedAt).Where(t => t is not null).Min();

        string text = installed switch
        {
            0 => "No games installed yet",
            1 => "1 game installed",
            _ => $"{installed} games installed",
        };
        if (installed > 0)
            text += updates switch
            {
                0 => " · everything is up to date",
                1 => " · 1 update available",
                _ => $" · {updates} updates available",
            };
        if (reason is not null)
            text += $" · {reason}";
        else if (failed || offline)
            text += " · some games couldn't be checked";
        else if (checkedAt is { } t)
            text += $" · checked at {t.ToLocalTime():t}";
        Summary = text;
    }
}
