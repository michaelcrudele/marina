using System.Text.Json;
using System.Text.Json.Serialization;
using HarbourLauncher.Core.Install;

namespace HarbourLauncher.Core.Saves;

/// <summary>One named set of saves for a game, e.g. "Main playthrough" or "Randomizer".</summary>
public sealed record SaveProfile(string Id, string Name, DateTimeOffset CreatedAt);

/// <summary>A profile as shown to the player: whether it's the one the game is using, and how big it is.</summary>
public sealed record SaveProfileInfo(SaveProfile Profile, bool IsActive, long Bytes, DateTimeOffset? LastChanged)
{
    public string Id => Profile.Id;
    public string Name => Profile.Name;
}

/// <summary>
/// The list of a game's save profiles, stored in .marina/profiles/profiles.json.
/// The active profile's saves are where the game expects them; every other
/// profile's saves are kept in .marina/profiles/&lt;id&gt;/, inside the game folder,
/// so they travel with it in backups and when the folder is moved.
/// A game with no profiles.json has one implicit profile, "Default", holding whatever saves it has.
/// </summary>
public sealed record SaveProfiles(string ActiveId, IReadOnlyList<SaveProfile> Profiles)
{
    /// <summary>Relative to the install folder (forward slashes).</summary>
    public const string StoreFolder = InstallState.MetaFolder + "/profiles";

    public const string DefaultId = "default";
    public const string DefaultName = "Default";
    public const int MaxNameLength = 40;

    private const string FileName = "profiles.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [JsonIgnore]
    public SaveProfile Active => Profiles.First(p => p.Id == ActiveId);

    public static string StorePath(string installDir) => Path.Combine(installDir, InstallState.MetaFolder, "profiles");

    /// <summary>Where an inactive profile's saves are kept.</summary>
    public static string ProfileFolder(string installDir, string id) => Path.Combine(StorePath(installDir), id);

    public static SaveProfiles Load(string installDir)
    {
        string path = Path.Combine(StorePath(installDir), FileName);
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<SaveProfiles>(File.ReadAllText(path), JsonOptions);
                if (loaded is { Profiles.Count: > 0 } && loaded.Profiles.Any(p => p.Id == loaded.ActiveId))
                    return loaded;
            }
            catch (JsonException)
            {
                // Fall through to the implicit profile; the saves themselves are untouched.
            }
        }
        return new SaveProfiles(DefaultId, [new SaveProfile(DefaultId, DefaultName, DateTimeOffset.UtcNow)]);
    }

    public void Save(string installDir)
    {
        string dir = StorePath(installDir);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, FileName);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Trims a profile name and checks it's usable and not already taken; returns the trimmed name.</summary>
    public string CheckName(string name, string? exceptId = null)
    {
        string trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Give the profile a name.");
        if (trimmed.Length > MaxNameLength)
            throw new ArgumentException($"Keep the name to {MaxNameLength} characters or fewer.");
        if (Profiles.Any(p => p.Id != exceptId && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"There's already a profile called {trimmed}.");
        return trimmed;
    }
}
