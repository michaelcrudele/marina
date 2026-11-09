namespace HarbourLauncher.Core.Catalog;

/// <summary>
/// Describes one HarbourMasters port: where its releases live and how its
/// Windows release zip is laid out. This is data, not code, so adding a new
/// port means adding an entry to ports.json.
/// </summary>
public sealed record PortDefinition
{
    /// <summary>Short stable id, used for folder names and CLI arguments.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The original game this port runs.</summary>
    public required string Game { get; init; }

    /// <summary>GitHub repository as "owner/name".</summary>
    public required string Repo { get; init; }

    /// <summary>Glob matching exactly one Windows asset in a release.</summary>
    public required string WindowsAsset { get; init; }

    /// <summary>Executable path, relative to the install folder.</summary>
    public required string Exe { get; init; }

    /// <summary>Release-zip paths never extracted (e.g. "debug/" symbols).</summary>
    public IReadOnlyList<string> Skip { get; init; } = [];

    /// <summary>
    /// Release-zip paths the user may edit. Written on first install, then
    /// left alone on updates. Files that are not in the release zip at all
    /// (saves, ROM-generated archives, mods) are always left alone and do
    /// not need to be listed here.
    /// </summary>
    public IReadOnlyList<string> Keep { get; init; } = [];

    /// <summary>Folder the port loads mods from, relative to the install folder.</summary>
    public string ModsFolder { get; init; } = "mods";

    /// <summary>
    /// Top-level files or folders in the install folder that hold the player's saves.
    /// Switching save profiles swaps exactly these. Matched ignoring case, so the
    /// default covers "Save", "save", "Saves" and "saves". Settings are not included.
    /// </summary>
    public IReadOnlyList<string> Saves { get; init; } = ["Save", "Saves"];
}
