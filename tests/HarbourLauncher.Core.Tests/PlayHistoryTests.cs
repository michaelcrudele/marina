using System.IO.Compression;
using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;

namespace HarbourLauncher.Core.Tests;

public static class PlayHistoryTests
{
    private sealed class Pc : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "hl-played-" + Guid.NewGuid().ToString("N"));
        public PortManager Manager { get; }
        public PortDefinition Soh => Manager.Catalog.Get("soh");
        public string Game => Manager.InstallFolder(Soh);

        public Pc()
        {
            Directory.CreateDirectory(Dir);
            var paths = new LauncherPaths(Path.Combine(Dir, "appdata"), Path.Combine(Dir, "games"));
            var http = new HttpClient(); // Never used.
            Manager = new PortManager(PortCatalog.LoadBuiltIn(),
                new GitHubReleaseClient(http, new ReleaseCache(paths.ReleaseCacheFile)),
                new AssetDownloader(http), new PortInstaller(), paths);
            Directory.CreateDirectory(Game);
            File.WriteAllText(Path.Combine(Game, "soh.exe"), "exe");
            new InstallState("soh", "9.3.1", "x.zip", DateTimeOffset.UtcNow, ["soh.exe"]).Save(Game);
        }

        public void Log(DateTime whenUtc)
        {
            string log = Path.Combine(Game, "logs", "soh.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllText(log, "log");
            File.SetLastWriteTimeUtc(log, whenUtc);
        }

        public void Dispose() => Directory.Delete(Dir, recursive: true);
    }

    private static readonly DateTimeOffset Played = new(2026, 11, 5, 20, 15, 0, TimeSpan.Zero);

    [Test]
    public static void NeverPlayedHasNoDate()
    {
        using var pc = new Pc();
        Assert.Equal(null, pc.Manager.GetLastPlayed(pc.Soh), "last played");
    }

    [Test]
    public static void RecordedLaunchIsTheLastPlayedTime()
    {
        using var pc = new Pc();
        pc.Manager.RecordPlayed(pc.Soh, Played);
        Assert.Equal(Played, pc.Manager.GetLastPlayed(pc.Soh), "last played");
    }

    [Test]
    public static void PlayingOutsideTheLauncherIsNoticedFromTheLog()
    {
        using var pc = new Pc();
        pc.Manager.RecordPlayed(pc.Soh, Played);
        var later = Played.UtcDateTime.AddDays(1);
        pc.Log(later);
        Assert.Equal(new DateTimeOffset(later), pc.Manager.GetLastPlayed(pc.Soh)?.ToUniversalTime(), "newer log wins");

        pc.Log(Played.UtcDateTime.AddDays(-3));
        Assert.Equal(Played, pc.Manager.GetLastPlayed(pc.Soh), "older log doesn't");
    }

    [Test]
    public static void BackupsKeepItAndSharedPacksDont()
    {
        using var pc = new Pc();
        pc.Manager.RecordPlayed(pc.Soh, Played);
        File.WriteAllText(Path.Combine(pc.Game, "shipofharkinian.json"), "settings");

        using (var backup = ZipFile.OpenRead(pc.Manager.BackUpGame(pc.Soh, Path.Combine(pc.Dir, "b")).File))
            Assert.True(backup.GetEntry("files/.marina/played.json") is not null, "in backup");
        using (var pack = ZipFile.OpenRead(pc.Manager.ExportGame(pc.Soh, Path.Combine(pc.Dir, "p")).File))
            Assert.True(!pack.Entries.Any(e => e.FullName.EndsWith("played.json", StringComparison.Ordinal)), "not in pack");
    }
}
