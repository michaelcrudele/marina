using System.IO.Compression;
using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;

namespace HarbourLauncher.Core.Tests;

/// <summary>Everything made before the launcher was renamed Marina keeps working.</summary>
public static class RenameTests
{
    private sealed class Sandbox : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "hl-rename-" + Guid.NewGuid().ToString("N"));
        public Sandbox() => Directory.CreateDirectory(Dir);
        public void Dispose() => Directory.Delete(Dir, recursive: true);

        public PortManager Manager(string games)
        {
            var paths = new LauncherPaths(Path.Combine(Dir, "appdata"), games);
            var http = new HttpClient(); // Never used.
            return new PortManager(PortCatalog.LoadBuiltIn(),
                new GitHubReleaseClient(http, new ReleaseCache(paths.ReleaseCacheFile)),
                new AssetDownloader(http), new PortInstaller(), paths);
        }
    }

    [Test]
    public static void GamesInstalledBeforeTheRenameAreStillInstalled()
    {
        using var s = new Sandbox();
        string games = Path.Combine(s.Dir, "games"), soh = Path.Combine(games, "soh");
        Directory.CreateDirectory(Path.Combine(soh, ".harbourlauncher"));
        File.WriteAllText(Path.Combine(soh, "soh.exe"), "exe");
        File.WriteAllText(Path.Combine(soh, ".harbourlauncher", "state.json"),
            """{"PortId":"soh","Tag":"9.3.1","AssetName":"x","InstalledAt":"2026-11-01T00:00:00Z","Files":["soh.exe"]}""");
        File.WriteAllText(Path.Combine(soh, ".harbourlauncher", "played.json"), """{"LastPlayed":"2026-11-05T20:15:00+00:00"}""");

        var manager = s.Manager(games);
        var port = manager.Catalog.Get("soh");

        Assert.Equal("9.3.1", manager.GetInstalled(port)?.Tag, "still installed");
        Assert.True(Directory.Exists(Path.Combine(soh, ".marina")) && !Directory.Exists(Path.Combine(soh, ".harbourlauncher")), "folder renamed");
        Assert.True(manager.GetLastPlayed(port) is not null, "last played kept");
        Assert.Equal("", string.Join(",", manager.FindExistingGames(includeDismissed: true).Select(f => f.Port.Id)), "not offered as a found game");
    }

    [Test]
    public static void LauncherDataMovesToTheNewFolderOnce()
    {
        using var s = new Sandbox();
        string legacy = Path.Combine(s.Dir, "HarbourLauncher"), root = Path.Combine(s.Dir, "Marina");
        Directory.CreateDirectory(Path.Combine(legacy, "artwork"));
        File.WriteAllText(Path.Combine(legacy, "settings.json"), """{"GamesFolder":"Z:\\Emulators\\Harbour Masters"}""");

        Assert.Equal(root, LauncherPaths.MigrateRoot(legacy, root), "uses the new folder");
        Assert.True(File.Exists(Path.Combine(root, "settings.json")) && !Directory.Exists(legacy), "data moved");

        Directory.CreateDirectory(legacy); // Something recreated it later: leave both alone.
        Assert.Equal(root, LauncherPaths.MigrateRoot(legacy, root), "still the new folder");
        Assert.True(Directory.Exists(legacy), "not touched again");
    }

    [Test]
    public static void BackupsAndPacksMadeBeforeTheRenameStillOpen()
    {
        using var s = new Sandbox();
        string games = Path.Combine(s.Dir, "games"), soh = Path.Combine(games, "soh");
        Directory.CreateDirectory(soh);
        File.WriteAllText(Path.Combine(soh, "soh.exe"), "exe");
        new InstallState("soh", "9.3.1", "x.zip", DateTimeOffset.UtcNow, ["soh.exe"]).Save(soh);
        var manager = s.Manager(games);
        var port = manager.Catalog.Get("soh");

        // Same formats, old names.
        string backup = manager.BackUpGame(port, Path.Combine(s.Dir, "old")).File;
        string pack = manager.ExportGame(port, Path.Combine(s.Dir, "old")).File;
        Assert.True(backup.EndsWith(".marinabackup", StringComparison.Ordinal) && pack.EndsWith(".marinapack", StringComparison.Ordinal), "new names");
        File.Move(backup, Path.Combine(s.Dir, "old.hlbackup"));
        File.Move(pack, Path.Combine(s.Dir, "old.hlpack"));

        Assert.Equal("soh", manager.ReadBackup(Path.Combine(s.Dir, "old.hlbackup")).Port.Id, "old backup");
        Assert.Equal("soh", manager.ReadPack(Path.Combine(s.Dir, "old.hlpack")).Port.Id, "old pack");
    }

    [Test]
    public static void OldBackupRestoresWithItsRecordRenamed()
    {
        using var s = new Sandbox();
        string file = Path.Combine(s.Dir, "pre-rename.hlbackup");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("backup.json").Open()))
                w.Write("""{"Format":1,"PortId":"soh","PortName":"Ship of Harkinian","Tag":"9.3.1","CreatedAt":"2026-11-05T00:00:00Z","Machine":"laptop","Bytes":10,"Files":["soh.exe",".harbourlauncher/state.json"]}""");
            using (var w = new StreamWriter(zip.CreateEntry("files/soh.exe").Open())) w.Write("exe");
            using (var w = new StreamWriter(zip.CreateEntry("files/.harbourlauncher/state.json").Open()))
                w.Write("""{"PortId":"soh","Tag":"9.3.1","AssetName":"x","InstalledAt":"2026-11-01T00:00:00Z","Files":["soh.exe"]}""");
        }
        var manager = s.Manager(Path.Combine(s.Dir, "games"));

        manager.RestoreBackupAsync(file).GetAwaiter().GetResult();

        var soh = manager.Catalog.Get("soh");
        Assert.Equal("9.3.1", manager.GetInstalled(soh)?.Tag, "restored and installed");
        Assert.True(Directory.Exists(Path.Combine(manager.InstallFolder(soh), ".marina")), "record renamed");
    }
}
