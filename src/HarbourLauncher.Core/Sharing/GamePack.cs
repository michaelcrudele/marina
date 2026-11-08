using System.IO.Compression;
using System.Text.Json;
using HarbourLauncher.Core.Catalog;

namespace HarbourLauncher.Core.Sharing;

/// <summary>What a .marinapack says about itself (manifest.json at the root of the zip).</summary>
/// <param name="Files">The player's files (under game/ in the zip).</param>
/// <param name="ProgramFiles">The port's release files (under program/), or empty if the pack doesn't carry the game.</param>
public sealed record PackManifest(
    int Format,
    string PortId,
    string PortName,
    string? Tag,
    DateTimeOffset ExportedAt,
    string? Artwork,
    IReadOnlyList<string> Files,
    IReadOnlyList<string>? ProgramFiles = null,
    string? AssetName = null)
{
    /// <summary>1: player files only. 2: can also carry the game's program files.</summary>
    public const int CurrentFormat = 2;

    public bool HasProgram => ProgramFiles is { Count: > 0 };
}

/// <summary>A pack that was opened and checked, ready to show the player before importing.</summary>
public sealed record PackInfo(string File, PackManifest Manifest, PortDefinition Port, long Bytes, long ProgramBytes);

public sealed record ExportResult(string File, int FileCount, int ProgramFileCount, long Bytes);

/// <param name="InstalledGame">The game was installed by this import.</param>
/// <param name="InstalledFromPack">It was installed from the pack's program files rather than downloaded.</param>
public sealed record ImportResult(PortDefinition Port, int FileCount, bool InstalledGame, bool InstalledFromPack, string? BackupFolder);

/// <summary>A .marinapack file is damaged, from a newer launcher, or for a game this launcher doesn't know.</summary>
public sealed class PackException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The .marinapack format: a zip with manifest.json, the player's files under
/// game/, and optionally their custom artwork.
/// </summary>
internal static class GamePack
{
    public const string ManifestEntry = "manifest.json";
    public const string GamePrefix = "game/";
    public const string ProgramPrefix = "program/";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Writes a pack. Already-compressed files (texture packs, images) are stored
    /// as-is, which makes multi-gigabyte packs far faster to create.
    /// Progress is reported from 0.0 to 1.0 by bytes written.
    /// </summary>
    public static void Write(string destination, PackManifest manifest, string gameFolder, string? artworkFile,
                             IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var sources = manifest.Files.Select(rel => (Path.Combine(gameFolder, rel), GamePrefix + rel))
            .Concat((manifest.ProgramFiles ?? []).Select(rel => (Path.Combine(gameFolder, rel), ProgramPrefix + rel)))
            .ToList();
        if (artworkFile is not null && manifest.Artwork is not null)
            sources.Add((artworkFile, manifest.Artwork));

        ZipWriter.Write(destination, ManifestEntry, manifest, sources, progress, ct);
    }

    public static PackInfo Read(string file, PortCatalog catalog)
    {
        if (!File.Exists(file))
            throw new PackException($"{file} doesn't exist.");
        try
        {
            using var zip = ZipFile.OpenRead(file);
            if (zip.GetEntry(Backup.GameBackup.ManifestEntry) is not null)
                throw new PackException($"{Path.GetFileName(file)} is a game backup, not a shared pack. Restore it instead.");
            var manifestEntry = zip.GetEntry(ManifestEntry)
                ?? throw new PackException($"{Path.GetFileName(file)} isn't a Marina game pack.");

            PackManifest? manifest;
            using (var reader = manifestEntry.Open())
                manifest = JsonSerializer.Deserialize<PackManifest>(reader, JsonOptions);
            if (manifest is null || manifest.Files is null || string.IsNullOrEmpty(manifest.PortId))
                throw new PackException($"{Path.GetFileName(file)} is damaged (its manifest is unreadable).");
            if (manifest.Format > PackManifest.CurrentFormat)
                throw new PackException($"{Path.GetFileName(file)} was made by a newer Marina. Update the launcher to import it.");

            var port = catalog.Ports.FirstOrDefault(p => string.Equals(p.Id, manifest.PortId, StringComparison.OrdinalIgnoreCase))
                ?? throw new PackException($"{Path.GetFileName(file)} is for {manifest.PortName}, which this launcher doesn't know about.");

            long bytes = zip.Entries.Where(e => e.FullName.StartsWith(GamePrefix, StringComparison.Ordinal)).Sum(e => e.Length);
            long programBytes = zip.Entries.Where(e => e.FullName.StartsWith(ProgramPrefix, StringComparison.Ordinal)).Sum(e => e.Length);
            return new PackInfo(file, manifest with { ProgramFiles = manifest.ProgramFiles ?? [] }, port, bytes, programBytes);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or IOException and not FileNotFoundException)
        {
            throw new PackException($"{Path.GetFileName(file)} is damaged or isn't a Marina game pack.", e);
        }
    }
}
