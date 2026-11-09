using System.IO.Compression;
using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;

namespace HarbourLauncher.Core.Tests;

public static class SettingsTests
{
    private sealed class Sandbox : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "hl-settings-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Dir, "appdata");
        public Sandbox() => Directory.CreateDirectory(Dir);
        public void Dispose() => Directory.Delete(Dir, recursive: true);

        public PortManager Manager()
        {
            var paths = LauncherPaths.FromSettings(Root);
            var http = new HttpClient(); // Never used: these tests don't touch GitHub.
            return new PortManager(PortCatalog.LoadBuiltIn(),
                new GitHubReleaseClient(http, new ReleaseCache(paths.ReleaseCacheFile)),
                new AssetDownloader(http), new PortInstaller(), paths);
        }

        /// <summary>Installs a fake Ship of Harkinian and adds a save file.</summary>
        public void InstallSoh(PortManager manager)
        {
            string zip = Path.Combine(Dir, "soh.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var w = new StreamWriter(archive.CreateEntry("soh.exe").Open()))
                w.Write("exe");
            var soh = manager.Catalog.Get("soh");
            new PortInstaller().Install(soh, zip, "1.0", "soh.zip", manager.InstallFolder(soh));
            Directory.CreateDirectory(Path.Combine(manager.InstallFolder(soh), "Save"));
            File.WriteAllText(Path.Combine(manager.InstallFolder(soh), "Save", "file1.sav"), "100 hours");
        }
    }

    [Test]
    public static void MissingOrBrokenSettingsFileGivesDefaults()
    {
        using var s = new Sandbox();
        string file = Path.Combine(s.Dir, "settings.json");
        Assert.Equal(null, LauncherSettings.Load(file).GamesFolder, "missing file");
        File.WriteAllText(file, "{ not json");
        Assert.Equal(null, LauncherSettings.Load(file).GamesFolder, "broken file");
    }

    [Test]
    public static void ChosenFolderIsSavedAndUsedNextTime()
    {
        using var s = new Sandbox();
        string games = Path.Combine(s.Dir, "Emulators", "Harbour Masters");
        var manager = s.Manager();
        Assert.True(manager.IsDefaultGamesFolder, "starts on the default folder");

        manager.ChangeGamesFolderAsync(games, moveInstalled: true).GetAwaiter().GetResult();

        Assert.Equal(games, manager.GamesFolder, "folder in use");
        Assert.Equal(Path.Combine(games, "soh"), manager.InstallFolder(manager.Catalog.Get("soh")), "game folder");
        Assert.Equal(games, s.Manager().GamesFolder, "folder after restart");
        Assert.True(!s.Manager().IsDefaultGamesFolder, "not the default");
    }

    [Test]
    public static async Task MovingKeepsSavesAndTheInstalledVersion()
    {
        using var s = new Sandbox();
        var manager = s.Manager();
        s.InstallSoh(manager);
        var soh = manager.Catalog.Get("soh");
        string oldFolder = manager.InstallFolder(soh);
        string games = Path.Combine(s.Dir, "Games");

        var change = await manager.ChangeGamesFolderAsync(games, moveInstalled: true);

        Assert.Equal(1, change.Moved.Count, "games moved");
        Assert.Equal("1.0", manager.GetInstalled(soh)?.Tag, "still installed");
        Assert.Equal("100 hours", File.ReadAllText(Path.Combine(games, "soh", "Save", "file1.sav")), "save moved");
        Assert.True(!Directory.Exists(oldFolder), "old folder removed");
    }

    [Test]
    public static async Task NotMovingLeavesGamesWhereTheyAre()
    {
        using var s = new Sandbox();
        var manager = s.Manager();
        s.InstallSoh(manager);
        var soh = manager.Catalog.Get("soh");
        string oldFolder = manager.InstallFolder(soh);

        await manager.ChangeGamesFolderAsync(Path.Combine(s.Dir, "Games"), moveInstalled: false);

        Assert.True(File.Exists(Path.Combine(oldFolder, "Save", "file1.sav")), "old copy untouched");
        Assert.True(manager.GetInstalled(soh) is null, "new folder has nothing installed");
    }

    [Test]
    public static async Task OccupiedDestinationChangesNothing()
    {
        using var s = new Sandbox();
        var manager = s.Manager();
        s.InstallSoh(manager);
        string games = Path.Combine(s.Dir, "Games");
        Directory.CreateDirectory(Path.Combine(games, "soh"));
        File.WriteAllText(Path.Combine(games, "soh", "something.txt"), "not ours");
        string before = manager.GamesFolder;

        var e = await Assert.ThrowsAsync<GamesFolderException>(
            () => manager.ChangeGamesFolderAsync(games, moveInstalled: true));

        Assert.True(e.Message.Contains("isn't empty"), "explains why");
        Assert.Equal(before, manager.GamesFolder, "folder unchanged");
        Assert.Equal(before, s.Manager().GamesFolder, "setting not saved");
        Assert.Equal("1.0", manager.GetInstalled(manager.Catalog.Get("soh"))?.Tag, "game still installed");
    }

    [Test]
    public static async Task RelativePathIsRejected()
    {
        using var s = new Sandbox();
        await Assert.ThrowsAsync<GamesFolderException>(
            () => s.Manager().ChangeGamesFolderAsync("Games", moveInstalled: true));
    }

    [Test]
    public static async Task GoingBackToDefaultStoresNoFolder()
    {
        using var s = new Sandbox();
        var manager = s.Manager();
        await manager.ChangeGamesFolderAsync(Path.Combine(s.Dir, "Games"), moveInstalled: true);
        await manager.ChangeGamesFolderAsync(null, moveInstalled: true);

        Assert.True(manager.IsDefaultGamesFolder, "back on default");
        Assert.Equal(null, LauncherSettings.Load(Path.Combine(s.Root, "settings.json")).GamesFolder, "saved as default");
    }

    [Test]
    public static void MoveAcrossDrivesCopiesEverythingThenRemovesTheOriginal()
    {
        using var s = new Sandbox();
        string from = Path.Combine(s.Dir, "from"), to = Path.Combine(s.Dir, "other drive", "soh");
        Directory.CreateDirectory(Path.Combine(from, "Save", "deep"));
        File.WriteAllText(Path.Combine(from, "soh.exe"), "exe");
        File.WriteAllText(Path.Combine(from, "Save", "deep", "file1.sav"), "100 hours");
        Directory.CreateDirectory(Path.Combine(from, "mods")); // Empty folders come along too.

        FolderMover.Move(from, to, forceCopy: true);

        Assert.Equal("100 hours", File.ReadAllText(Path.Combine(to, "Save", "deep", "file1.sav")), "nested save");
        Assert.True(Directory.Exists(Path.Combine(to, "mods")), "empty folder");
        Assert.True(!Directory.Exists(from), "original removed");
    }

    [Test]
    public static void AccentColorIsSavedAndKeptAlongsideOtherSettings()
    {
        using var s = new Sandbox();
        var manager = s.Manager();
        Assert.Equal(null, manager.AccentColor, "starts on Marina's orange");

        string games = Path.Combine(s.Dir, "Games");
        manager.ChangeGamesFolderAsync(games, moveInstalled: false).GetAwaiter().GetResult();
        manager.SetAccentColor("5fa4ff");

        var reopened = s.Manager();
        Assert.Equal("#5FA4FF", reopened.AccentColor, "colour after restart");
        Assert.Equal(games, reopened.GamesFolder, "games folder kept");

        reopened.SetAccentColor(null);
        Assert.Equal(null, s.Manager().AccentColor, "back to the default");
        Assert.Equal(games, s.Manager().GamesFolder, "games folder still kept");
    }

    [Test]
    public static void AccentColorRejectsNonColours()
    {
        using var s = new Sandbox();
        var manager = s.Manager();
        manager.SetAccentColor("#123456");
        Assert.Throws<ArgumentException>(() => manager.SetAccentColor("orange"));
        Assert.Equal("#123456", manager.AccentColor, "bad colour changes nothing");

        // A hand-edited settings.json with junk in it falls back to the default.
        string file = Path.Combine(s.Root, "settings.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("#123456", "not a colour"));
        Assert.Equal(null, manager.AccentColor, "junk in the file");
    }

    [Test]
    public static void ColoursAreNormalized()
    {
        foreach (var (input, expected) in new[]
                 {
                     ("#eb802b", "#EB802B"), ("EB802B", "#EB802B"), (" #abc ", "#AABBCC"),
                     ("#FFEB802B", "#EB802B"),
                 })
        {
            Assert.True(LauncherSettings.TryNormalizeColor(input, out string got), $"'{input}' accepted");
            Assert.Equal(expected, got, $"'{input}'");
        }
        foreach (string? bad in new[] { null, "", "#12345", "#GGGGGG", "red", "#1234567" })
            Assert.True(!LauncherSettings.TryNormalizeColor(bad, out _), $"'{bad}' rejected");
    }
}
