using System.Diagnostics;
using System.Windows.Input;
using HarbourLauncher.Core.SelfUpdate;

namespace HarbourLauncher.Desktop.ViewModels;

/// <summary>
/// Keeps Marina itself up to date: checks GitHub on start and from Settings, and
/// when there's a newer version, asks before downloading it and restarting.
/// </summary>
public sealed class AppUpdateViewModel : ObservableObject
{
    private readonly SelfUpdater? updater;
    private readonly UiServices ui;
    private readonly Func<bool> anyGameBusy;

    private AppUpdate? update;
    private CancellationTokenSource? cancel;
    private bool isOpen;
    private bool isChecking;
    private bool isWorking;
    private double progress;
    private string? error;
    private string? checkResult;

    public AppUpdateViewModel(SelfUpdater? updater, UiServices ui, Func<bool> anyGameBusy)
    {
        this.updater = updater;
        this.ui = ui;
        this.anyGameBusy = anyGameBusy;
        CheckCommand = new RelayCommand(() => CheckAsync(fromSettings: true));
        UpdateCommand = new RelayCommand(UpdateAsync);
        LaterCommand = new RelayCommand(() => { if (IsWorking) cancel?.Cancel(); else IsOpen = false; });
        WhatsNewCommand = new RelayCommand(() => { if (update is not null) OpenInBrowser(update.Release.HtmlUrl); });
    }

    public ICommand CheckCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand LaterCommand { get; }
    public ICommand WhatsNewCommand { get; }

    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }

    public bool IsChecking
    {
        get => isChecking;
        private set { if (Set(ref isChecking, value)) Raise(nameof(CanCheck), nameof(CheckLabel)); }
    }

    public bool CanCheck => updater is not null && !IsChecking && !IsWorking;
    public string CheckLabel => IsChecking ? "Checking…" : "Check for updates";

    public bool IsWorking
    {
        get => isWorking;
        private set { if (Set(ref isWorking, value)) Raise(nameof(CanAct), nameof(LaterLabel), nameof(CanCheck)); }
    }

    public bool CanAct => !IsWorking;
    public string LaterLabel => IsWorking ? "Stop" : "Not now";
    public double Progress { get => progress; private set => Set(ref progress, value); }

    public string? Error
    {
        get => error;
        private set { if (Set(ref error, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    /// <summary>What the last check from Settings found, shown under its button.</summary>
    public string? CheckResult
    {
        get => checkResult;
        private set { if (Set(ref checkResult, value)) Raise(nameof(HasCheckResult)); }
    }

    public bool HasCheckResult => CheckResult is not null;

    public string CurrentVersionText => $"You have Marina v{AppInfo.Version}.";

    public string Title => update is null ? "" : $"Marina v{update.Version} is available";

    /// <summary>The single-file exe can replace itself; a build run from its bin folder can only point to the download.</summary>
    public bool CanInstall => AppInfo.IsSingleFile;

    public string Detail => CanInstall
        ? $"You have v{AppInfo.Version}. Marina will download the update, then restart. Your games, saves and settings aren't touched."
        : $"You have v{AppInfo.Version}. This copy of Marina can't update itself, so download the new version from GitHub.";

    public string UpdateLabel => IsWorking ? "Downloading…" : CanInstall ? "Update and restart" : "Open download page";

    /// <summary>On start: quietly look for a newer Marina, and ask only if there is one.</summary>
    public async Task CheckOnStartupAsync()
    {
        // A build made on a developer's PC (e.g. 0.1.42-dev) shouldn't nag about releases on every run.
        if (updater is null || updater.Current.IsPreRelease) return;
        await CheckAsync(fromSettings: false);
    }

    private async Task CheckAsync(bool fromSettings)
    {
        if (updater is null || IsChecking || IsWorking) return;
        IsChecking = true;
        if (fromSettings) CheckResult = null;
        try
        {
            // The button always asks GitHub; the start-up check reuses a release confirmed in the last hour.
            var found = await updater.CheckAsync(force: fromSettings);
            if (found is null)
            {
                if (fromSettings) CheckResult = $"You have the latest version (v{AppInfo.Version}).";
                return;
            }
            Show(found);
        }
        catch (Exception e)
        {
            // At start-up a failed check stays quiet; the games' own update check reports being offline.
            if (fromSettings) CheckResult = $"Couldn't check for updates: {e.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void Show(AppUpdate found)
    {
        update = found;
        Error = null;
        Progress = 0;
        Raise(nameof(Title), nameof(Detail), nameof(UpdateLabel), nameof(CanInstall));
        IsOpen = true;
    }

    private async Task UpdateAsync()
    {
        if (updater is null || update is null || IsWorking) return;
        if (!CanInstall)
        {
            OpenInBrowser(update.Release.HtmlUrl);
            IsOpen = false;
            return;
        }
        if (anyGameBusy())
        {
            Error = "Wait for the current install or update to finish first.";
            return;
        }

        Error = null;
        Progress = 0;
        cancel = new CancellationTokenSource();
        IsWorking = true;
        Raise(nameof(UpdateLabel));
        try
        {
            string downloaded = await updater.DownloadAsync(update, new Progress<double>(f => Progress = f * 100), cancel.Token);
            if (anyGameBusy())
            {
                // Something started while downloading; restarting now would cut it off.
                SelfUpdater.CleanUp(updater.ExePath);
                Error = "A game started installing or updating. Click Update again once it's done.";
                return;
            }
            updater.Install(downloaded);
            IsWorking = false; // Lets the window close for the restart.
            ui.RestartApp?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Stopped; the half-finished download was deleted.
        }
        catch (UnauthorizedAccessException)
        {
            Error = $"Marina doesn't have permission to replace itself in {Path.GetDirectoryName(updater.ExePath)}. " +
                    "Use \"What's new\" to download it from GitHub instead.";
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            IsWorking = false;
            Raise(nameof(UpdateLabel));
            cancel.Dispose();
            cancel = null;
        }
    }

    private static void OpenInBrowser(string url)
    {
        try { using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
