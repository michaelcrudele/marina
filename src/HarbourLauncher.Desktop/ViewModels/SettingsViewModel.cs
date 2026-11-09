using System.Windows.Input;
using Avalonia.Media;
using HarbourLauncher.Core;

namespace HarbourLauncher.Desktop.ViewModels;

/// <summary>
/// The settings panel. Changes apply when the player clicks Save; the accent
/// colour is previewed live while picking and put back on Cancel.
/// </summary>
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
    private Color accent = AccentTheme.Default;
    private Color savedAccent = AccentTheme.Default;

    public SettingsViewModel(PortManager manager, Func<bool> anyGameBusy, Func<Task> applied, Action lookForGames)
    {
        this.lookForGames = lookForGames;
        this.manager = manager;
        this.anyGameBusy = anyGameBusy;
        this.applied = applied;
        SaveCommand = new RelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() =>
        {
            if (IsSaving) return;
            Accent = savedAccent; // Undo the preview.
            IsOpen = false;
        });
        UseDefaultCommand = new RelayCommand(() => Folder = manager.DefaultGamesFolder);
        LookForGamesCommand = new RelayCommand(() =>
        {
            if (IsSaving || IsChanged) return;
            SaveAccent(); // The panel closes, so keep the colour the player can already see.
            IsOpen = false;
            lookForGames();
        });
        ResetAccentCommand = new RelayCommand(() => Accent = AccentTheme.Default);
        Swatches =
        [
            new("Marina orange", AccentTheme.Default, this),
            new("Gold", Color.FromRgb(0xE8, 0xB9, 0x31), this),
            new("Green", Color.FromRgb(0x52, 0xB8, 0x5A), this),
            new("Teal", Color.FromRgb(0x2B, 0xB5, 0xA5), this),
            new("Blue", Color.FromRgb(0x4C, 0x8D, 0xF6), this),
            new("Purple", Color.FromRgb(0x9B, 0x7B, 0xEA), this),
            new("Pink", Color.FromRgb(0xE4, 0x6A, 0xA6), this),
            new("Silver", Color.FromRgb(0xA9, 0xB4, 0xC2), this),
        ];
    }

    /// <summary>Applies the player's saved accent colour. Called once at startup.</summary>
    public void ApplySavedAccent()
    {
        savedAccent = AccentTheme.Parse(manager.AccentColor);
        accent = savedAccent;
        AccentTheme.Apply(accent);
        RaiseAccent();
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand UseDefaultCommand { get; }
    public ICommand LookForGamesCommand { get; }
    public ICommand ResetAccentCommand { get; }

    /// <summary>Ready-made colours that read well on the dark background.</summary>
    public IReadOnlyList<AccentSwatch> Swatches { get; }

    /// <summary>The accent colour being picked. Shown across the app straight away as a preview.</summary>
    public Color Accent
    {
        get => accent;
        set
        {
            value = AccentTheme.WithoutAlpha(value);
            if (!Set(ref accent, value)) return;
            AccentTheme.Apply(value);
            RaiseAccent();
        }
    }

    public string AccentHex => AccentTheme.ToHex(Accent);

    public bool IsDefaultAccent => Accent == AccentTheme.Default;

    public bool AccentHardToSee => AccentTheme.IsHardToSee(Accent);

    private bool AccentChanged => Accent != savedAccent;

    private void RaiseAccent()
    {
        Raise(nameof(Accent), nameof(AccentHex), nameof(IsDefaultAccent), nameof(AccentHardToSee));
        foreach (var swatch in Swatches) swatch.Refresh();
    }

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
        Accent = savedAccent;
        Raise(nameof(ShowMoveOption), nameof(MoveOptionText), nameof(IsChanged), nameof(DefaultFolder));
        IsOpen = true;
    }

    private async Task SaveAsync()
    {
        if (IsSaving) return;
        if (!SaveAccent()) return;
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

    /// <summary>Saves the accent colour if it changed. False (with an error shown) if it couldn't be saved.</summary>
    private bool SaveAccent()
    {
        if (!AccentChanged) return true;
        try
        {
            manager.SetAccentColor(IsDefaultAccent ? null : AccentHex);
            savedAccent = Accent;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = $"Couldn't save the colour: {e.Message}";
            return false;
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path.Trim(); }
    }
}

/// <summary>One of the ready-made accent colours in Settings.</summary>
public sealed class AccentSwatch : ObservableObject
{
    private readonly SettingsViewModel settings;

    public AccentSwatch(string name, Color color, SettingsViewModel settings)
    {
        this.settings = settings;
        Name = name;
        Color = color;
        Brush = new SolidColorBrush(color);
        PickCommand = new RelayCommand(() => settings.Accent = color);
    }

    public string Name { get; }
    public Color Color { get; }
    public IBrush Brush { get; }
    public ICommand PickCommand { get; }

    public bool IsSelected => settings.Accent == Color;

    internal void Refresh() => Raise(nameof(IsSelected));
}
