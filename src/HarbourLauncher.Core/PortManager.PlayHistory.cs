using System.Text.Json;
using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.Install;

namespace HarbourLauncher.Core;

/// <summary>When each game was last played.</summary>
public sealed partial class PortManager
{
    private const string PlayedFile = "played.json";

    private sealed record PlayRecord(DateTimeOffset LastPlayed);

    /// <summary>
    /// When the game was last played: the last time the launcher started it, or,
    /// if it was started some other way since (e.g. from its own .exe), the last
    /// time it wrote its log. Null if there's no sign it has been played.
    /// Kept in the game's own .marina folder, so it moves with backups.
    /// </summary>
    public DateTimeOffset? GetLastPlayed(PortDefinition port)
    {
        string folder = InstallFolder(port);
        if (!Directory.Exists(folder)) return null;

        DateTimeOffset? recorded = null;
        string file = Path.Combine(folder, InstallState.MetaFolder, PlayedFile);
        try
        {
            if (File.Exists(file))
                recorded = JsonSerializer.Deserialize<PlayRecord>(File.ReadAllText(file))?.LastPlayed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable record: fall back to the logs.
        }

        DateTimeOffset? logged = null;
        try
        {
            string logs = Path.Combine(folder, "logs");
            if (Directory.Exists(logs))
            {
                var newest = Directory.EnumerateFiles(logs, "*", SearchOption.AllDirectories)
                    .Select(f => File.GetLastWriteTimeUtc(f))
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();
                if (newest > DateTime.MinValue) logged = new DateTimeOffset(newest, TimeSpan.Zero).ToLocalTime();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return recorded > logged || logged is null ? recorded : logged;
    }

    /// <summary>Records that the game was played at this time. Never stops the game from starting.</summary>
    public void RecordPlayed(PortDefinition port, DateTimeOffset when)
    {
        try
        {
            string meta = Path.Combine(InstallFolder(port), InstallState.MetaFolder);
            Directory.CreateDirectory(meta);
            string file = Path.Combine(meta, PlayedFile);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(new PlayRecord(when)));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to note the time is no reason to fail a launch.
        }
    }
}
