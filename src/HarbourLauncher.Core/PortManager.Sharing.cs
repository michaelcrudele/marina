using System.IO.Compression;
using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.Install;
using HarbourLauncher.Core.Launch;
using HarbourLauncher.Core.Sharing;

namespace HarbourLauncher.Core;

/// <summary>Sharing a game as a .marinapack file: the game's program files plus the player's saves, settings and mods.</summary>
public sealed partial class PortManager
{
    private const string ImportBackupFolder = "import-backup";

    /// <summary>The player's files that would go into a pack (see <see cref="ShareRules"/>).</summary>
    public IReadOnlyList<string> ShareableFiles(PortDefinition port)
    {
        var state = GetInstalled(port) ?? throw new InvalidOperationException($"{port.Name} isn't installed.");
        string folder = InstallFolder(port);
        var releaseFiles = new HashSet<string>(state.Files.Select(Glob.NormalizePath), StringComparer.OrdinalIgnoreCase);

        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Glob.NormalizePath(Path.GetRelativePath(folder, f)))
            .Where(rel => ShareRules.IsShareable(rel, releaseFiles))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The game's own files that would go into a pack: exactly what its release installed.
    /// Empty when the launcher doesn't know which files those are (a game added
    /// from the games folder that hasn't been updated through the launcher yet).
    /// </summary>
    public IReadOnlyList<string> ShareableProgramFiles(PortDefinition port)
    {
        var state = GetInstalled(port) ?? throw new InvalidOperationException($"{port.Name} isn't installed.");
        string folder = InstallFolder(port);
        return state.Files
            .Select(Glob.NormalizePath)
            .Where(ShareRules.IsShareableProgramFile)
            .Where(rel => File.Exists(Path.Combine(folder, rel)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// What could go into a pack for this game, grouped the way a player thinks
    /// about it (the game, settings, saves, each mod, artwork), with sizes, plus
    /// the files that are never packed.
    /// </summary>
    public ShareContents GetShareContents(PortDefinition port)
    {
        var state = GetInstalled(port) ?? throw new InvalidOperationException($"{port.Name} isn't installed.");
        string folder = InstallFolder(port);
        long Size(string rel) => new FileInfo(Path.Combine(folder, rel)).Length;
        var items = new List<ShareItem>();

        var program = ShareableProgramFiles(port);
        string? noGame = null;
        if (program.Count > 0)
        {
            string version = state.IsVersionKnown ? $" {state.Tag}" : "";
            items.Add(new ShareItem(ShareContents.GameId, ShareItemKind.Game, $"{port.Name}{version}",
                "The game itself. Without it, whoever imports the pack downloads the latest version.",
                program, program.Sum(Size)));
        }
        else
        {
            noGame = $"{port.Name} itself can't be included yet: it was added from your games folder, so the launcher " +
                     $"doesn't know which files are the game's. Update it once in the launcher to fix that.";
        }

        var files = ShareableFiles(port);
        var loose = files.Where(f => !f.Contains('/')).ToList();
        if (loose.Count > 0)
            items.Add(new ShareItem(ShareContents.SettingsId, ShareItemKind.Settings, "Settings",
                string.Join(", ", loose), loose, loose.Sum(Size)));

        foreach (var group in files.Where(f => f.Contains('/')).GroupBy(f => f[..f.IndexOf('/')], StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(g => g.Key.Equals("mods", StringComparison.OrdinalIgnoreCase)) // Saves etc. first, mods last.
                                   .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Equals("mods", StringComparison.OrdinalIgnoreCase))
            {
                // One choice per mod: a file or folder directly inside mods/.
                foreach (var mod in group.GroupBy(f => f.Split('/')[1], StringComparer.OrdinalIgnoreCase)
                                         .OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var modFiles = mod.ToList();
                    long bytes = modFiles.Sum(Size);
                    if (bytes == 0) continue; // Placeholders like custom_mod_files_go_here.txt.
                    string detail = modFiles.Count == 1 ? "Mod" : $"Mod folder, {modFiles.Count} files";
                    items.Add(new ShareItem("mod:" + mod.Key, ShareItemKind.Mod, mod.Key, detail, modFiles, bytes));
                }
                continue;
            }

            var folderFiles = group.ToList();
            string label = char.ToUpperInvariant(group.Key[0]) + group.Key[1..];
            items.Add(new ShareItem("folder:" + group.Key, ShareItemKind.Folder, label,
                folderFiles.Count == 1 ? "1 file" : $"{folderFiles.Count} files", folderFiles, folderFiles.Sum(Size)));
        }

        if (GetArtwork(port) is { } art)
            items.Add(new ShareItem(ShareContents.ArtworkId, ShareItemKind.Artwork, "Artwork",
                "Your custom icon or box art", [], new FileInfo(art).Length));

        var release = new HashSet<string>(state.Files.Select(Glob.NormalizePath), StringComparer.OrdinalIgnoreCase);
        var never = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Glob.NormalizePath(Path.GetRelativePath(folder, f)))
            .Select(rel => (rel, reason: ShareRules.NeverSharedReason(rel, release)))
            .Where(x => x.reason is not null && x.reason != "launcher backup")
            .Select(x => new ExcludedFile(x.rel, Size(x.rel), x.reason!))
            .OrderByDescending(x => x.Bytes)
            .ToList();

        return new ShareContents(port, items, never, noGame);
    }

    /// <summary>
    /// Saves a game to a .marinapack file. <paramref name="itemIds"/> picks what goes
    /// in (ids from <see cref="GetShareContents"/>); null means everything.
    /// </summary>
    public ExportResult ExportGame(PortDefinition port, string destination, IReadOnlyCollection<string>? itemIds = null,
                                   IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var state = GetInstalled(port) ?? throw new InvalidOperationException($"{port.Name} isn't installed.");
        if (!destination.EndsWith(ShareRules.Extension, StringComparison.OrdinalIgnoreCase))
            destination += ShareRules.Extension;

        var contents = GetShareContents(port);
        var chosen = contents.Items
            .Where(i => itemIds is null || itemIds.Contains(i.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (chosen.Count == 0)
            throw new InvalidOperationException("Choose at least one thing to include in the pack.");

        var programFiles = chosen.FirstOrDefault(i => i.Kind == ShareItemKind.Game)?.Files ?? [];
        var files = chosen.Where(i => i.Kind is not (ShareItemKind.Game or ShareItemKind.Artwork))
                          .SelectMany(i => i.Files).ToList();
        string? artwork = chosen.Any(i => i.Kind == ShareItemKind.Artwork) ? GetArtwork(port) : null;

        var manifest = new PackManifest(
            PackManifest.CurrentFormat, port.Id, port.Name,
            state.IsVersionKnown ? state.Tag : null,
            DateTimeOffset.UtcNow,
            artwork is null ? null : "artwork" + Path.GetExtension(artwork).ToLowerInvariant(),
            files,
            programFiles,
            programFiles.Count > 0 ? state.AssetName : null);

        GamePack.Write(destination, manifest, InstallFolder(port), artwork, progress, ct);
        return new ExportResult(destination, files.Count, programFiles.Count, new FileInfo(destination).Length);
    }

    /// <summary>Opens and checks a pack without changing anything.</summary>
    public PackInfo ReadPack(string file) => GamePack.Read(file, Catalog);

    /// <summary>
    /// Imports a pack.
    /// - Game not installed: it's installed from the pack's program files, or
    ///   downloaded from GitHub if the pack doesn't carry them.
    /// - Game installed: the pack's program files are ignored; the installed
    ///   version stays (updating is up to the player).
    /// Then the player's files are added. Files that get replaced are moved to
    /// .marina\import-backup; if anything fails, they're put back.
    /// The pack's artwork is used only if the game has none yet.
    /// </summary>
    public async Task<ImportResult> ImportPackAsync(
        string file, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var info = ReadPack(file);
        var port = info.Port;
        string folder = InstallFolder(port);

        if (GameLauncher.IsRunning(port, folder))
            throw new GameRunningException(port, "importing into");

        bool installGame = GetInstalled(port) is null;
        bool fromPack = installGame && info.Manifest.HasProgram;
        if (installGame && !fromPack)
            await InstallLatestAsync(port, progress, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress(InstallStage.Installing));
        var (written, backedUp) = await Task.Run(() => ExtractPack(info, folder, fromPack), ct).ConfigureAwait(false);

        if (info.Manifest.Artwork is not null && GetArtwork(port) is null)
            TryImportArtwork(info);

        progress?.Report(new InstallProgress(InstallStage.Done, 1));
        return new ImportResult(port, written, installGame, fromPack,
            backedUp ? Path.Combine(folder, InstallState.MetaFolder, ImportBackupFolder) : null);
    }

    private static (int Written, bool BackedUp) ExtractPack(PackInfo info, string folder, bool installProgram)
    {
        string root = Path.GetFullPath(folder);
        string backup = Path.Combine(root, InstallState.MetaFolder, ImportBackupFolder);
        var manifest = info.Manifest;
        var noReleaseFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listedProgramFiles = new HashSet<string>(manifest.ProgramFiles ?? [], StringComparer.OrdinalIgnoreCase);

        using var zip = ZipFile.OpenRead(info.File);

        // Work out what to write, and check all of it before touching anything.
        var work = new List<(ZipArchiveEntry Entry, string Rel)>();
        foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            if (entry.FullName.StartsWith(GamePack.GamePrefix, StringComparison.Ordinal))
            {
                // The player's part must never carry programs or ROM data, even in a pack made by hand.
                string rel = Glob.NormalizePath(entry.FullName[GamePack.GamePrefix.Length..]);
                if (!ShareRules.IsShareable(rel, noReleaseFiles))
                    throw new PackException($"{Path.GetFileName(info.File)} contains {rel}, which game packs aren't allowed to carry. Nothing was imported.");
                PortInstaller.EnsureInside(root, rel);
                work.Add((entry, rel));
            }
            else if (installProgram && entry.FullName.StartsWith(GamePack.ProgramPrefix, StringComparison.Ordinal))
            {
                string rel = Glob.NormalizePath(entry.FullName[GamePack.ProgramPrefix.Length..]);
                if (!listedProgramFiles.Contains(rel) || !ShareRules.IsShareableProgramFile(rel))
                    throw new PackException($"{Path.GetFileName(info.File)} contains {rel}, which game packs aren't allowed to carry. Nothing was imported.");
                PortInstaller.EnsureInside(root, rel);
                work.Add((entry, rel));
            }
        }

        if (installProgram)
        {
            string exe = Glob.NormalizePath(info.Port.Exe);
            if (!work.Any(w => string.Equals(w.Rel, exe, StringComparison.OrdinalIgnoreCase)))
                throw new PackException($"{Path.GetFileName(info.File)} doesn't contain {info.Port.Exe}, so {info.Port.Name} can't be installed from it.");
        }

        // A pack downloaded from the internet keeps Windows' "downloaded file" mark on the programs it installs.
        string? zone = MarkOfTheWeb.Read(info.File);

        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        var undo = new Stack<Action>();
        bool backedUp = false;
        try
        {
            foreach (var (entry, rel) in work)
            {
                string target = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (File.Exists(target))
                {
                    string saved = Path.Combine(backup, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    File.Move(target, saved);
                    undo.Push(() => File.Move(saved, target, overwrite: true));
                    backedUp = true;
                }
                else
                {
                    undo.Push(() => File.Delete(target));
                }
                entry.ExtractToFile(target);
                if (zone is not null && ShareRules.IsProgram(rel)) MarkOfTheWeb.Write(target, zone);
            }

            if (installProgram)
            {
                var programFiles = work.Where(w => listedProgramFiles.Contains(w.Rel)).Select(w => w.Rel).ToList();
                new InstallState(info.Port.Id, manifest.Tag ?? InstallState.UnknownTag, manifest.AssetName ?? "",
                                 DateTimeOffset.UtcNow, programFiles).Save(root);
            }
        }
        catch
        {
            while (undo.TryPop(out var step))
            {
                try { step(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (installProgram) File.Delete(InstallState.PathFor(root));
            throw;
        }
        return (work.Count(w => !listedProgramFiles.Contains(w.Rel) || !installProgram), backedUp);
    }

    private void TryImportArtwork(PackInfo info)
    {
        string temp = Path.Combine(Path.GetTempPath(), "hl-art-" + Guid.NewGuid().ToString("N") + Path.GetExtension(info.Manifest.Artwork));
        try
        {
            using (var zip = ZipFile.OpenRead(info.File))
                zip.GetEntry(info.Manifest.Artwork!)?.ExtractToFile(temp);
            if (File.Exists(temp)) SetArtwork(info.Port, temp);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Artwork is a nice-to-have; the import itself succeeded.
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
