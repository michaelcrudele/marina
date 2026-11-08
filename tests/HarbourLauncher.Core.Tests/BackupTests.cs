using System.IO.Compression;
using HarbourLauncher.Core.Backup;
using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;
using HarbourLauncher.Core.Sharing;

namespace HarbourLauncher.Core.Tests;

/// <summary>Backing up a whole game to a USB drive and restoring it on another PC.</summary>
public static class BackupTests
{
    private static readonly string[] Release = ["Paperboat.exe", "shaderc_shared.dll", "paperboat.o2r", "debug/Paperboat.pdb"];

    private sealed class Pc : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "hl-backup-" + Guid.NewGuid().ToString("N"));
        public PortManager Manager { get; }
        public PortDefinition PaperBoat => Manager.Catalog.Get("paperboat");
        public string Game => Manager.InstallFolder(PaperBoat);

        public Pc()
        {
            Directory.CreateDirectory(Dir);
            var paths = new LauncherPaths(Path.Combine(Dir, "appdata"), Path.Combine(Dir, "games"));
            var http = new HttpClient(); // Never used.
            Manager = new PortManager(PortCatalog.LoadBuiltIn(),
                new GitHubReleaseClient(http, new ReleaseCache(paths.ReleaseCacheFile)),
                new AssetDownloader(http), new PortInstaller(), paths);
        }

        /// <summary>A PaperBoat folder shaped like a real one.</summary>
        public void InstallPaperBoat(string save = "my save")
        {
            foreach (string f in Release) Write(f, "release " + f);
            Write("pm64.o2r", "made from my ROM");
            Write("torch.hash.yml", "rom hash");
            Write("saves/file0.json", save);
            Write("paperboat.cfg.json", "my settings");
            Write("mods/paperboat-hd.o2r", "texture pack");
            Write("logs/Paperboat.log", "log");
            Write(".marina/backup/Paperboat.exe", "previous version");
            new InstallState("paperboat", "1.0.3", "x.zip", DateTimeOffset.UtcNow, Release).Save(Game);
        }

        public void Write(string rel, string text)
        {
            string path = Path.Combine(Game, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public string Read(string rel) => File.ReadAllText(Path.Combine(Game, rel));
        public bool Has(string rel) => File.Exists(Path.Combine(Game, rel));
        public void Dispose() => Directory.Delete(Dir, recursive: true);
    }

    [Test]
    public static void BackupHoldsTheWholeGameIncludingRomData()
    {
        using var pc = new Pc();
        pc.InstallPaperBoat();

        var contents = pc.Manager.GetBackupContents(pc.PaperBoat);
        Assert.Equal("The game,Game data made from your ROM,Saves and settings,Mods",
            string.Join(",", contents.Parts.Select(p => p.Label)), "parts");

        var result = pc.Manager.BackUpGame(pc.PaperBoat, Path.Combine(pc.Dir, "usb", "PaperBoat backup"));
        Assert.True(result.File.EndsWith(".marinabackup", StringComparison.Ordinal), "extension added");

        using var zip = ZipFile.OpenRead(result.File);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        foreach (string f in new[] { "Paperboat.exe", "pm64.o2r", "torch.hash.yml", "saves/file0.json", "paperboat.cfg.json",
                                     "mods/paperboat-hd.o2r", ".marina/state.json" })
            Assert.True(names.Contains("files/" + f), $"has {f}");
        foreach (string f in new[] { "logs/Paperboat.log", "debug/Paperboat.pdb", ".marina/backup/Paperboat.exe" })
            Assert.True(!names.Contains("files/" + f), $"skips {f}");
    }

    [Test]
    public static void RestoreOnAnotherPcIsReadyToPlay()
    {
        using var laptop = new Pc();
        laptop.InstallPaperBoat("100 hours");
        string backup = laptop.Manager.BackUpGame(laptop.PaperBoat, Path.Combine(laptop.Dir, "usb", "pb")).File;

        using var desktop = new Pc();
        var info = desktop.Manager.ReadBackup(backup);
        Assert.Equal("1.0.3", info.Manifest.Tag, "version");
        Assert.Equal(Environment.MachineName, info.Manifest.Machine, "made on");

        var result = desktop.Manager.RestoreBackupAsync(backup).GetAwaiter().GetResult();

        Assert.True(result.PreviousFolder is null, "nothing was there before");
        Assert.Equal("1.0.3", desktop.Manager.GetInstalled(desktop.PaperBoat)?.Tag, "installed, same version");
        Assert.Equal("made from my ROM", desktop.Read("pm64.o2r"), "ROM data restored");
        Assert.Equal("100 hours", desktop.Read("saves/file0.json"), "save restored");
        Assert.Equal("texture pack", desktop.Read("mods/paperboat-hd.o2r"), "mods restored");
        Assert.True(!Directory.Exists(Path.Combine(desktop.Dir, "games", ".paperboat.restoring")), "staging cleaned up");
    }

    [Test]
    public static void RestoringOverAnExistingGameKeepsTheOldCopy()
    {
        using var pc = new Pc();
        pc.InstallPaperBoat("old save");
        string backup = pc.Manager.BackUpGame(pc.PaperBoat, Path.Combine(pc.Dir, "pb")).File;
        pc.Write("saves/file0.json", "newer save on this PC");

        var result = pc.Manager.RestoreBackupAsync(backup).GetAwaiter().GetResult();

        Assert.Equal("old save", pc.Read("saves/file0.json"), "backup restored");
        Assert.True(result.PreviousFolder is not null, "old copy kept");
        Assert.Equal("newer save on this PC", File.ReadAllText(Path.Combine(result.PreviousFolder!, "saves", "file0.json")), "old copy intact");
        Assert.True(Path.GetFileName(result.PreviousFolder!).StartsWith("paperboat (before restore", StringComparison.Ordinal), "named clearly");
        Assert.Equal("", string.Join(",", pc.Manager.FindExistingGames(includeDismissed: true).Select(f => f.Port.Id)),
            "old copy isn't offered as a found game");
    }

    [Test]
    public static void StoppedRestoreLeavesTheExistingGameAlone()
    {
        using var pc = new Pc();
        pc.InstallPaperBoat("old save");
        string backup = pc.Manager.BackUpGame(pc.PaperBoat, Path.Combine(pc.Dir, "pb")).File;
        pc.Write("saves/file0.json", "current save");

        Assert.ThrowsAsync<OperationCanceledException>(() =>
            pc.Manager.RestoreBackupAsync(backup, null, new CancellationToken(canceled: true))).GetAwaiter().GetResult();

        Assert.Equal("current save", pc.Read("saves/file0.json"), "untouched");
        Assert.Equal(1, Directory.GetDirectories(Path.Combine(pc.Dir, "games")).Length, "no extra folders left behind");
    }

    [Test]
    public static void BackupsAndPacksAreNotMixedUp()
    {
        using var pc = new Pc();
        pc.InstallPaperBoat();
        string backup = pc.Manager.BackUpGame(pc.PaperBoat, Path.Combine(pc.Dir, "pb")).File;
        string pack = pc.Manager.ExportGame(pc.PaperBoat, Path.Combine(pc.Dir, "pb")).File;

        Assert.True(Assert.Throws<PackException>(() => pc.Manager.ReadPack(backup)).Message.Contains("backup"), "backup as pack");
        Assert.True(Assert.Throws<BackupException>(() => pc.Manager.ReadBackup(pack)).Message.Contains("pack"), "pack as backup");
    }

    [Test]
    public static void BackupWithAPathEscapeIsRejected()
    {
        using var pc = new Pc();
        string file = Path.Combine(pc.Dir, "bad.marinabackup");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("backup.json").Open()))
                w.Write("""{"Format":1,"PortId":"paperboat","PortName":"PaperBoat","CreatedAt":"2026-11-05T00:00:00Z","Machine":"x","Bytes":1,"Files":[]}""");
            using (var w = new StreamWriter(zip.CreateEntry("files/../../escaped.txt").Open())) w.Write("x");
        }

        Assert.ThrowsAsync<InvalidDataException>(() => pc.Manager.RestoreBackupAsync(file)).GetAwaiter().GetResult();
        Assert.True(!File.Exists(Path.Combine(pc.Dir, "escaped.txt")), "nothing escaped");
    }

    [Test]
    public static void BackupNeedsAnInstalledGame()
    {
        using var pc = new Pc();
        Assert.Throws<InvalidOperationException>(() => pc.Manager.BackUpGame(pc.PaperBoat, Path.Combine(pc.Dir, "pb")));
    }
}
