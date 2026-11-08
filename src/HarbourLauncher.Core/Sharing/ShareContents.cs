using HarbourLauncher.Core.Catalog;

namespace HarbourLauncher.Core.Sharing;

public enum ShareItemKind { Game, Settings, Folder, Mod, Artwork }

/// <summary>
/// One thing the player can choose to put in a pack, e.g. "the game", "Saves"
/// or a single mod. <see cref="Files"/> are relative to the game folder.
/// </summary>
public sealed record ShareItem(string Id, ShareItemKind Kind, string Label, string Detail, IReadOnlyList<string> Files, long Bytes);

/// <summary>A file that's never put in a pack, and why (shown so nothing is left out silently).</summary>
public sealed record ExcludedFile(string File, long Bytes, string Reason);

/// <summary>Everything that could go into a pack for one game, with sizes.</summary>
/// <param name="NoGameReason">Why the game itself can't be included, or null if it can.</param>
public sealed record ShareContents(
    PortDefinition Port,
    IReadOnlyList<ShareItem> Items,
    IReadOnlyList<ExcludedFile> NeverIncluded,
    string? NoGameReason)
{
    public const string GameId = "game";
    public const string SettingsId = "settings";
    public const string ArtworkId = "artwork";
}
