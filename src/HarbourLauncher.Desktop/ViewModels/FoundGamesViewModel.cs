using System.Collections.ObjectModel;
using System.Windows.Input;
using HarbourLauncher.Core;

namespace HarbourLauncher.Desktop.ViewModels;

/// <summary>One game found in the games folder that the launcher didn't install.</summary>
public sealed class FoundGameItem(FoundGame game) : ObservableObject
{
    private bool isSelected = true;

    public FoundGame Game { get; } = game;
    public string Name => Game.Port.Name;
    public string Detail => $"{Game.Port.Game} · {Game.Folder}";
    public bool NeedsRename => Game.NeedsRename;
    public string RenameNote => $"Its folder will be renamed to “{Path.GetFileName(Game.TargetFolder)}”.";
    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
}

/// <summary>The "found games" prompt: add games installed by hand to the library.</summary>
public sealed class FoundGamesViewModel : ObservableObject
{
    private readonly PortManager manager;
    private readonly Func<Task> added;
    private bool isOpen;
    private string? error;

    public FoundGamesViewModel(PortManager manager, Func<Task> added)
    {
        this.manager = manager;
        this.added = added;
        AddCommand = new RelayCommand(AddAsync);
        NotNowCommand = new RelayCommand(NotNow);
    }

    public ObservableCollection<FoundGameItem> Items { get; } = [];

    public ICommand AddCommand { get; }
    public ICommand NotNowCommand { get; }

    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }

    public string Title => Items.Count == 1
        ? "Found a game in your games folder"
        : $"Found {Items.Count} games in your games folder";

    public string Intro => (Items.Count == 1 ? "This game wasn't" : "These games weren't") +
        " installed by Marina. Add them to your library to play and update them from here. " +
        "Your saves, mods and settings stay exactly where they are.";

    public string? Error
    {
        get => error;
        private set { if (Set(ref error, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    public void Show(IReadOnlyList<FoundGame> games)
    {
        Items.Clear();
        foreach (var game in games) Items.Add(new FoundGameItem(game));
        Error = null;
        Raise(nameof(Title), nameof(Intro));
        IsOpen = Items.Count > 0;
    }

    private async Task AddAsync()
    {
        var problems = new List<string>();
        var dismissed = new List<FoundGame>();
        foreach (var item in Items.ToList())
        {
            if (!item.IsSelected)
            {
                dismissed.Add(item.Game);
                Items.Remove(item);
                continue;
            }
            try
            {
                manager.AddExistingGame(item.Game);
                Items.Remove(item);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                problems.Add($"{item.Name}: {e.Message}");
            }
        }

        TryDismiss(dismissed);
        Raise(nameof(Title), nameof(Intro));
        if (problems.Count > 0)
            Error = "Some games couldn't be added. " + string.Join(" ", problems);
        else
            IsOpen = false;
        await added();
    }

    private void NotNow()
    {
        TryDismiss(Items.Select(i => i.Game).ToList());
        IsOpen = false;
    }

    private void TryDismiss(IReadOnlyList<FoundGame> games)
    {
        if (games.Count == 0) return;
        try { manager.DismissFoundGames(games); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Just asks again next time. */ }
    }
}
