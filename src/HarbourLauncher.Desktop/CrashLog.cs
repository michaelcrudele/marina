using HarbourLauncher.Core;

namespace HarbourLauncher.Desktop;

/// <summary>
/// Writes unexpected errors to %LOCALAPPDATA%\Marina\crash.log, so a
/// launch that fails silently (no console when started from the .exe) still
/// leaves a record of what went wrong.
/// </summary>
internal static class CrashLog
{
    public static string FilePath => Path.Combine(LauncherPaths.DefaultRoot, "crash.log");

    public static void Write(Exception? error, string where)
    {
        if (error is null) return;
        try
        {
            Directory.CreateDirectory(LauncherPaths.DefaultRoot);
            File.AppendAllText(FilePath,
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({where}, Marina {typeof(CrashLog).Assembly.GetName().Version})" +
                Environment.NewLine + error + Environment.NewLine + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write it; don't hide the original error.
        }
    }
}
