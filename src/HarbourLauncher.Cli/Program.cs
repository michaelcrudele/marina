using HarbourLauncher.Core;
using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;
using HarbourLauncher.Core.Sharing;
using HarbourLauncher.Core.Backup;

// A thin command-line front end over PortManager. The Avalonia app will call
// the same PortManager methods; this exists to exercise them on a real machine.

var argList = args.ToList();
string? root = TakeOption(argList, "--root");
string? zip = TakeOption(argList, "--zip");
string? tag = TakeOption(argList, "--tag");
bool noMove = argList.Remove("--no-move");
bool add = argList.Remove("--add");
bool listOnly = argList.Remove("--list");
bool noGame = argList.Remove("--no-game");
bool noMods = argList.Remove("--no-mods");
bool yes = argList.Remove("--yes");
string? backupFirst = TakeOption(argList, "--backup");

var paths = root is null ? LauncherPaths.Default() : LauncherPaths.FromSettings(root);
var http = new HttpClient();
var catalog = PortCatalog.LoadBuiltIn();
var manager = new PortManager(
    catalog,
    new GitHubReleaseClient(http, new ReleaseCache(paths.ReleaseCacheFile), Environment.GetEnvironmentVariable("GITHUB_TOKEN")),
    new AssetDownloader(http),
    new PortInstaller(),
    paths);

try
{
    switch (argList.FirstOrDefault())
    {
        case "list":
            foreach (var port in catalog.Ports)
            {
                var installed = manager.GetInstalled(port);
                Console.WriteLine($"{port.Id,-14} {port.Name,-20} {(installed is null ? "not installed" : installed.Tag)}");
            }
            return 0;

        case "check":
            foreach (var port in Selected(argList))
            {
                var status = await manager.CheckAsync(port, force: true);
                string state = !status.IsInstalled ? "not installed"
                    : status.UpdateAvailable ? $"{status.Installed!.Tag} -> update available"
                    : $"{status.Installed!.Tag}, up to date";
                string stale = status.Latest.FromStaleCache ? $" ({status.Latest.StaleReason}; showing last known release)" : "";
                Console.WriteLine($"{port.Id,-14} latest {status.Latest.Tag,-8} {state}{stale}");
            }
            return 0;

        case "install":
        {
            var port = catalog.Get(Required(argList, 1, "port id"));
            InstallResult? result;
            if (zip is not null)
            {
                result = new PortInstaller().Install(port, zip, tag ?? "local", Path.GetFileName(zip), manager.InstallFolder(port));
            }
            else
            {
                result = await manager.InstallLatestAsync(port, new Progress<InstallProgress>(p =>
                {
                    if (p.Stage == InstallStage.Downloading) Console.Write($"\rDownloading {p.Fraction:P0}   ");
                    else if (p.Stage == InstallStage.Installing) Console.WriteLine("\rInstalling...        ");
                }));
            }

            if (result is null)
            {
                Console.WriteLine($"{port.Name} is already up to date.");
                return 0;
            }
            Console.WriteLine($"Installed {port.Name} {result.Tag} to {manager.InstallFolder(port)}");
            Console.WriteLine($"  {result.Written.Count} files written, {result.Kept.Count} kept, {result.Removed.Count} removed.");
            if (Directory.EnumerateFiles(result.BackupFolder, "*", SearchOption.AllDirectories).Any())
                Console.WriteLine($"  Replaced and removed files were backed up to {result.BackupFolder}");
            return 0;
        }

        case "folder":
        {
            if (argList.Count < 2)
            {
                Console.WriteLine($"{manager.GamesFolder}{(manager.IsDefaultGamesFolder ? " (default)" : "")}");
                return 0;
            }
            string? folder = argList[1].Equals("default", StringComparison.OrdinalIgnoreCase)
                ? null : Path.GetFullPath(argList[1]);
            var change = await manager.ChangeGamesFolderAsync(folder, moveInstalled: !noMove);
            Console.WriteLine($"Games folder is now {change.Folder}");
            foreach (var port in change.Moved)
                Console.WriteLine($"  Moved {port.Name}");
            return 0;
        }

        case "scan":
        {
            var found = manager.FindExistingGames(includeDismissed: true);
            if (found.Count == 0)
            {
                Console.WriteLine($"No games to add in {manager.GamesFolder}.");
                return 0;
            }
            foreach (var game in found)
            {
                string rename = game.NeedsRename ? $" (folder will be renamed to {Path.GetFileName(game.TargetFolder)})" : "";
                if (add)
                {
                    manager.AddExistingGame(game);
                    Console.WriteLine($"Added {game.Port.Name}{rename}");
                }
                else
                {
                    Console.WriteLine($"Found {game.Port.Name} in {game.Folder}{rename}");
                }
            }
            if (!add) Console.WriteLine("Run 'marina scan --add' to add them to the library.");
            return 0;
        }

        case "artwork":
        {
            var port = catalog.Get(Required(argList, 1, "port id"));
            if (argList.Count < 3)
            {
                Console.WriteLine(manager.GetArtwork(port) ?? $"{port.Name} uses the default banner.");
                return 0;
            }
            if (argList[2].Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                manager.ClearArtwork(port);
                Console.WriteLine($"{port.Name} is back to the default banner.");
            }
            else
            {
                manager.SetArtwork(port, argList[2]);
                Console.WriteLine($"{port.Name} now uses {Path.GetFileName(argList[2])}.");
            }
            return 0;
        }

        case "export":
        {
            var port = catalog.Get(Required(argList, 1, "port id"));
            var contents = manager.GetShareContents(port);
            if (listOnly)
            {
                foreach (var item in contents.Items)
                    Console.WriteLine($"  {Size(item.Bytes),10}  {item.Label}  ({item.Detail})");
                foreach (var x in contents.NeverIncluded.Where(x => x.Bytes > 0))
                    Console.WriteLine($"  {Size(x.Bytes),10}  never included: {x.File} ({x.Reason})");
                if (contents.NoGameReason is { } why) Console.WriteLine(why);
                return 0;
            }
            var ids = contents.Items
                .Where(i => !(noGame && i.Kind == ShareItemKind.Game) && !(noMods && i.Kind == ShareItemKind.Mod))
                .Select(i => i.Id).ToList();
            string file = argList.Count > 2 ? argList[2] : port.Id + ShareRules.Extension;
            var result = manager.ExportGame(port, Path.GetFullPath(file), ids, new Progress<double>(f => Console.Write($"\rPacking {f:P0}   ")));
            Console.Write("\r");
            Console.WriteLine($"Saved {result.File} ({result.Bytes / (1024.0 * 1024):N1} MB): " +
                              $"{result.ProgramFileCount} game files, {result.FileCount} saves/settings/mod files.");
            if (result.ProgramFileCount == 0)
                Console.WriteLine($"  {port.Name} itself isn't included: update it once with the launcher, then export again.");
            Console.WriteLine("No ROM data is included: whoever imports it needs their own ROM.");
            return 0;
        }

        case "import":
        {
            var info = manager.ReadPack(Path.GetFullPath(Required(argList, 1, ".marinapack file")));
            Console.WriteLine($"{info.Port.Name}{(info.Manifest.Tag is { } t ? " " + t : "")}: " +
                              (info.Manifest.HasProgram ? "game included, " : "game not included, ") +
                              $"{info.Manifest.Files.Count} saves/settings/mod files, exported {info.Manifest.ExportedAt.ToLocalTime():d}");
            var result = await manager.ImportPackAsync(info.File, new Progress<InstallProgress>(p =>
            {
                if (p.Stage == InstallStage.Downloading) Console.Write($"\rDownloading {info.Port.Name} {p.Fraction:P0}   ");
            }));
            string how = result.InstalledFromPack ? "Installed from the pack; " : result.InstalledGame ? "Downloaded the game; " : "";
            Console.WriteLine($"\r{how}imported {result.FileCount} files into {manager.InstallFolder(info.Port)}.        ");
            if (result.BackupFolder is not null)
                Console.WriteLine($"  Files it replaced were backed up to {result.BackupFolder}");
            return 0;
        }

        case "backup":
        {
            var port = catalog.Get(Required(argList, 1, "port id"));
            var contents = manager.GetBackupContents(port);
            foreach (var part in contents.Parts)
                Console.WriteLine($"  {Size(part.Bytes),10}  {part.Label}");
            string file = argList.Count > 2 ? argList[2] : $"{port.Name} backup {DateTime.Now:yyyy-MM-dd}.marinabackup";
            var result = manager.BackUpGame(port, Path.GetFullPath(file), new Progress<double>(f => Console.Write($"\rBacking up {f:P0}   ")));
            Console.WriteLine($"\rBacked up {port.Name} to {result.File} ({Size(result.Bytes)}).");
            return 0;
        }

        case "restore":
        {
            var info = manager.ReadBackup(Path.GetFullPath(Required(argList, 1, ".marinabackup file")));
            Console.WriteLine($"{info.Port.Name}{(info.Manifest.Tag is { } t ? " " + t : "")}, backed up on {info.Manifest.Machine} " +
                              $"{info.Manifest.CreatedAt.ToLocalTime():g} ({Size(info.Manifest.Bytes)})");
            var result = await manager.RestoreBackupAsync(info.File, new Progress<double>(f => Console.Write($"\rRestoring {f:P0}   ")));
            Console.WriteLine($"\rRestored {result.FileCount} files to {result.Folder}.        ");
            if (result.PreviousFolder is not null)
                Console.WriteLine($"  The copy that was already here is in {result.PreviousFolder}");
            return 0;
        }

        case "uninstall":
        {
            var port = catalog.Get(Required(argList, 1, "port id"));
            long size = manager.GetGameFolderSize(port);
            if (!yes)
            {
                Console.WriteLine($"This deletes {manager.InstallFolder(port)} ({Size(size)}): the game, saves, settings, mods and ROM data.");
                Console.WriteLine("Run again with --yes to uninstall, and --backup <file> to save a .marinabackup first.");
                return 1;
            }
            if (backupFirst is not null)
            {
                var saved = manager.BackUpGame(port, Path.GetFullPath(backupFirst), new Progress<double>(f => Console.Write($"\rBacking up {f:P0}   ")));
                Console.WriteLine($"\rBacked up to {saved.File} ({Size(saved.Bytes)}).");
            }
            var result = await manager.UninstallGameAsync(port);
            Console.WriteLine($"Uninstalled {port.Name}. Freed {Size(result.FreedBytes)}.");
            return 0;
        }

        case "launch":
            manager.Launch(catalog.Get(Required(argList, 1, "port id")));
            return 0;

        default:
            Console.WriteLine("""
                Usage: marina <command> [options]

                  list                 Show each port and its installed version
                  check [port]         Compare installed versions with the latest releases
                  install <port>       Install the latest release, or update to it
                  launch <port>        Start an installed port
                  folder               Show where games are installed
                  folder <path>        Install games in <path> from now on, moving installed ones there
                  folder default       Go back to the default games folder
                  scan [--add]         Find games in the games folder that you installed by hand
                  export <port> [file] Save a game with its saves, settings and mods as a .marinapack to share
                                       (--list shows what can go in, with sizes; --no-game, --no-mods leave parts out)
                  import <file>        Import a .marinapack (installs the game from it if needed)
                  backup <port> [file] Back up a whole game (with its ROM data, saves and mods) to a .marinabackup
                  restore <file>       Restore a .marinabackup made on this or another of your PCs
                  uninstall <port> --yes [--backup <file>]
                                       Delete a game and everything in its folder (optionally backing it up first)
                  artwork <port> [file|none]
                                       Show, set or remove a game's custom icon or box art

                Options:
                  --root <folder>      Launcher data folder (default: %LOCALAPPDATA%\Marina)
                  --zip <file>         With install: use a release zip already on disk
                  --tag <version>      With --zip: the version to record
                  --no-move            With folder: leave installed games where they are

                Set GITHUB_TOKEN to raise GitHub's limit of 60 checks per hour.
                """);
            return argList.Count == 0 ? 0 : 1;
    }
}
catch (Exception e) when (e is KeyNotFoundException or HttpRequestException or InvalidDataException
                              or AssetNotFoundException or GitHubRateLimitException or GameRunningException
                              or FileNotFoundException or ArgumentException or GamesFolderException
                              or PackException or InvalidOperationException or BackupException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static string Size(long bytes) => bytes switch
{
    >= 1L << 30 => $"{bytes / (double)(1L << 30):N2} GB",
    >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
    _ => $"{Math.Max(1, (bytes + 1023) / 1024)} KB",
};

IEnumerable<PortDefinition> Selected(List<string> a) =>
    a.Count > 1 ? [catalog.Get(a[1])] : catalog.Ports;

static string Required(List<string> a, int index, string what) =>
    a.Count > index ? a[index] : throw new ArgumentException($"Missing {what}.");

static string? TakeOption(List<string> a, string name)
{
    int i = a.IndexOf(name);
    if (i < 0) return null;
    if (i + 1 >= a.Count) throw new ArgumentException($"{name} needs a value.");
    string value = a[i + 1];
    a.RemoveRange(i, 2);
    return value;
}
