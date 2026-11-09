using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HarbourLauncher.Desktop.ViewModels;
using HarbourLauncher.Desktop.Views;

namespace HarbourLauncher.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var (manager, updater) = Services.Create();
            var library = new LibraryViewModel(manager, updater);
            library.Settings.ApplySavedAccent(); // Before the window shows, so it never flashes orange.
            library.Ui.RestartApp = () => Restart(desktop);
            desktop.MainWindow = new MainWindow { DataContext = library };
            _ = StartAsync(library, desktop.Args ?? []);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Starts the library, then opens a .marinapack if the launcher was started with one (e.g. "Open with").</summary>
    private static async Task StartAsync(LibraryViewModel library, string[] args)
    {
        if (args.Contains(Program.AfterUpdateArg))
            library.Notice = $"Marina was updated to v{AppInfo.Version}.";
        if (FindPack(args) is { } pack) library.OpenPack(pack);
        await library.StartAsync(); // Handles its own errors.
    }

    /// <summary>
    /// Starts the Marina.exe that an update just put in place, then closes this one.
    /// The new copy waits for this one to exit before it starts (see Program.Main).
    /// </summary>
    private static void Restart(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (Environment.ProcessPath is not { } exe) return;
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add(Program.AfterUpdateArg);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using (Process.Start(start)) { }
        desktop.Shutdown();
    }

    private static string? FindPack(string[] args)
    {
        string[] extensions = [".marinapack", ".marinabackup", ".hlpack", ".hlbackup"]; // .hl* from before the rename.
        return args.FirstOrDefault(a => extensions.Any(e => a.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Someone started Marina while it was already running: bring this window to
    /// the front, and open the file they started it with, if any. Called from a
    /// background thread, so the work is passed to the UI thread.
    /// </summary>
    public static void OnLaunchedAgain(string[] args) => Dispatcher.UIThread.Post(() =>
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
            return; // Still starting up; its window is about to appear anyway.
        window.BringToFront();
        if (FindPack(args) is { } pack && window.DataContext is LibraryViewModel library)
            library.OpenPack(pack);
    });
}
