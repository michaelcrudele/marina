using System.IO.Compression;
using System.Text.Json;
using HarbourLauncher.Core.Catalog;

namespace HarbourLauncher.Core.Backup;

/// <summary>
/// What a .marinabackup says about itself (backup.json at the root of the zip).
/// A backup is a complete copy of one game folder, for the player's own PCs:
/// the game, the data made from their ROM, saves, settings, mods, artwork and
/// the launcher's record of the install, under files/ in the zip.
/// </summary>
public sealed record BackupManifest(
    int Format,
    string PortId,
    string PortName,
    string? Tag,
    DateTimeOffset CreatedAt,
    string Machine,
    string? Artwork,
    long Bytes,
    IReadOnlyList<string> Files)
{
    public const int CurrentFormat = 1;
}

/// <summary>One part of a backup, for showing the player what it holds.</summary>
public sealed record BackupPart(string Label, long Bytes, int FileCount);

/// <summary>What backing up a game would save, before doing it.</summary>
public sealed record BackupContents(PortDefinition Port, string? Tag, IReadOnlyList<BackupPart> Parts, IReadOnlyList<string> Files, long Bytes);

/// <summary>A backup file that was opened and checked.</summary>
public sealed record BackupInfo(string File, BackupManifest Manifest, PortDefinition Port);

public sealed record BackupResult(string File, int FileCount, long Bytes);

/// <param name="PreviousFolder">Where the game that was already on this PC was moved to, or null if there wasn't one.</param>
public sealed record RestoreResult(PortDefinition Port, int FileCount, string Folder, string? PreviousFolder);

/// <summary>A backup can't be made or restored. Nothing was changed.</summary>
public sealed class BackupException(string message, Exception? inner = null) : Exception(message, inner);

internal static class GameBackup
{
    public const string Extension = ".marinabackup";

    /// <summary>The extension backups had before the launcher was renamed Marina; still opened.</summary>
    public const string LegacyExtension = ".hlbackup";
    public const string ManifestEntry = "backup.json";
    public const string FilesPrefix = "files/";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Launcher scratch space and files nobody needs back.</summary>
    private static readonly string[] SkippedFolders =
        [".marina/backup/", ".marina/staging/", ".marina/import-backup/",
         ".harbourlauncher/backup/", ".harbourlauncher/staging/", ".harbourlauncher/import-backup/", "logs/", "debug/"];

    public static bool IsBackedUp(string relativePath) =>
        !SkippedFolders.Any(f => relativePath.StartsWith(f, StringComparison.OrdinalIgnoreCase));

    public static BackupInfo Read(string file, PortCatalog catalog)
    {
        if (!File.Exists(file))
            throw new BackupException($"{file} doesn't exist.");
        try
        {
            using var zip = ZipFile.OpenRead(file);
            if (zip.GetEntry(Sharing.GamePack.ManifestEntry) is not null && zip.GetEntry(ManifestEntry) is null)
                throw new BackupException($"{Path.GetFileName(file)} is a shared game pack, not a backup. Import it instead.");
            var entry = zip.GetEntry(ManifestEntry)
                ?? throw new BackupException($"{Path.GetFileName(file)} isn't a Marina backup.");

            BackupManifest? manifest;
            using (var reader = entry.Open())
                manifest = JsonSerializer.Deserialize<BackupManifest>(reader, JsonOptions);
            if (manifest is null || manifest.Files is null || string.IsNullOrEmpty(manifest.PortId))
                throw new BackupException($"{Path.GetFileName(file)} is damaged (its manifest is unreadable).");
            if (manifest.Format > BackupManifest.CurrentFormat)
                throw new BackupException($"{Path.GetFileName(file)} was made by a newer Marina. Update the launcher to restore it.");

            var port = catalog.Ports.FirstOrDefault(p => string.Equals(p.Id, manifest.PortId, StringComparison.OrdinalIgnoreCase))
                ?? throw new BackupException($"{Path.GetFileName(file)} is a backup of {manifest.PortName}, which this launcher doesn't know about.");
            return new BackupInfo(file, manifest, port);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or IOException and not FileNotFoundException)
        {
            throw new BackupException($"{Path.GetFileName(file)} is damaged or isn't a Marina backup.", e);
        }
    }

    /// <summary>
    /// Checks the destination drive has room, and (for a single backup file)
    /// can hold a file this big: USB sticks are often FAT32, which can't hold
    /// a file of 4 GB or more.
    /// </summary>
    public static void CheckDestination(string destination, long bytes, bool singleFile)
    {
        DriveInfo drive;
        try
        {
            drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!);
            if (!drive.IsReady) return;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return; // Can't tell (e.g. some network paths); let the write itself decide.
        }

        long needed = bytes + 64L * 1024 * 1024;
        if (drive.AvailableFreeSpace < needed)
            throw new BackupException(
                $"{drive.Name} only has {Size(drive.AvailableFreeSpace)} free, and the backup needs about {Size(needed)}.");

        const long Fat32Limit = 4L * 1024 * 1024 * 1024 - 1;
        if (singleFile && bytes > Fat32Limit && string.Equals(drive.DriveFormat, "FAT32", StringComparison.OrdinalIgnoreCase))
            throw new BackupException(
                $"{drive.Name} is formatted as FAT32, which can't hold files of 4 GB or more, and this backup is about {Size(bytes)}. " +
                "Reformat the drive as exFAT (Windows: right-click the drive > Format), or back up to another drive.");
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
        _ => $"{Math.Max(1, (bytes + 1023) / 1024)} KB",
    };
}
