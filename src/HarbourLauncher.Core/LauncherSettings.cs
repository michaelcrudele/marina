using System.Text.Json;

namespace HarbourLauncher.Core;

/// <summary>The player's launcher settings, saved as settings.json.</summary>
public sealed record LauncherSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Where games are installed. Null means the default folder.</summary>
    public string? GamesFolder { get; init; }

    /// <summary>
    /// Folders the player said "not now" to when the launcher found a game
    /// it didn't install, so it doesn't ask again on every start.
    /// </summary>
    public IReadOnlyList<string> DismissedFolders { get; init; } = [];

    /// <summary>Games whose artwork should fill the card, cropping the edges, even though it isn't portrait.</summary>
    public IReadOnlyList<string> CroppedArtwork { get; init; } = [];

    /// <summary>
    /// The colour of buttons, titles and highlights, as "#RRGGBB".
    /// Null means Marina's own orange.
    /// </summary>
    public string? AccentColor { get; init; }

    /// <summary>
    /// Tidies a colour typed or picked by the player into "#RRGGBB" (upper case).
    /// Accepts "#RGB", "#RRGGBB" and "#AARRGGBB" (alpha is dropped), with or without the "#".
    /// Returns false for anything else.
    /// </summary>
    public static bool TryNormalizeColor(string? text, out string normalized)
    {
        normalized = "";
        string hex = (text ?? "").Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        else if (hex.Length == 8) hex = hex[2..];
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit)) return false;
        normalized = "#" + hex.ToUpperInvariant();
        return true;
    }

    /// <summary>Loads settings. A missing or unreadable file gives the defaults.</summary>
    public static LauncherSettings Load(string path)
    {
        if (!File.Exists(path)) return new LauncherSettings();
        try
        {
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), JsonOptions)
                   ?? new LauncherSettings();
        }
        catch (JsonException)
        {
            return new LauncherSettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
