using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;
using HarbourLauncher.Core.Launch;

namespace HarbourLauncher.Core;

public sealed record PortStatus(PortDefinition Port, InstallState? Installed, ReleaseInfo Latest)
{
    public bool IsInstalled => Installed is not null;
    public bool UpdateAvailable => Installed is not null && !string.Equals(Installed.Tag, Latest.Tag, StringComparison.Ordinal);
}

public enum InstallStage { Checking, Downloading, Installing, Done }

public sealed record InstallProgress(InstallStage Stage, double Fraction = 0);

public sealed class GameRunningException(PortDefinition port, string action = "updating")
    : Exception($"Close {port.Name} before {action} it.");

/// <summary>The games folder could not be changed. Nothing was moved.</summary>
public sealed class GamesFolderException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record GamesFolderChange(string Folder, IReadOnlyList<PortDefinition> Moved);

/// <summary>
/// A game found in the games folder that the launcher didn't install.
/// <see cref="NeedsRename"/> is true when its folder isn't named after the
/// port id yet (e.g. "Ship of Harkinian" instead of "soh").
/// </summary>
public sealed record FoundGame(PortDefinition Port, string Folder, string TargetFolder)
{
    public bool NeedsRename => !string.Equals(
        Path.TrimEndingDirectorySeparator(Folder), Path.TrimEndingDirectorySeparator(TargetFolder),
        StringComparison.OrdinalIgnoreCase);
}

/// <summary>Ties the catalog, GitHub, downloads and installer together. The UI talks to this.</summary>
public sealed partial class PortManager(
    PortCatalog catalog,
    GitHubReleaseClient releases,
    AssetDownloader downloader,
    PortInstaller installer,
    LauncherPaths paths)
{
    private LauncherPaths paths = paths;

    public PortCatalog Catalog { get; } = catalog;

    /// <summary>The folder that holds one subfolder per game.</summary>
    public string GamesFolder => paths.Ports;

    public bool IsDefaultGamesFolder => paths.IsDefaultGamesFolder;

    /// <summary>The games folder used when the player hasn't picked one.</summary>
    public string DefaultGamesFolder => paths.WithGamesFolder(null).Ports;

    public string InstallFolder(PortDefinition port) => paths.PortFolder(port.Id);

    public InstallState? GetInstalled(PortDefinition port) => InstallState.Load(InstallFolder(port));

    /// <param name="force">Ask GitHub now, even if the release was confirmed within the last hour.</param>
    public async Task<PortStatus> CheckAsync(PortDefinition port, bool force = false, CancellationToken ct = default) =>
        new(port, GetInstalled(port), await releases.GetLatestReleaseAsync(port.Repo, force, ct).ConfigureAwait(false));

    /// <summary>
    /// Installs the latest release, or updates to it. Returns null when the
    /// installed version is already the latest.
    /// </summary>
    public async Task<InstallResult?> InstallLatestAsync(
        PortDefinition port, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        string folder = InstallFolder(port);
        if (GameLauncher.IsRunning(port, folder))
            throw new GameRunningException(port);

        progress?.Report(new InstallProgress(InstallStage.Checking));
        var status = await CheckAsync(port, force: true, ct).ConfigureAwait(false); // Install what's really latest.
        if (status.IsInstalled && !status.UpdateAvailable)
            return null;

        var asset = status.Latest.FindAsset(port.WindowsAsset);
        Directory.CreateDirectory(paths.Downloads);
        string zip = Path.Combine(paths.Downloads, asset.Name);
        try
        {
            await downloader.DownloadAsync(
                asset, zip,
                new Progress<double>(f => progress?.Report(new InstallProgress(InstallStage.Downloading, f))),
                ct).ConfigureAwait(false);

            progress?.Report(new InstallProgress(InstallStage.Installing));
            var result = await Task.Run(
                () => installer.Install(port, zip, status.Latest.Tag, asset.Name, folder), ct).ConfigureAwait(false);

            progress?.Report(new InstallProgress(InstallStage.Done, 1));
            return result;
        }
        finally
        {
            File.Delete(zip);
        }
    }

    /// <summary>
    /// Switches to a new games folder and saves the choice. With
    /// <paramref name="moveInstalled"/>, installed games (with their saves,
    /// mods and settings) move to the new folder; otherwise they stay where
    /// they are and the library shows what is in the new folder.
    /// Everything is checked before anything moves; if a move fails part way,
    /// games already moved are moved back.
    /// </summary>
    public async Task<GamesFolderChange> ChangeGamesFolderAsync(
        string? folder, bool moveInstalled, CancellationToken ct = default)
    {
        if (folder is not null && !Path.IsPathFullyQualified(folder))
            throw new GamesFolderException("Choose a full folder path, like Z:\\Games\\HarbourMasters.");

        var next = paths.WithGamesFolder(folder);
        if (string.Equals(next.Ports, paths.Ports, StringComparison.OrdinalIgnoreCase))
        {
            SaveSettings(folder, next);
            return new GamesFolderChange(next.Ports, []);
        }

        var toMove = moveInstalled ? Catalog.Ports.Where(p => GetInstalled(p) is not null).ToList() : [];
        foreach (var port in toMove)
        {
            string from = InstallFolder(port);
            string to = next.PortFolder(port.Id);
            if (GameLauncher.IsRunning(port, from))
                throw new GameRunningException(port, "moving");
            if (FolderMover.IsSameOrInside(to, from))
                throw new GamesFolderException($"The new folder is inside {port.Name}'s folder. Choose a different one.");
            if (!FolderMover.IsEmptyOrMissing(to))
                throw new GamesFolderException(
                    $"{to} already exists and isn't empty, so {port.Name} can't be moved there. " +
                    "Move or rename that folder, or choose a different one.");
        }

        CheckWritable(next.Ports);

        var moved = new List<PortDefinition>();
        try
        {
            foreach (var port in toMove)
            {
                ct.ThrowIfCancellationRequested();
                string from = InstallFolder(port), to = next.PortFolder(port.Id);
                await Task.Run(() => FolderMover.Move(from, to), CancellationToken.None).ConfigureAwait(false);
                moved.Add(port);
            }
        }
        catch (Exception e)
        {
            foreach (var port in moved)
            {
                try { FolderMover.Move(next.PortFolder(port.Id), InstallFolder(port)); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            throw e is OperationCanceledException ? e
                : new GamesFolderException($"Couldn't move your games: {e.Message} Nothing was changed.", e);
        }

        SaveSettings(folder, next);
        return new GamesFolderChange(next.Ports, moved);
    }

    private void SaveSettings(string? folder, LauncherPaths next)
    {
        // Store null for the default folder, so it follows the default if that ever changes.
        (LoadSettings() with { GamesFolder = next.IsDefaultGamesFolder ? null : Path.GetFullPath(folder!) })
            .Save(paths.SettingsFile);
        paths = next;
    }

    private LauncherSettings LoadSettings() => LauncherSettings.Load(paths.SettingsFile);

    // ---- Accent colour ----

    /// <summary>The player's accent colour as "#RRGGBB", or null for Marina's own orange.</summary>
    public string? AccentColor =>
        LauncherSettings.TryNormalizeColor(LoadSettings().AccentColor, out string color) ? color : null;

    /// <summary>Saves the accent colour. Null goes back to Marina's own orange.</summary>
    public void SetAccentColor(string? color)
    {
        string? value = null;
        if (color is not null)
        {
            if (!LauncherSettings.TryNormalizeColor(color, out string normalized))
                throw new ArgumentException($"\"{color}\" isn't a colour. Use a hex code like #EB802B.", nameof(color));
            value = normalized;
        }

        var settings = LoadSettings();
        if (settings.AccentColor != value)
            (settings with { AccentColor = value }).Save(paths.SettingsFile);
    }

    // ---- Custom artwork ----

    /// <summary>Image types the launcher accepts for artwork.</summary>
    public static IReadOnlyList<string> ArtworkExtensions { get; } = [".png", ".jpg", ".jpeg", ".bmp", ".webp"];

    private const long MaxArtworkBytes = 25 * 1024 * 1024;

    /// <summary>The player's image for this game, or null to use the default banner.</summary>
    public string? GetArtwork(PortDefinition port) =>
        ArtworkExtensions.Select(e => Path.Combine(paths.Artwork, port.Id + e)).FirstOrDefault(File.Exists);

    /// <summary>
    /// Copies <paramref name="imageFile"/> into the launcher's artwork folder for
    /// this game, so it keeps working if the original is moved or deleted.
    /// </summary>
    public string SetArtwork(PortDefinition port, string imageFile)
    {
        var info = new FileInfo(imageFile);
        if (!info.Exists)
            throw new FileNotFoundException($"{imageFile} doesn't exist.", imageFile);
        string ext = info.Extension.ToLowerInvariant();
        if (!ArtworkExtensions.Contains(ext) || !LooksLikeImage(imageFile, ext))
            throw new InvalidDataException($"{info.Name} isn't a PNG, JPEG, BMP or WebP image.");
        if (info.Length > MaxArtworkBytes)
            throw new InvalidDataException($"{info.Name} is larger than 25 MB. Choose a smaller image.");

        Directory.CreateDirectory(paths.Artwork);
        string target = Path.Combine(paths.Artwork, port.Id + ext);
        string temp = target + ".tmp";
        File.Copy(imageFile, temp, overwrite: true);
        ClearArtwork(port);
        File.Move(temp, target);
        return target;
    }

    public void ClearArtwork(PortDefinition port)
    {
        foreach (string ext in ArtworkExtensions)
            File.Delete(Path.Combine(paths.Artwork, port.Id + ext));
        SetArtworkCropped(port, false); // A new image starts uncropped.
    }

    /// <summary>True when the player chose to crop this game's artwork to fill its card.</summary>
    public bool IsArtworkCropped(PortDefinition port) =>
        LoadSettings().CroppedArtwork.Contains(port.Id, StringComparer.OrdinalIgnoreCase);

    public void SetArtworkCropped(PortDefinition port, bool cropped)
    {
        var settings = LoadSettings();
        var list = settings.CroppedArtwork.Where(id => !string.Equals(id, port.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cropped) list.Add(port.Id);
        if (list.Count != settings.CroppedArtwork.Count || cropped != IsArtworkCropped(port))
            (settings with { CroppedArtwork = list }).Save(paths.SettingsFile);
    }

    /// <summary>Checks the file's first bytes, so a renamed non-image is caught early.</summary>
    private static bool LooksLikeImage(string file, string ext)
    {
        Span<byte> head = stackalloc byte[12];
        int read;
        using (var stream = File.OpenRead(file))
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        head = head[..read];
        return ext switch
        {
            ".png" => head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]),
            ".jpg" or ".jpeg" => head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]),
            ".bmp" => head.StartsWith("BM"u8),
            ".webp" => head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8),
            _ => false,
        };
    }

    // ---- Games installed by hand ----

    /// <summary>
    /// Looks through the games folder for games the launcher didn't install:
    /// any folder whose top level holds a port's exe. Folders the player
    /// dismissed are skipped unless <paramref name="includeDismissed"/>.
    /// </summary>
    public IReadOnlyList<FoundGame> FindExistingGames(bool includeDismissed = false)
    {
        if (!Directory.Exists(paths.Ports)) return [];

        var dismissed = includeDismissed
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(LoadSettings().DismissedFolders.Select(Normalize), StringComparer.OrdinalIgnoreCase);

        List<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(paths.Ports)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .Where(d => InstallState.Load(d) is null) // Already the launcher's.
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var found = new List<FoundGame>();
        foreach (var port in Catalog.Ports)
        {
            if (GetInstalled(port) is not null) continue;
            string target = InstallFolder(port);

            var matches = folders.Where(d => HasExe(d, port.Exe)).ToList();
            // Prefer a folder already named after the port.
            string? folder = matches.FirstOrDefault(d => SameFolder(d, target))
                             ?? (FolderMover.IsEmptyOrMissing(target) ? matches.FirstOrDefault() : null);
            if (folder is null || dismissed.Contains(Normalize(folder))) continue;

            found.Add(new FoundGame(port, Normalize(folder), Normalize(target)));
            folders.Remove(folder);
        }
        return found;
    }

    /// <summary>
    /// Adds a game the player installed by hand. Its folder is renamed to the
    /// port id if needed; nothing inside it changes except a small record in
    /// .marina. Its version is unknown until the first update.
    /// </summary>
    public void AddExistingGame(FoundGame game)
    {
        var port = game.Port;
        if (GetInstalled(port) is not null) return;
        if (!HasExe(game.Folder, port.Exe))
            throw new FileNotFoundException($"{port.Exe} is no longer in {game.Folder}.");

        string target = InstallFolder(port);
        if (game.NeedsRename)
        {
            if (GameLauncher.IsRunning(port, game.Folder))
                throw new GameRunningException(port, "adding");
            if (!FolderMover.IsEmptyOrMissing(target))
                throw new IOException($"{target} already exists, so {game.Folder} can't be renamed to it.");
            if (Directory.Exists(target)) Directory.Delete(target);
            Directory.Move(game.Folder, target);
        }

        new InstallState(port.Id, InstallState.UnknownTag, "", DateTimeOffset.UtcNow, []).Save(target);
        UndismissFolder(game.Folder);
    }

    /// <summary>Remembers not to offer these again (until the player asks to look).</summary>
    public void DismissFoundGames(IEnumerable<FoundGame> games)
    {
        var settings = LoadSettings();
        var list = settings.DismissedFolders.Select(Normalize)
            .Concat(games.Select(g => Normalize(g.Folder)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        (settings with { DismissedFolders = list }).Save(paths.SettingsFile);
    }

    private void UndismissFolder(string folder)
    {
        var settings = LoadSettings();
        var list = settings.DismissedFolders.Where(d => !SameFolder(d, folder)).ToList();
        if (list.Count != settings.DismissedFolders.Count)
            (settings with { DismissedFolders = list }).Save(paths.SettingsFile);
    }

    private static bool HasExe(string folder, string exe)
    {
        try
        {
            string path = Path.Combine(folder, exe);
            string? dir = Path.GetDirectoryName(path);
            if (dir is null || !Directory.Exists(dir)) return false;
            string name = Path.GetFileName(path);
            return Directory.EnumerateFiles(dir).Any(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private static bool SameFolder(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static void CheckWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".marina-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new GamesFolderException($"Can't save games to {folder}: {e.Message}", e);
        }
    }

    /// <summary>Starts the game and records when it was played.</summary>
    public System.Diagnostics.Process Launch(PortDefinition port)
    {
        var process = GameLauncher.Launch(port, InstallFolder(port));
        RecordPlayed(port, DateTimeOffset.Now);
        return process;
    }
}
