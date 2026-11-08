using System.Diagnostics;
using Avalonia;
using HarbourLauncher.Core.SelfUpdate;

namespace HarbourLauncher.Desktop;

internal static class Program
{
    /// <summary>Passed (with the old copy's process id) to the Marina that an update just installed.</summary>
    public const string AfterUpdateArg = "--after-update";

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception, "unhandled");
        TaskScheduler.UnobservedTaskException += (_, e) => CrashLog.Write(e.Exception, "background task");

        // Just updated: let the old copy finish closing first, or this one would hand over to it and quit.
        WaitForOldCopy(args);

        // Only one Marina at a time: if it's already running, bring that one forward and stop here.
        using var instance = SingleInstance.Acquire();
        if (!instance.IsFirst && instance.SendToFirst(args))
            return;
        if (instance.IsFirst)
            instance.Listen(App.OnLaunchedAgain);

        // Delete the old exe (and any half-finished download) an earlier update left behind.
        if (instance.IsFirst && Environment.ProcessPath is { } exe)
            SelfUpdater.CleanUp(exe);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            CrashLog.Write(e, "startup");
            throw;
        }
    }

    private static void WaitForOldCopy(string[] args)
    {
        int at = Array.IndexOf(args, AfterUpdateArg);
        if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], out int pid)) return;
        try
        {
            using var old = Process.GetProcessById(pid);
            old.WaitForExit(TimeSpan.FromSeconds(20));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // Already gone.
        }
    }

    // Also used by the Avalonia XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
