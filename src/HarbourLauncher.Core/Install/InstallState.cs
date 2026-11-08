using System.Text.Json;

namespace HarbourLauncher.Core.Install;

/// <summary>
/// What the launcher installed into a folder: which release, and exactly
/// which files came from it. The file list is what lets an update remove
/// files a newer release dropped without touching anything the user added.
/// </summary>
public sealed record InstallState(
    string PortId,
    string Tag,
    string AssetName,
    DateTimeOffset InstalledAt,
    IReadOnlyList<string> Files)
{
    public const string MetaFolder = ".marina";

    /// <summary>The folder's name before the launcher was renamed Marina. Renamed to <see cref="MetaFolder"/> when found.</summary>
    public const string LegacyMetaFolder = ".harbourlauncher";

    /// <summary>The tag recorded for a game the player installed by hand before using the launcher.</summary>
    public const string UnknownTag = "unknown";

    public bool IsVersionKnown => !string.Equals(Tag, UnknownTag, StringComparison.Ordinal);
    private const string FileName = "state.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string PathFor(string installDir)
    {
        string path = Path.Combine(installDir, MetaFolder, FileName);
        string legacy = Path.Combine(installDir, LegacyMetaFolder, FileName);
        return !File.Exists(path) && File.Exists(legacy) ? legacy : path; // Not migrated yet.
    }

    public static InstallState? Load(string installDir)
    {
        MigrateLegacyFolder(installDir);
        var path = PathFor(installDir);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<InstallState>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null; // Treated like a folder the launcher didn't install.
        }
    }

    /// <summary>
    /// Games installed before the rename keep their record in .harbourlauncher;
    /// rename it to .marina the first time the game is looked at. If the rename
    /// can't happen (e.g. a file in it is open), the old folder is still read.
    /// </summary>
    public static void MigrateLegacyFolder(string installDir)
    {
        string legacy = Path.Combine(installDir, LegacyMetaFolder);
        string current = Path.Combine(installDir, MetaFolder);
        if (!Directory.Exists(legacy) || Directory.Exists(current)) return;
        try
        {
            Directory.Move(legacy, current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Try again next time.
        }
    }

    public void Save(string installDir)
    {
        var path = PathFor(installDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
