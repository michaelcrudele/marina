using System.Collections.ObjectModel;
using System.Windows.Input;
using HarbourLauncher.Core;
using HarbourLauncher.Core.Sharing;

namespace HarbourLauncher.Desktop.ViewModels;

/// <summary>One thing that can go in a pack, with a checkbox and its size.</summary>
public sealed class ShareItemViewModel(ShareItem item, Action changed, Func<bool> canEdit) : ObservableObject
{
    private bool isSelected = true;

    /// <summary>Choices are locked while the pack is being written.</summary>
    public bool CanEdit => canEdit();
    internal void RefreshCanEdit() => Raise(nameof(CanEdit));

    public ShareItem Item { get; } = item;
    public string Label => Item.Label;
    public string Detail => Item.Detail;
    public string SizeText => ShareViewModel.FormatSize(Item.Bytes);
    public bool IsMod => Item.Kind == ShareItemKind.Mod;

    public bool IsSelected
    {
        get => isSelected;
        set { if (Set(ref isSelected, value)) changed(); }
    }
}

/// <summary>The "share game" dialog: pick what goes in the .marinapack, see sizes, then save it.</summary>
public sealed class ShareViewModel : ObservableObject
{
    private readonly PortManager manager;
    private readonly UiServices ui;

    private GameViewModel? game;
    private ShareContents? contents;
    private CancellationTokenSource? cancel;
    private bool isOpen;
    private bool isWorking;
    private double progress;
    private string? error;

    public ShareViewModel(PortManager manager, UiServices ui)
    {
        this.manager = manager;
        this.ui = ui;
        SaveCommand = new RelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(Cancel);
    }

    public ObservableCollection<ShareItemViewModel> Items { get; } = [];
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }

    public bool IsWorking
    {
        get => isWorking;
        private set
        {
            if (!Set(ref isWorking, value)) return;
            Raise(nameof(CanEdit), nameof(CanSave), nameof(CancelLabel));
            foreach (var item in Items) item.RefreshCanEdit();
        }
    }

    public bool CanEdit => !IsWorking;
    public string CancelLabel => IsWorking ? "Stop" : "Cancel";
    public double Progress { get => progress; private set { if (Set(ref progress, value)) Raise(nameof(ProgressText)); } }
    public string ProgressText => $"Creating the pack… {Progress / 100:P0}";

    public string? Error
    {
        get => error;
        private set { if (Set(ref error, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    public string Title => game is null ? "" : $"Share {game.Name}";

    public long SelectedBytes => Items.Where(i => i.IsSelected).Sum(i => i.Item.Bytes);
    public string TotalText => Items.Any(i => i.IsSelected)
        ? $"Pack size: about {FormatSize(SelectedBytes)}"
        : "Choose at least one thing to include.";
    public bool CanSave => !IsWorking && Items.Any(i => i.IsSelected);

    /// <summary>A note that depends on the choices, e.g. what happens without the game.</summary>
    public string? ChoiceNote
    {
        get
        {
            if (contents?.NoGameReason is { } why) return why;
            bool gameChosen = Items.Any(i => i.Item.Kind == ShareItemKind.Game && i.IsSelected);
            return gameChosen ? null : $"Without the game, whoever imports the pack downloads the latest {contents?.Port.Name} from GitHub.";
        }
    }

    public bool HasChoiceNote => ChoiceNote is not null;

    /// <summary>Lists what's never packed, e.g. "pm64.o2r (40 MB, made from your ROM)".</summary>
    public string NeverIncludedText
    {
        get
        {
            if (contents is null) return "";
            var notable = contents.NeverIncluded
                .Where(x => x.Reason is "made from your ROM" or "ROM" && x.Bytes > 1024)
                .Select(x => $"{Path.GetFileName(x.File)} ({FormatSize(x.Bytes)}, {x.Reason})")
                .ToList();
            string rom = notable.Count > 0 ? string.Join(", ", notable) + "; " : "";
            return $"Never included: {rom}logs, debug files and launcher backups. " +
                   "Whoever imports the pack uses their own ROM.";
        }
    }

    public void Open(GameViewModel target)
    {
        if (IsWorking) return;
        if (target.IsBusy)
        {
            ui.ShowNotice?.Invoke($"Wait for {target.Name} to finish installing or updating first.");
            return;
        }
        try
        {
            contents = manager.GetShareContents(target.Port);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ui.ShowNotice?.Invoke($"Couldn't read {target.Name}'s folder. {e.Message}");
            return;
        }

        game = target;
        Items.Clear();
        foreach (var item in contents.Items)
            Items.Add(new ShareItemViewModel(item, OnSelectionChanged, () => CanEdit));
        Error = null;
        Progress = 0;
        Raise(nameof(Title), nameof(NeverIncludedText));
        OnSelectionChanged();
        IsOpen = true;
    }

    private void OnSelectionChanged() =>
        Raise(nameof(SelectedBytes), nameof(TotalText), nameof(CanSave), nameof(ChoiceNote), nameof(HasChoiceNote));

    private void Cancel()
    {
        if (IsWorking) cancel?.Cancel();
        else IsOpen = false;
    }

    private async Task SaveAsync()
    {
        if (game is null || !CanSave || ui.SavePackAsync is null) return;
        var target = game;
        string? file = await ui.SavePackAsync(Title, $"{target.Name}{ShareRules.Extension}");
        if (file is null) return;

        var ids = Items.Where(i => i.IsSelected).Select(i => i.Item.Id).ToList();
        Error = null;
        Progress = 0;
        cancel = new CancellationTokenSource();
        IsWorking = true;
        target.BeginExternalWork("Creating a pack to share…");
        try
        {
            var token = cancel.Token;
            var report = new Progress<double>(f => Progress = f * 100);
            var result = await Task.Run(() => manager.ExportGame(target.Port, file, ids, report, token), token);
            IsOpen = false;
            ui.ShowNotice?.Invoke($"Saved {Path.GetFileName(result.File)} ({FormatSize(result.Bytes)}).");
        }
        catch (OperationCanceledException)
        {
            Error = "Stopped. No pack was saved.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = $"Couldn't save the pack. {e.Message}";
        }
        finally
        {
            target.EndExternalWork();
            IsWorking = false;
            cancel.Dispose();
            cancel = null;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
        _ => $"{Math.Max(1, (bytes + 1023) / 1024)} KB",
    };
}
