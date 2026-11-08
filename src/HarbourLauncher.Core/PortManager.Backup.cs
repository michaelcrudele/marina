using System.IO.Compression;
using HarbourLauncher.Core.Backup;
using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.Install;
using HarbourLauncher.Core.Launch;
using HarbourLauncher.Core.Sharing;

namespace HarbourLauncher.Core;

/// <summary>
/// Whole-game backups (.marinabackup) for moving games between the player's own
/// PCs or keeping a copy on a USB drive. Unlike a shared .marinapack, a backup
/// holds everything, including the data made from the player's ROM.
/// </summary>
public sealed partial class PortManager
{
    /// <summary>What a backup of this game would hold, by part, with sizes.</summary>
    public BackupContents GetBackupContents(PortDefinition port)
    {
        var state = GetInstalled(port) ?? throw new InvalidOperationException($"{port.Name} isn't installed.");
        string folder = InstallFolder(port);
        var release = new HashSet<string>(state.Files.Select(Glob.NormalizePath), StringComparer.OrdinalIgnoreCase);

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Glob.NormalizePath(Path.GetRelativePath(folder, f)))
            .Where(GameBackup.IsBackedUp)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sizes = files.ToDictionary(f => f, f => new FileInfo(Path.Combine(folder, f)).Length, StringComparer.OrdinalIgnoreCase);

        string PartOf(string rel)
        {
            if (release.Contains(rel) || rel.StartsWith(".marina/", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith(".harbourlauncher/", StringComparison.OrdinalIgnoreCase)) return "The game";
            if (ShareRules.NeverSharedReason(rel, release) is "made from your ROM" or "ROM") return "Game data made from your ROM";
            if (rel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) return "Mods";
            return "Saves and settings";
        }

        string[] order = ["The game", "Game data made from your ROM", "Saves and settings", "Mods"];
        var parts = files.GroupBy(PartOf)
            .OrderBy(g => Array.IndexOf(order, g.Key))
            .Select(g => new BackupPart(g.Key, g.Sum(f => sizes[f]), g.Count()))
            .ToList();
        if (GetArtwork(port) is { } art)
            parts.Add(new BackupPart("Artwork", new FileInfo(art).Length, 1));

        string? tag = state.IsVersionKnown ? state.Tag : null;
        return new BackupContents(port, tag, parts, files, parts.Sum(p => p.Bytes));
    }

    /// <summary>Backs up a whole game to a .marinabackup file, e.g. on a USB drive.</summary>
    public BackupResult BackUpGame(PortDefinition port, string destination,
                                   IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!destination.EndsWith(GameBackup.Extension, StringComparison.OrdinalIgnoreCase))
            destination += GameBackup.Extension;
        string folder = InstallFolder(port);
        if (GameLauncher.IsRunning(port, folder))
            throw new GameRunningException(port, "backing up");

        var contents = GetBackupContents(port);
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        GameBackup.CheckDestination(destination, contents.Bytes, singleFile: true);

        string? artwork = GetArtwork(port);
        var manifest = new BackupManifest(
            BackupManifest.CurrentFormat, port.Id, port.Name, contents.Tag, DateTimeOffset.UtcNow,
            Environment.MachineName,
            artwork is null ? null : "artwork" + Path.GetExtension(artwork).ToLowerInvariant(),
            contents.Bytes, contents.Files);

        var sources = contents.Files.Select(rel => (Path.Combine(folder, rel), GameBackup.FilesPrefix + rel)).ToList();
        if (artwork is not null) sources.Add((artwork, manifest.Artwork!));

        ZipWriter.Write(destination, GameBackup.ManifestEntry, manifest, sources, progress, ct);
        return new BackupResult(destination, contents.Files.Count, new FileInfo(destination).Length);
    }

    /// <summary>Opens and checks a backup without changing anything.</summary>
    public BackupInfo ReadBackup(string file) => GameBackup.Read(file, Catalog);

    /// <summary>
    /// Restores a backup into this PC's games folder. If the game is already
    /// here, its folder is kept, renamed to "&lt;id&gt; (before restore …)", not
    /// deleted. The backup is unpacked next to it first, so a failed or stopped
    /// restore leaves the existing game exactly as it was.
    /// </summary>
    public async Task<RestoreResult> RestoreBackupAsync(string file, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var info = ReadBackup(file);
        var port = info.Port;
        string folder = InstallFolder(port);
        if (GameLauncher.IsRunning(port, folder))
            throw new GameRunningException(port, "restoring");

        Directory.CreateDirectory(paths.Ports);
        CheckWritable(paths.Ports);
        GameBackup.CheckDestination(Path.Combine(paths.Ports, "x"), info.Manifest.Bytes, singleFile: false);

        string staging = Path.Combine(paths.Ports, $".{port.Id}.restoring");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

        int count;
        try
        {
            count = await Task.Run(() => Unpack(info, staging, progress, ct), ct).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteFolder(staging);
            throw;
        }

        string? previous = null;
        try
        {
            if (Directory.Exists(folder))
            {
                if (Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    previous = UniqueFolder(Path.Combine(paths.Ports, $"{port.Id} (before restore {DateTime.Now:yyyy-MM-dd HHmm})"));
                    Directory.Move(folder, previous);
                }
                else
                {
                    Directory.Delete(folder);
                }
            }
            Directory.Move(staging, folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (previous is not null && !Directory.Exists(folder))
            {
                try { Directory.Move(previous, folder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            TryDeleteFolder(staging);
            throw new BackupException($"Couldn't put the restored {port.Name} in place: {e.Message} Nothing was changed.", e);
        }

        if (info.Manifest.Artwork is not null)
            TryRestoreArtwork(info);

        return new RestoreResult(port, count, folder, previous);
    }

    private static int Unpack(BackupInfo info, string staging, IProgress<double>? progress, CancellationToken ct)
    {
        string root = Path.GetFullPath(staging);
        Directory.CreateDirectory(root);
        string? zone = MarkOfTheWeb.Read(info.File);

        using var zip = ZipFile.OpenRead(info.File);
        var entries = zip.Entries
            .Where(e => e.FullName.StartsWith(GameBackup.FilesPrefix, StringComparison.Ordinal) && !e.FullName.EndsWith('/'))
            .ToList();
        foreach (var entry in entries)
            PortInstaller.EnsureInside(root, entry.FullName[GameBackup.FilesPrefix.Length..]);

        long total = Math.Max(1, entries.Sum(e => e.Length));
        long done = 0;
        var buffer = new byte[1024 * 1024];
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            string rel = Glob.NormalizePath(entry.FullName[GameBackup.FilesPrefix.Length..]);
            string target = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = entry.Open())
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    done += read;
                    progress?.Report((double)done / total);
                }
            }
            File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime);
            if (zone is not null && ShareRules.IsProgram(rel)) MarkOfTheWeb.Write(target, zone);
        }
        return entries.Count;
    }

    private void TryRestoreArtwork(BackupInfo info)
    {
        string temp = Path.Combine(Path.GetTempPath(), "hl-art-" + Guid.NewGuid().ToString("N") + Path.GetExtension(info.Manifest.Artwork));
        try
        {
            using (var zip = ZipFile.OpenRead(info.File))
                zip.GetEntry(info.Manifest.Artwork!)?.ExtractToFile(temp);
            if (File.Exists(temp)) SetArtwork(info.Port, temp);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // The game itself was restored; artwork is a nice-to-have.
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static string UniqueFolder(string path)
    {
        string candidate = path;
        for (int i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
            candidate = $"{path} ({i})";
        return candidate;
    }

    private static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
