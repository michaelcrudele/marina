using Avalonia;

namespace HarbourLauncher.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception, "unhandled");
        TaskScheduler.UnobservedTaskException += (_, e) => CrashLog.Write(e.Exception, "background task");

        // Only one Marina at a time: if it's already running, bring that one forward and stop here.
        using var instance = SingleInstance.Acquire();
        if (!instance.IsFirst && instance.SendToFirst(args))
            return;
        if (instance.IsFirst)
            instance.Listen(App.OnLaunchedAgain);

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

    // Also used by the Avalonia XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
