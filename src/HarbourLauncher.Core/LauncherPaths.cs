namespace HarbourLauncher.Core;

/// <summary>
/// Where the launcher keeps things. The launcher's own data (downloads, cache,
/// settings) lives under <see cref="Root"/>; the games themselves live under
/// <see cref="Ports"/>, which the player can move anywhere.
/// </summary>
public sealed class LauncherPaths(string root, string? gamesFolder = null)
{
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>The folder that holds one subfolder per installed game.</summary>
    public string Ports { get; } = Path.GetFullPath(gamesFolder ?? Path.Combine(root, "ports"));

    /// <summary>True when the games folder is the default one under <see cref="Root"/>.</summary>
    public bool IsDefaultGamesFolder => string.Equals(
        Ports, Path.GetFullPath(Path.Combine(Root, "ports")), StringComparison.OrdinalIgnoreCase);

    public string Downloads => Path.Combine(Root, "downloads");
    public string ReleaseCacheFile => Path.Combine(Root, "release-cache.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Custom icons and box art the player picked, one image per game.</summary>
    public string Artwork => Path.Combine(Root, "artwork");

    public string PortFolder(string portId) => Path.Combine(Ports, portId);

    public LauncherPaths WithGamesFolder(string? folder) => new(Root, folder);

    /// <summary>%LOCALAPPDATA%\Marina on Windows.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Marina");

    /// <summary>Where the launcher kept its data before it was renamed Marina.</summary>
    public static string LegacyDefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HarbourLauncher");

    /// <summary>The default root, with the games folder the player chose in settings.</summary>
    public static LauncherPaths Default() => FromSettings(MigrateRoot(LegacyDefaultRoot, DefaultRoot));

    /// <summary>
    /// Moves the launcher's data (settings, artwork, cache, and games in the
    /// default games folder) from its pre-rename folder to the new one, once.
    /// Returns the folder to use: the old one if it couldn't be moved yet.
    /// </summary>
    public static string MigrateRoot(string legacyRoot, string root)
    {
        if (Directory.Exists(root) || !Directory.Exists(legacyRoot)) return root;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(root))!);
            Directory.Move(legacyRoot, root);
            return root;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return legacyRoot; // E.g. a file in it is open; carry on there and try again next start.
        }
    }

    public static LauncherPaths FromSettings(string root)
    {
        var settings = LauncherSettings.Load(Path.Combine(root, "settings.json"));
        return new LauncherPaths(root, settings.GamesFolder);
    }
}
