using System.Collections.ObjectModel;
using System.Windows.Input;
using HarbourLauncher.Core;
using HarbourLauncher.Core.Saves;

namespace HarbourLauncher.Desktop.ViewModels;

/// <summary>The "save profiles" dialog: separate sets of saves for one game, e.g. a main playthrough and a randomizer.</summary>
public sealed class SaveProfilesViewModel : ObservableObject
{
    private readonly PortManager manager;
    private readonly UiServices ui;

    private GameViewModel? game;
    private bool isOpen;
    private string newName = "";
    private bool copyCurrent;
    private bool switchToNew = true;
    private string? error;

    public SaveProfilesViewModel(PortManager manager, UiServices ui)
    {
        this.manager = manager;
        this.ui = ui;
        CreateCommand = new RelayCommand(Create);
        CloseCommand = new RelayCommand(() => IsOpen = false);
    }

    public ICommand CreateCommand { get; }
    public ICommand CloseCommand { get; }

    public ObservableCollection<SaveProfileRowViewModel> Profiles { get; } = [];

    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }

    public string Title => game is null ? "" : $"Save profiles for {game.Name}";

    public string Explanation => game is null ? "" :
        $"Each profile is a separate set of {game.Name} saves, for another playthrough, a randomizer or a challenge run. " +
        "The game plays whichever profile is active. Settings, controls and mods are shared by every profile.";

    /// <summary>
    /// Shown when the active profile has no saves: either the game hasn't saved yet, or it keeps
    /// its saves somewhere Marina doesn't look, in which case profiles wouldn't separate them.
    /// </summary>
    public string? SavesHint
    {
        get
        {
            if (game is null || Profiles.FirstOrDefault(p => p.IsActive) is not { HasSaves: false }) return null;
            string where = string.Join(" or ", game.Port.Saves.Select(s => $"{s}/"));
            return $"No saves found yet in {where}. That's normal if you haven't saved in this profile. " +
                   $"If {game.Name} has saves somewhere else in its folder, tell Marina by adding them to " +
                   "\"saves\" in ports.json, or profiles won't keep them apart.";
        }
    }

    public bool HasSavesHint => SavesHint is not null;

    public string NewName
    {
        get => newName;
        set { if (Set(ref newName, value)) Raise(nameof(CanCreate)); }
    }

    public bool CanCreate => !string.IsNullOrWhiteSpace(NewName);

    /// <summary>Start the new profile from a copy of the active profile's saves, instead of empty.</summary>
    public bool CopyCurrent { get => copyCurrent; set => Set(ref copyCurrent, value); }

    public bool SwitchToNew { get => switchToNew; set => Set(ref switchToNew, value); }

    public int MaxNameLength => SaveProfiles.MaxNameLength;

    public string? Error
    {
        get => error;
        private set { if (Set(ref error, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    public void Open(GameViewModel target)
    {
        if (target.IsBusy)
        {
            ui.ShowNotice?.Invoke($"Wait for {target.Name} to finish what it's doing first.");
            return;
        }
        game = target;
        NewName = "";
        CopyCurrent = false;
        SwitchToNew = true;
        Error = null;
        Raise(nameof(Title), nameof(Explanation));
        Reload();
        IsOpen = true;
    }

    private void Reload()
    {
        Profiles.Clear();
        if (game is null) return;
        try
        {
            foreach (var info in manager.GetSaveProfiles(game.Port))
                Profiles.Add(new SaveProfileRowViewModel(info, this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = $"Couldn't read {game.Name}'s saves. {e.Message}";
        }
        game.RefreshSaveProfile();
        Raise(nameof(SavesHint), nameof(HasSavesHint));
    }

    /// <summary>Runs one change; re-reads the list if it worked, or shows the problem in the dialog.</summary>
    private bool Try(Action action)
    {
        if (game is null) return false;
        if (game.IsBusy)
        {
            Error = $"Wait for {game.Name} to finish what it's doing first.";
            return false;
        }
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or InvalidOperationException or ArgumentException)
        {
            Error = e.Message; // Nothing changed, so the list (and any half-typed name) stays as it is.
            return false;
        }
        Error = null;
        Reload();
        return true;
    }

    private void Create()
    {
        if (game is null || !CanCreate) return;
        var target = game;
        SaveProfile? created = null;
        if (!Try(() => created = manager.CreateSaveProfile(target.Port, NewName, CopyCurrent))) return;
        string name = created!.Name;
        NewName = "";
        CopyCurrent = false;
        if (SwitchToNew && Try(() => manager.SwitchSaveProfile(target.Port, created.Id)))
            ui.ShowNotice?.Invoke($"{target.Name} is now using the {name} profile.");
    }

    internal void Switch(SaveProfileRowViewModel row)
    {
        if (game is not { } target) return;
        if (Try(() => manager.SwitchSaveProfile(target.Port, row.Id)))
            ui.ShowNotice?.Invoke($"{target.Name} is now using the {row.Name} profile.");
    }

    internal bool Rename(SaveProfileRowViewModel row, string name) =>
        game is { } target && Try(() => manager.RenameSaveProfile(target.Port, row.Id, name));

    internal void Delete(SaveProfileRowViewModel row)
    {
        if (game is { } target) Try(() => manager.DeleteSaveProfile(target.Port, row.Id));
    }
}

/// <summary>One profile in the dialog, with inline rename and a two-step delete.</summary>
public sealed class SaveProfileRowViewModel : ObservableObject
{
    private readonly SaveProfilesViewModel owner;
    private readonly SaveProfileInfo info;
    private bool isRenaming;
    private bool isConfirmingDelete;
    private string editName;

    public SaveProfileRowViewModel(SaveProfileInfo info, SaveProfilesViewModel owner)
    {
        this.info = info;
        this.owner = owner;
        editName = info.Name;
        SwitchCommand = new RelayCommand(() => owner.Switch(this));
        RenameCommand = new RelayCommand(() => { EditName = Name; IsRenaming = true; });
        SaveRenameCommand = new RelayCommand(() => { if (owner.Rename(this, EditName)) IsRenaming = false; });
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        DeleteCommand = new RelayCommand(() => IsConfirmingDelete = true);
        ConfirmDeleteCommand = new RelayCommand(() => owner.Delete(this));
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
    }

    public string Id => info.Id;
    public string Name => info.Name;
    public bool IsActive => info.IsActive;
    public bool IsInactive => !info.IsActive;
    public bool HasSaves => info.LastChanged is not null;

    public string Details
    {
        get
        {
            string size = info.Bytes == 0 ? "No saves yet" : ShareViewModel.FormatSize(info.Bytes);
            string changed = info.LastChanged is { } when ? $" · last saved {when.LocalDateTime:MMM d, yyyy}" : "";
            return (IsActive ? "Active · " : "") + size + changed;
        }
    }

    public string DeleteWarning => $"Delete {Name} and its saves for good?";

    public ICommand SwitchCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand SaveRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }

    public bool IsRenaming
    {
        get => isRenaming;
        private set { if (Set(ref isRenaming, value)) Raise(nameof(ShowButtons), nameof(ShowName)); }
    }

    public bool IsConfirmingDelete
    {
        get => isConfirmingDelete;
        private set { if (Set(ref isConfirmingDelete, value)) Raise(nameof(ShowButtons)); }
    }

    public bool ShowName => !IsRenaming;
    public bool ShowButtons => !IsRenaming && !IsConfirmingDelete;
    public int MaxNameLength => owner.MaxNameLength;

    public string EditName { get => editName; set => Set(ref editName, value); }
}
