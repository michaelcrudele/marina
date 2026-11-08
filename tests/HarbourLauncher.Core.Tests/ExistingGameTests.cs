using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;

namespace HarbourLauncher.Core.Tests;

/// <summary>Finding and adding games the player installed by hand.</summary>
public static class ExistingGameTests
{
    private sealed class Sandbox : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "hl-found-" + Guid.NewGuid().ToString("N"));
        public string Games => Path.Combine(Dir, "Harbour Masters");
        public string Root => Path.Combine(Dir, "appdata");
        public PortManager Manager { get; }

        public Sandbox()
        {
            Directory.CreateDirectory(Games);
            new LauncherSettings { GamesFolder = Games }.Save(Path.Combine(Root, "settings.json"));
            var paths = LauncherPaths.FromSettings(Root);
            var http = new HttpClient(); // Never used.
            Manager = new PortManager(PortCatalog.LoadBuiltIn(),
                new GitHubReleaseClient(http, new ReleaseCache(paths.ReleaseCacheFile)),
                new AssetDownloader(http), new PortInstaller(), paths);
        }

        /// <summary>Creates a folder with these files, like a manual install.</summary>
        public string Folder(string name, params string[] files)
        {
            string dir = Path.Combine(Games, name);
            Directory.CreateDirectory(dir);
            foreach (string f in files)
            {
                string path = Path.Combine(dir, f);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, f);
            }
            return dir;
        }

        public void Dispose() => Directory.Delete(Dir, recursive: true);
    }

    private static string Ids(IEnumerable<FoundGame> found) => string.Join(",", found.Select(f => f.Port.Id));

    [Test]
    public static void FindsGamesInCorrectlyNamedFoldersRegardlessOfExeCase()
    {
        // Mirrors a real folder: Lighthouse.exe with a capital L, plus saves and mods.
        using var s = new Sandbox();
        s.Folder("lighthouse", "Lighthouse.exe", "bk.o2r", "saves/save1.bin", "mods/x.o2r");
        s.Folder("paperboat", "Paperboat.exe", "pm64.o2r");

        var found = s.Manager.FindExistingGames();

        Assert.Equal("lighthouse,paperboat", Ids(found), "found");
        Assert.True(found.All(f => !f.NeedsRename), "no rename needed");
    }

    [Test]
    public static void FindsGamesInOtherFolderNamesAndOffersToRename()
    {
        using var s = new Sandbox();
        s.Folder("Ship of Harkinian", "soh.exe");

        var game = s.Manager.FindExistingGames().Single();

        Assert.Equal("soh", game.Port.Id, "port");
        Assert.True(game.NeedsRename, "needs rename");
        Assert.Equal(Path.Combine(s.Games, "soh"), game.TargetFolder, "target");
    }

    [Test]
    public static void IgnoresFoldersWithoutAGameAndGamesAlreadyInTheLibrary()
    {
        using var s = new Sandbox();
        s.Folder("random stuff", "notes.txt");
        s.Folder("soh", "soh.exe");
        new InstallState("soh", "1.0", "a.zip", DateTimeOffset.UtcNow, ["soh.exe"]).Save(Path.Combine(s.Games, "soh"));

        Assert.Equal("", Ids(s.Manager.FindExistingGames()), "nothing to offer");
    }

    [Test]
    public static void AddingKeepsEverythingAndShowsAsInstalledWithUnknownVersion()
    {
        using var s = new Sandbox();
        string dir = s.Folder("lighthouse", "Lighthouse.exe", "saves/save1.bin");
        var port = s.Manager.Catalog.Get("lighthouse");

        s.Manager.AddExistingGame(s.Manager.FindExistingGames().Single());

        var state = s.Manager.GetInstalled(port);
        Assert.True(state is not null, "installed");
        Assert.True(!state!.IsVersionKnown, "version unknown");
        Assert.Equal("saves/save1.bin", File.ReadAllText(Path.Combine(dir, "saves", "save1.bin")), "save untouched");
        Assert.Equal("", Ids(s.Manager.FindExistingGames()), "not offered again");
    }

    [Test]
    public static void AddingRenamesTheFolderToTheGameId()
    {
        using var s = new Sandbox();
        s.Folder("Ship of Harkinian", "soh.exe", "Save/file1.sav");

        s.Manager.AddExistingGame(s.Manager.FindExistingGames().Single());

        Assert.True(!Directory.Exists(Path.Combine(s.Games, "Ship of Harkinian")), "old name gone");
        Assert.True(File.Exists(Path.Combine(s.Games, "soh", "Save", "file1.sav")), "save in renamed folder");
        Assert.True(s.Manager.GetInstalled(s.Manager.Catalog.Get("soh")) is not null, "installed");
    }

    [Test]
    public static void DoesNotOfferARenameOntoAnOccupiedFolder()
    {
        using var s = new Sandbox();
        s.Folder("Ship of Harkinian", "soh.exe");
        s.Folder("soh", "something-else.txt");

        Assert.Equal("", Ids(s.Manager.FindExistingGames()), "not offered");
    }

    [Test]
    public static void DismissedGamesAreOnlyOfferedWhenAskedToLook()
    {
        using var s = new Sandbox();
        s.Folder("paperboat", "Paperboat.exe");

        s.Manager.DismissFoundGames(s.Manager.FindExistingGames());

        Assert.Equal("", Ids(s.Manager.FindExistingGames()), "not offered on start");
        Assert.Equal("paperboat", Ids(s.Manager.FindExistingGames(includeDismissed: true)), "offered when looking");
        Assert.Equal(s.Games, LauncherPaths.FromSettings(s.Root).Ports, "games folder setting kept");
    }

    [Test]
    public static void ChangingFolderKeepsDismissedList()
    {
        using var s = new Sandbox();
        s.Folder("paperboat", "Paperboat.exe");
        s.Manager.DismissFoundGames(s.Manager.FindExistingGames());

        s.Manager.ChangeGamesFolderAsync(s.Games + Path.DirectorySeparatorChar, moveInstalled: false).GetAwaiter().GetResult();

        Assert.Equal("", Ids(s.Manager.FindExistingGames()), "still dismissed");
    }
}
