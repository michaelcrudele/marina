using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;

namespace HarbourLauncher.Core.Tests;

public static class UninstallTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    private sealed class Pc : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "hl-uninstall-" + Guid.NewGuid().ToString("N"));
        public PortManager Manager { get; }
        public PortDefinition PaperBoat => Manager.Catalog.Get("paperboat");
        public string Games => Path.Combine(Dir, "games");
        public string Game => Manager.InstallFolder(PaperBoat);

        public Pc()
        {
            Directory.CreateDirectory(Dir);
            var paths = new LauncherPaths(Path.Combine(Dir, "appdata"), Games);
            var http = new HttpClient(); // Never used.
            Manager = new PortManager(PortCatalog.LoadBuiltIn(),
                new GitHubReleaseClient(http, new ReleaseCache(paths.ReleaseCacheFile)),
                new AssetDownloader(http), new PortInstaller(), paths);
        }

        public void InstallPaperBoat()
        {
            Write("Paperboat.exe", 1000);
            Write("pm64.o2r", 4000);
            Write("saves/file0.json", 100);
            Write("mods/hd.o2r", 5000);
            new InstallState("paperboat", "1.0.3", "x.zip", DateTimeOffset.UtcNow, ["Paperboat.exe"]).Save(Game);
        }

        public void Write(string rel, int bytes)
        {
            string path = Path.Combine(Game, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
        }

        public void Dispose()
        {
            foreach (var f in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Dir, recursive: true);
        }
    }

    [Test]
    public static void UninstallRemovesTheWholeFolderAndReportsTheSpaceFreed()
    {
        using var pc = new Pc();
        pc.InstallPaperBoat();
        long size = pc.Manager.GetGameFolderSize(pc.PaperBoat);

        var result = pc.Manager.UninstallGameAsync(pc.PaperBoat).GetAwaiter().GetResult();

        Assert.Equal(size, result.FreedBytes, "space freed");
        Assert.True(size >= 10_100, "counts everything, including saves and mods");
        Assert.True(!Directory.Exists(pc.Game), "folder gone");
        Assert.True(pc.Manager.GetInstalled(pc.PaperBoat) is null, "no longer installed");
        Assert.Equal(0, Directory.GetDirectories(pc.Games).Length, "nothing left behind");
    }

    [Test]
    public static void ReadOnlyFilesDontBlockUninstall()
    {
        using var pc = new Pc();
        pc.InstallPaperBoat();
        File.SetAttributes(Path.Combine(pc.Game, "saves", "file0.json"), FileAttributes.ReadOnly);

        pc.Manager.UninstallGameAsync(pc.PaperBoat).GetAwaiter().GetResult();

        Assert.True(!Directory.Exists(pc.Game), "folder gone");
    }

    [Test]
    public static void ArtworkIsKeptForAReinstall()
    {
        using var pc = new Pc();
        pc.InstallPaperBoat();
        string art = Path.Combine(pc.Dir, "art.png");
        File.WriteAllBytes(art, Png);
        pc.Manager.SetArtwork(pc.PaperBoat, art);

        pc.Manager.UninstallGameAsync(pc.PaperBoat).GetAwaiter().GetResult();

        Assert.True(pc.Manager.GetArtwork(pc.PaperBoat) is not null, "artwork kept");
    }

    [Test]
    public static void BackUpUninstallRestoreBringsEverythingBack()
    {
        using var pc = new Pc();
        pc.InstallPaperBoat();
        string backup = pc.Manager.BackUpGame(pc.PaperBoat, Path.Combine(pc.Dir, "usb", "pb")).File;

        pc.Manager.UninstallGameAsync(pc.PaperBoat).GetAwaiter().GetResult();
        pc.Manager.RestoreBackupAsync(backup).GetAwaiter().GetResult();

        Assert.Equal("1.0.3", pc.Manager.GetInstalled(pc.PaperBoat)?.Tag, "installed again");
        Assert.True(File.Exists(Path.Combine(pc.Game, "pm64.o2r")) && File.Exists(Path.Combine(pc.Game, "saves", "file0.json")), "data back");
    }

    [Test]
    public static void UninstallingAGameThatIsntThereIsAnError()
    {
        using var pc = new Pc();
        Directory.CreateDirectory(pc.Games);
        Assert.ThrowsAsync<InvalidOperationException>(() => pc.Manager.UninstallGameAsync(pc.PaperBoat)).GetAwaiter().GetResult();
    }

    [Test]
    public static void ManyFilesAreDeletedWithProgress()
    {
        using var pc = new Pc();
        pc.InstallPaperBoat();
        for (int i = 0; i < 500; i++) pc.Write($"assets/yaml/us/f{i % 7}/file{i}.yml", 10);
        var reports = new System.Collections.Concurrent.ConcurrentBag<double>();

        pc.Manager.UninstallGameAsync(pc.PaperBoat, new SyncProgress(reports.Add)).GetAwaiter().GetResult();

        Assert.True(!Directory.Exists(pc.Game), "folder gone");
        Assert.True(reports.Count >= 10, "progress reported along the way");
        Assert.Equal(1.0, reports.Max(), "progress reaches 100%");
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
