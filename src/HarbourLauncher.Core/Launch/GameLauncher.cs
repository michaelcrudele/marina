using System.Diagnostics;
using HarbourLauncher.Core.Catalog;

namespace HarbourLauncher.Core.Launch;

public static class GameLauncher
{
    public static string ExePath(PortDefinition port, string installDir) =>
        Path.GetFullPath(Path.Combine(installDir, port.Exe));

    /// <summary>
    /// True if this port's exe from this folder is running. Updating a running
    /// game would fail on locked files, so callers check this first. When a
    /// process's path can't be read, it is assumed to be ours (the safe answer).
    /// </summary>
    public static bool IsRunning(PortDefinition port, string installDir)
    {
        string exe = ExePath(port, installDir);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(port.Exe)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Starts the port from its own folder. The ports look for their assets,
    /// config and saves relative to the working directory, so it must be set.
    /// </summary>
    public static Process Launch(PortDefinition port, string installDir)
    {
        string exe = ExePath(port, installDir);
        if (!File.Exists(exe))
            throw new FileNotFoundException($"{port.Name} is not installed.", exe);

        return Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Could not start {port.Name}.");
    }
}
