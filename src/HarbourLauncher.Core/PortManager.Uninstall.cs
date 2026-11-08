using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.Launch;

namespace HarbourLauncher.Core;

public sealed record UninstallResult(PortDefinition Port, string Folder, long FreedBytes);

/// <summary>Removing a game from this PC.</summary>
public sealed partial class PortManager
{
    /// <summary>Total size of the game's folder: everything uninstalling would delete.</summary>
    public long GetGameFolderSize(PortDefinition port)
    {
        string folder = InstallFolder(port);
        if (!Directory.Exists(folder)) return 0;
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }

    /// <summary>
    /// Deletes the game's whole folder: the game, its saves, settings, mods, the
    /// data made from the player's ROM and the launcher's update backups.
    /// The folder is first renamed out of the way, so the game disappears from
    /// the library in one step even if deleting a large folder takes a while.
    /// The player's custom artwork is kept, so it's back if they reinstall.
    /// All the work happens off the caller's thread; progress is 0.0 to 1.0 by files deleted.
    /// </summary>
    public Task<UninstallResult> UninstallGameAsync(PortDefinition port, IProgress<double>? progress = null) =>
        Task.Run(() => UninstallGame(port, progress));

    private UninstallResult UninstallGame(PortDefinition port, IProgress<double>? progress)
    {
        string folder = InstallFolder(port);
        if (GetInstalled(port) is null && !Directory.Exists(folder))
            throw new InvalidOperationException($"{port.Name} isn't installed.");
        if (GameLauncher.IsRunning(port, folder))
            throw new GameRunningException(port, "uninstalling");

        // One pass over the folder gives both the file list and the size.
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .ToList();
        long bytes = files.Sum(f => f.Length);

        // Retry anything an earlier uninstall couldn't finish deleting.
        foreach (string leftover in Directory.EnumerateDirectories(paths.Ports, $".{port.Id}.uninstalling-*"))
        {
            try { DeleteFolder(leftover, null); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        string doomed = Path.Combine(paths.Ports, $".{port.Id}.uninstalling-{Guid.NewGuid():N}");
        try
        {
            Directory.Move(folder, doomed);
        }
        catch (IOException e)
        {
            throw new IOException(
                $"Couldn't remove {port.Name}: a file in its folder is in use. Close anything using it (the game, " +
                $"File Explorer, an editor) and try again. ({e.Message})", e);
        }

        try
        {
            DeleteFolder(doomed, progress);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"{port.Name} was removed from the library, but some of its files couldn't be deleted. " +
                $"They're in {doomed}; delete that folder yourself, or uninstall again later to retry. ({e.Message})", e);
        }
        return new UninstallResult(port, folder, bytes);
    }

    /// <summary>
    /// Deletes a folder quickly, including on network drives, where each delete
    /// is a round trip: files are deleted several at a time, and a file's
    /// attributes are only touched if it's read-only (which blocks deleting on Windows).
    /// </summary>
    private static void DeleteFolder(string folder, IProgress<double>? progress)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
        int done = 0;
        try
        {
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, file =>
            {
                try
                {
                    File.Delete(file);
                }
                catch (UnauthorizedAccessException)
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                int n = Interlocked.Increment(ref done);
                if (n % 20 == 0 || n == files.Count) progress?.Report((double)n / files.Count);
            });
        }
        catch (AggregateException e) when (e.InnerExceptions.FirstOrDefault(x => x is IOException or UnauthorizedAccessException) is { } first)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
        Directory.Delete(folder, recursive: true); // Only empty folders are left now.
        progress?.Report(1);
    }
}
