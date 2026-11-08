using System.Windows.Input;
using HarbourLauncher.Core;

namespace HarbourLauncher.Desktop.ViewModels;

/// <summary>The settings panel. Changes apply when the player clicks Save.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly PortManager manager;
    private readonly Func<bool> anyGameBusy;
    private readonly Func<Task> applied;
    private readonly Action lookForGames;

    private bool isOpen;
    private bool isSaving;
    private string folder = "";
    private bool moveInstalled = true;
    private int installedCount;
    private string? error;

    public SettingsViewModel(PortManager manager, Func<bool> anyGameBusy, Func<Task> applied, Action lookForGames)
    {
        this.lookForGames = lookForGames;
        this.manager = manager;
        this.anyGameBusy = anyGameBusy;
        this.applied = applied;
        SaveCommand = new RelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => { if (!IsSaving) IsOpen = false; });
        UseDefaultCommand = new RelayCommand(() => Folder = manager.DefaultGamesFolder);
        LookForGamesCommand = new RelayCommand(() =>
        {
            if (IsSaving || IsChanged) return;
            IsOpen = false;
            lookForGames();
        });
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand UseDefaultCommand { get; }
    public ICommand LookForGamesCommand { get; }

    /// <summary>Looking only makes sense in the saved folder, not one still being typed.</summary>
    public bool CanLookForGames => !IsSaving && !IsChanged;

    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }

    public bool IsSaving
    {
        get => isSaving;
        private set { if (Set(ref isSaving, value)) Raise(nameof(CanEdit), nameof(SaveLabel), nameof(CanLookForGames)); }
    }

    public bool CanEdit => !IsSaving;
    public string SaveLabel => IsSaving ? (MoveInstalled && installedCount > 0 ? "Moving games…" : "Saving…") : "Save";

    /// <summary>The games folder being edited.</summary>
    public string Folder
    {
        get => folder;
        set { if (Set(ref folder, value)) { Error = null; Raise(nameof(ExampleText), nameof(IsChanged), nameof(ShowMoveOption), nameof(CanLookForGames)); } }
    }

    public bool MoveInstalled { get => moveInstalled; set => Set(ref moveInstalled, value); }

    public string DefaultFolder => manager.DefaultGamesFolder;

    public string ExampleText => string.IsNullOrWhiteSpace(Folder)
        ? ""
        : $"Each game gets its own folder inside, for example {Path.Combine(Folder.Trim(), "soh")}";

    public bool IsChanged => !string.Equals(Normalize(Folder), Normalize(manager.GamesFolder), StringComparison.OrdinalIgnoreCase);

    public bool ShowMoveOption => installedCount > 0 && IsChanged;

    public string MoveOptionText => installedCount == 1
        ? "Move my installed game to the new folder (saves and mods come too)"
        : $"Move my {installedCount} installed games to the new folder (saves and mods come too)";

    public string? Error
    {
        get => error;
        private set { if (Set(ref error, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    public void Open()
    {
        installedCount = manager.Catalog.Ports.Count(p => manager.GetInstalled(p) is not null);
        Folder = manager.GamesFolder;
        MoveInstalled = true;
        Error = null;
        Raise(nameof(ShowMoveOption), nameof(MoveOptionText), nameof(IsChanged), nameof(DefaultFolder));
        IsOpen = true;
    }

    private async Task SaveAsync()
    {
        if (IsSaving) return;
        if (!IsChanged)
        {
            IsOpen = false;
            return;
        }

        string chosen = Folder.Trim();
        if (chosen.Length == 0 || !Path.IsPathFullyQualified(chosen))
        {
            Error = @"Enter a full folder path, like Z:\Emulators\Harbour Masters, or use Browse.";
            return;
        }
        if (anyGameBusy())
        {
            Error = "Wait for the current install or update to finish first.";
            return;
        }

        IsSaving = true;
        try
        {
            bool toDefault = string.Equals(Normalize(chosen), Normalize(manager.DefaultGamesFolder), StringComparison.OrdinalIgnoreCase);
            await manager.ChangeGamesFolderAsync(toDefault ? null : chosen, MoveInstalled && installedCount > 0);
            IsOpen = false;
            await applied();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = e.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path.Trim(); }
    }
}
