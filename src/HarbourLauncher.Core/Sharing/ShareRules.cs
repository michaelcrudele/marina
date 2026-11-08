namespace HarbourLauncher.Core.Sharing;

/// <summary>
/// Decides which files in a game folder go into a shared .marinapack.
///
/// A pack has two parts:
/// - the player's files (saves, settings, mods, randomizer seeds), which never
///   include programs; and
/// - the port's program files, exactly the files its release installed, so
///   the importer doesn't need to download anything.
/// Neither part ever carries game data: no ROMs and no archive generated from
/// a ROM (e.g. bk.o2r), so whoever imports it supplies their own copy of the game.
/// </summary>
public static class ShareRules
{
    public const string Extension = ".marinapack";

    /// <summary>The extension packs had before the launcher was renamed Marina; still opened.</summary>
    public const string LegacyExtension = ".hlpack";

    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".com", ".scr", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".hta", ".lnk", ".url", ".reg", ".cpl", ".so", ".dylib", ".sh", ".appimage",
    };

    private static readonly HashSet<string> RomExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".z64", ".n64", ".v64", ".rom", ".ndd", ".u64",
    };

    /// <summary>Game-data archives. Allowed inside mods/, never at the top level, where the ROM-generated one lives.</summary>
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".o2r", ".otr" };

    private static readonly string[] SkippedFolders = ["logs/", "debug/", ".marina/", ".harbourlauncher/"];

    /// <summary>Files written alongside a ROM-generated archive; meaningless without it.</summary>
    private static readonly HashSet<string> RomExtractionFiles = new(StringComparer.OrdinalIgnoreCase) { "torch.hash.yml" };

    /// <summary>
    /// True if a file the port's release installed may go into the program part of a pack.
    /// Programs are fine here; ROM data and logs/debug symbols are not.
    /// </summary>
    public static bool IsShareableProgramFile(string relativePath)
    {
        string rel = relativePath.Replace('\\', '/').TrimStart('/');
        if (rel.Length == 0 || rel.EndsWith('/')) return false;
        if (SkippedFolders.Any(f => rel.StartsWith(f, StringComparison.OrdinalIgnoreCase))) return false;
        if (RomExtensions.Contains(Path.GetExtension(rel))) return false;
        return !RomExtractionFiles.Contains(Path.GetFileName(rel));
    }

    /// <summary>
    /// Why a file in the game folder is never shared, or null if it may be
    /// (as a player file or, for release files, as part of the game).
    /// </summary>
    public static string? NeverSharedReason(string relativePath, IReadOnlySet<string> releaseFiles)
    {
        string rel = relativePath.Replace('\\', '/').TrimStart('/');
        if (releaseFiles.Contains(rel))
            return IsShareableProgramFile(rel) ? null : "debug file";
        if (rel.StartsWith(".marina/", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith(".harbourlauncher/", StringComparison.OrdinalIgnoreCase)) return "launcher backup";
        if (rel.StartsWith("logs/", StringComparison.OrdinalIgnoreCase)) return "log";
        if (rel.StartsWith("debug/", StringComparison.OrdinalIgnoreCase)) return "debug file";
        string ext = Path.GetExtension(rel);
        if (RomExtensions.Contains(ext)) return "ROM";
        if (RomExtractionFiles.Contains(Path.GetFileName(rel))) return "made from your ROM";
        if (!rel.Contains('/') && ArchiveExtensions.Contains(ext)) return "made from your ROM";
        if (ProgramExtensions.Contains(ext)) return "program that isn't part of the game's release";
        return null;
    }

    /// <summary>Formats that are already compressed; packing stores them as-is, which is much faster.</summary>
    public static bool IsAlreadyCompressed(string relativePath) => Path.GetExtension(relativePath).ToLowerInvariant() is
        ".o2r" or ".otr" or ".zip" or ".7z" or ".rar" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".ogg" or ".mp3" or ".mp4";

    /// <summary>True for files Windows can run (used to keep the "downloaded file" warning on them).</summary>
    public static bool IsProgram(string relativePath) => ProgramExtensions.Contains(Path.GetExtension(relativePath));

    /// <summary>
    /// True if <paramref name="relativePath"/> (forward slashes, relative to the game folder)
    /// may be shared as one of the player's files. <paramref name="releaseFiles"/> are files the port's release installed;
    /// they're skipped because the importer downloads the release themselves.
    /// </summary>
    public static bool IsShareable(string relativePath, IReadOnlySet<string> releaseFiles)
    {
        string rel = relativePath.Replace('\\', '/').TrimStart('/');
        if (rel.Length == 0 || rel.EndsWith('/')) return false;
        if (releaseFiles.Contains(rel)) return false;
        if (SkippedFolders.Any(f => rel.StartsWith(f, StringComparison.OrdinalIgnoreCase))) return false;

        string name = Path.GetFileName(rel);
        string ext = Path.GetExtension(rel);
        if (ProgramExtensions.Contains(ext) || RomExtensions.Contains(ext)) return false;
        if (RomExtractionFiles.Contains(name)) return false;

        bool topLevel = !rel.Contains('/');
        if (topLevel && ArchiveExtensions.Contains(ext)) return false;
        return true;
    }
}
