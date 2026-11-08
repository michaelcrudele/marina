using System.IO.Compression;
using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;
using HarbourLauncher.Core.Sharing;

namespace HarbourLauncher.Core.Tests;

public static class SharingTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    // A real Lighthouse install, as the launcher leaves it after an update.
    private static readonly string[] ReleaseFiles =
        ["Lighthouse.exe", "lighthouse.o2r", "config.yml", "gamecontrollerdb.txt", "readme.txt", "assets/yaml/us/rev0/assets.yaml"];

    private static readonly string[] PlayerFiles =
        ["saves/global.json", "lighthouse.cfg.json", "imgui.ini", "mods/~romhacks/hack.o2r", "randomizer/seed.json"];

    private static readonly string[] NeverShared =
        ["bk.o2r", "torch.hash.yml", "Banjo-Kazooie (USA).z64", "logs/Lighthouse.log", "debug/Lighthouse.pdb", "mods/evil.dll"];

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("This test must not download anything.");
    }

    /// <summary>One PC: its own launcher data and games folder.</summary>
    private sealed class Pc : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "hl-share-" + Guid.NewGuid().ToString("N"));
        public PortManager Manager { get; }
        public PortDefinition Lighthouse => Manager.Catalog.Get("lighthouse");
        public string Game => Manager.InstallFolder(Lighthouse);

        public Pc()
        {
            Directory.CreateDirectory(Dir);
            var paths = new LauncherPaths(Path.Combine(Dir, "appdata"), Path.Combine(Dir, "games"));
            var http = new HttpClient(new NoNetwork()); // Proves imports install from the pack, not GitHub.
            Manager = new PortManager(PortCatalog.LoadBuiltIn(),
                new GitHubReleaseClient(http, new ReleaseCache(paths.ReleaseCacheFile)),
                new AssetDownloader(http), new PortInstaller(), paths);
        }

        public void InstallLighthouse(params string[] extraFiles)
        {
            foreach (string f in ReleaseFiles.Concat(extraFiles)) Write(f, "contents of " + f);
            new InstallState("lighthouse", "1.1.0", "x.zip", DateTimeOffset.UtcNow, ReleaseFiles).Save(Game);
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

    private static List<string> Entries(string pack)
    {
        using var zip = ZipFile.OpenRead(pack);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    [Test]
    public static void PackHoldsTheGameAndPlayerFilesButNoRomData()
    {
        using var pc = new Pc();
        pc.InstallLighthouse([.. PlayerFiles, .. NeverShared]);

        var result = pc.Manager.ExportGame(pc.Lighthouse, Path.Combine(pc.Dir, "my lighthouse"));
        var entries = Entries(result.File);

        Assert.True(result.File.EndsWith(".marinapack", StringComparison.Ordinal), "extension added");
        Assert.True(entries.Contains("manifest.json"), "manifest");
        foreach (string f in PlayerFiles)
            Assert.True(entries.Contains("game/" + f), $"has {f}");
        foreach (string f in ReleaseFiles)
            Assert.True(entries.Contains("program/" + f), $"has program file {f}");
        foreach (string f in NeverShared)
            Assert.True(!entries.Contains("game/" + f) && !entries.Contains("program/" + f), $"leaves out {f}");
        Assert.Equal(PlayerFiles.Length, result.FileCount, "player file count");
        Assert.Equal(ReleaseFiles.Length, result.ProgramFileCount, "program file count");
    }

    [Test]
    public static void ImportOnAPcWithoutTheGameInstallsItFromThePack()
    {
        using var mine = new Pc();
        mine.InstallLighthouse(PlayerFiles);
        mine.Write("saves/global.json", "my 100% save");
        string pack = mine.Manager.ExportGame(mine.Lighthouse, Path.Combine(mine.Dir, "lh.marinapack")).File;

        using var theirs = new Pc(); // No Lighthouse, and no network (NoNetwork throws if used).
        var info = theirs.Manager.ReadPack(pack);
        Assert.True(info.Manifest.HasProgram, "pack carries the game");
        Assert.True(info.ProgramBytes > 0, "program size");

        var result = theirs.Manager.ImportPackAsync(pack).GetAwaiter().GetResult();

        Assert.True(result.InstalledGame && result.InstalledFromPack, "installed from the pack");
        var state = theirs.Manager.GetInstalled(theirs.Lighthouse)!;
        Assert.Equal("1.1.0", state.Tag, "same version as the sharer");
        Assert.Equal(ReleaseFiles.Length, state.Files.Count, "release files recorded, so later updates work");
        Assert.Equal("contents of Lighthouse.exe", theirs.Read("Lighthouse.exe"), "program installed");
        Assert.Equal("my 100% save", theirs.Read("saves/global.json"), "save imported");
        Assert.Equal(PlayerFiles.Length, result.FileCount, "player files counted");
    }

    [Test]
    public static void InstalledGameKeepsItsOwnVersion()
    {
        using var mine = new Pc();
        mine.InstallLighthouse(PlayerFiles);
        string pack = mine.Manager.ExportGame(mine.Lighthouse, Path.Combine(mine.Dir, "lh.marinapack")).File;

        using var theirs = new Pc();
        theirs.InstallLighthouse();
        theirs.Write("Lighthouse.exe", "their newer exe");
        new InstallState("lighthouse", "1.2.0", "y.zip", DateTimeOffset.UtcNow, ReleaseFiles).Save(theirs.Game);

        var result = theirs.Manager.ImportPackAsync(pack).GetAwaiter().GetResult();

        Assert.True(!result.InstalledGame, "nothing installed");
        Assert.Equal("their newer exe", theirs.Read("Lighthouse.exe"), "program untouched");
        Assert.Equal("1.2.0", theirs.Manager.GetInstalled(theirs.Lighthouse)!.Tag, "version untouched");
    }

    [Test]
    public static void GameWithUnknownFilesSharesOnlyPlayerFiles()
    {
        using var pc = new Pc();
        pc.InstallLighthouse(PlayerFiles);
        new InstallState("lighthouse", InstallState.UnknownTag, "", DateTimeOffset.UtcNow, []).Save(pc.Game);

        var result = pc.Manager.ExportGame(pc.Lighthouse, Path.Combine(pc.Dir, "lh.marinapack"));

        Assert.Equal(0, result.ProgramFileCount, "no program files");
        Assert.True(!pc.Manager.ReadPack(result.File).Manifest.HasProgram, "manifest says so");
        Assert.True(!Entries(result.File).Contains("game/bk.o2r"), "still no ROM data");
    }

    [Test]
    public static void ProgramPartCannotSmuggleUnlistedFilesOrRoms()
    {
        foreach (var (listed, entry) in new[] { ("Lighthouse.exe", "program/extra.exe"), ("Banjo.z64", "program/Banjo.z64") })
        {
            using var pc = new Pc();
            string pack = Path.Combine(pc.Dir, "bad.marinapack");
            using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                    w.Write($$"""{"Format":2,"PortId":"lighthouse","PortName":"Lighthouse","ExportedAt":"2026-11-05T00:00:00Z","Files":[],"ProgramFiles":["Lighthouse.exe","{{listed}}"]}""");
                using (var w = new StreamWriter(zip.CreateEntry("program/Lighthouse.exe").Open())) w.Write("exe");
                using (var w = new StreamWriter(zip.CreateEntry(entry).Open())) w.Write("x");
            }

            Assert.ThrowsAsync<PackException>(() => pc.Manager.ImportPackAsync(pack)).GetAwaiter().GetResult();
            Assert.True(pc.Manager.GetInstalled(pc.Lighthouse) is null, $"{entry}: nothing installed");
            Assert.True(!Directory.Exists(pc.Game) || !Directory.EnumerateFiles(pc.Game, "*", SearchOption.AllDirectories).Any(), $"{entry}: nothing written");
        }
    }

    [Test]
    public static void ImportOnAnotherPcRestoresSavesAndBacksUpWhatItReplaces()
    {
        using var mine = new Pc();
        mine.InstallLighthouse(PlayerFiles);
        mine.Write("saves/global.json", "my 100% save");
        mine.Manager.SetArtwork(mine.Lighthouse, WritePng(mine.Dir));
        string pack = mine.Manager.ExportGame(mine.Lighthouse, Path.Combine(mine.Dir, "lh.marinapack")).File;

        using var theirs = new Pc();
        theirs.InstallLighthouse();
        theirs.Write("saves/global.json", "their own save");
        theirs.Write("saves/other-slot.json", "untouched");

        var info = theirs.Manager.ReadPack(pack);
        Assert.Equal("lighthouse", info.Port.Id, "port");
        Assert.Equal("1.1.0", info.Manifest.Tag, "version");

        var result = theirs.Manager.ImportPackAsync(pack).GetAwaiter().GetResult();

        Assert.True(!result.InstalledGame, "game was already installed");
        Assert.Equal("my 100% save", theirs.Read("saves/global.json"), "save imported");
        Assert.Equal("untouched", theirs.Read("saves/other-slot.json"), "other files kept");
        Assert.Equal("their own save", File.ReadAllText(Path.Combine(result.BackupFolder!, "saves", "global.json")), "replaced file backed up");
        Assert.True(theirs.Manager.GetArtwork(theirs.Lighthouse) is not null, "artwork came along");
    }

    [Test]
    public static void HandMadePackWithAProgramIsRejectedBeforeAnythingChanges()
    {
        using var pc = new Pc();
        pc.InstallLighthouse();
        pc.Write("saves/global.json", "mine");
        string pack = Path.Combine(pc.Dir, "evil.marinapack");
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                w.Write("""{"Format":1,"PortId":"lighthouse","PortName":"Lighthouse","Tag":null,"ExportedAt":"2026-11-05T00:00:00Z","Artwork":null,"Files":[]}""");
            using (var w = new StreamWriter(zip.CreateEntry("game/saves/global.json").Open())) w.Write("overwritten");
            using (var w = new StreamWriter(zip.CreateEntry("game/Lighthouse.exe").Open())) w.Write("malware");
        }

        var e = Assert.ThrowsAsync<PackException>(() => pc.Manager.ImportPackAsync(pack)).GetAwaiter().GetResult();

        Assert.True(e.Message.Contains("Lighthouse.exe"), "names the file");
        Assert.Equal("mine", pc.Read("saves/global.json"), "nothing changed");
        Assert.Equal("contents of Lighthouse.exe", pc.Read("Lighthouse.exe"), "exe untouched");
    }

    [Test]
    public static void PackCannotWriteOutsideTheGameFolder()
    {
        using var pc = new Pc();
        pc.InstallLighthouse();
        string pack = Path.Combine(pc.Dir, "slip.marinapack");
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                w.Write("""{"Format":1,"PortId":"lighthouse","PortName":"Lighthouse","ExportedAt":"2026-11-05T00:00:00Z","Files":[]}""");
            using (var w = new StreamWriter(zip.CreateEntry("game/../../escaped.txt").Open())) w.Write("x");
        }

        Assert.ThrowsAsync<InvalidDataException>(() => pc.Manager.ImportPackAsync(pack)).GetAwaiter().GetResult();
        Assert.True(!File.Exists(Path.Combine(pc.Dir, "escaped.txt")), "nothing escaped");
    }

    [Test]
    public static void UnreadablePacksGiveClearErrors()
    {
        using var pc = new Pc();
        string notZip = Path.Combine(pc.Dir, "notes.marinapack");
        File.WriteAllText(notZip, "hello");
        Assert.Throws<PackException>(() => pc.Manager.ReadPack(notZip));

        string otherZip = Path.Combine(pc.Dir, "photos.marinapack");
        using (var zip = ZipFile.Open(otherZip, ZipArchiveMode.Create)) zip.CreateEntry("photo.jpg");
        Assert.True(Assert.Throws<PackException>(() => pc.Manager.ReadPack(otherZip)).Message.Contains("isn't a Marina"), "not a pack");

        string future = Path.Combine(pc.Dir, "future.marinapack");
        using (var zip = ZipFile.Open(future, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            w.Write("""{"Format":99,"PortId":"lighthouse","PortName":"Lighthouse","ExportedAt":"2026-11-05T00:00:00Z","Files":[]}""");
        Assert.True(Assert.Throws<PackException>(() => pc.Manager.ReadPack(future)).Message.Contains("newer"), "newer format");

        string unknown = Path.Combine(pc.Dir, "unknown.marinapack");
        using (var zip = ZipFile.Open(unknown, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            w.Write("""{"Format":1,"PortId":"zelda64recomp","PortName":"Zelda64Recomp","ExportedAt":"2026-11-05T00:00:00Z","Files":[]}""");
        Assert.True(Assert.Throws<PackException>(() => pc.Manager.ReadPack(unknown)).Message.Contains("doesn't know"), "unknown game");
    }

    [Test]
    public static void ShareRules()
    {
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(Sharing.ShareRules.IsShareable("mods/pack.o2r", none), "mods archives are fine");
        Assert.True(!Sharing.ShareRules.IsShareable("oot.o2r", none), "top-level ROM archive");
        Assert.True(!Sharing.ShareRules.IsShareable("roms/game.N64", none), "ROM anywhere");
        Assert.True(!Sharing.ShareRules.IsShareable("mods/run.BAT", none), "scripts anywhere");
        Assert.True(!Sharing.ShareRules.IsShareable(@"logs\x.log", none), "backslashes normalised");
        Assert.True(!Sharing.ShareRules.IsShareable("soh.o2r", new HashSet<string>(["soh.o2r"])), "release file");
    }

    private static string WritePng(string dir)
    {
        string path = Path.Combine(dir, "art.png");
        File.WriteAllBytes(path, Png);
        return path;
    }

    // ---- Choosing what goes in ----

    /// <summary>A PaperBoat folder shaped like a real one, with an HD texture pack.</summary>
    private static Pc PaperBoatPc(out PortDefinition paperboat)
    {
        var pc = new Pc();
        paperboat = pc.Manager.Catalog.Get("paperboat");
        string game = pc.Manager.InstallFolder(paperboat);
        void Write(string rel, int bytes)
        {
            string path = Path.Combine(game, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
        }
        string[] release = ["Paperboat.exe", "shaderc_shared.dll", "paperboat.o2r", "config.yml", "assets/yaml/us/ui.yml", "debug/Paperboat.pdb"];
        foreach (string f in release) Write(f, 1000);
        Write("pm64.o2r", 40_000);
        Write("torch.hash.yml", 10);
        Write("logs/Paperboat.log", 10);
        Write("paperboat.cfg.json", 150);
        Write("imgui.ini", 60);
        Write("saves/file0.json", 100);
        Write("mods/paperboat-hd.o2r", 4_590);
        Write("mods/paperboat-hd-WHITE.o2r", 19_100);
        Write("mods/custom_mod_files_go_here.txt", 0);
        new InstallState("paperboat", "1.0.3", "x.zip", DateTimeOffset.UtcNow, release).Save(game);
        return pc;
    }

    [Test]
    public static void ShareContentsListsEachChoiceWithItsSize()
    {
        using var pc = PaperBoatPc(out var paperboat);
        var contents = pc.Manager.GetShareContents(paperboat);
        string Ids() => string.Join(",", contents.Items.Select(i => i.Id));

        Assert.Equal("game,settings,folder:saves,mod:paperboat-hd-WHITE.o2r,mod:paperboat-hd.o2r", Ids(), "choices");
        Assert.Equal(5000L, contents.Items[0].Bytes, "game size (no debug symbols)");
        Assert.Equal(210L, contents.Items[1].Bytes, "settings size");
        Assert.Equal(19_100L, contents.Items.Single(i => i.Id == "mod:paperboat-hd-WHITE.o2r").Bytes, "mod size");
        Assert.True(contents.NoGameReason is null, "game can be included");

        var rom = contents.NeverIncluded.Single(x => x.File == "pm64.o2r");
        Assert.Equal("made from your ROM", rom.Reason, "pm64.o2r reason");
        Assert.Equal(40_000L, rom.Bytes, "pm64.o2r size");
        Assert.True(contents.NeverIncluded.Any(x => x.File == "logs/Paperboat.log"), "logs listed");
        Assert.True(contents.NeverIncluded.Any(x => x.File == "debug/Paperboat.pdb"), "debug listed");
    }

    [Test]
    public static void OnlyChosenItemsGoInThePack()
    {
        using var pc = PaperBoatPc(out var paperboat);
        string pack = pc.Manager.ExportGame(paperboat, Path.Combine(pc.Dir, "pb"),
            ["game", "settings", "folder:saves", "mod:paperboat-hd.o2r"]).File;
        var entries = Entries(pack);

        Assert.True(entries.Contains("program/Paperboat.exe"), "game");
        Assert.True(entries.Contains("game/saves/file0.json"), "saves");
        Assert.True(entries.Contains("game/mods/paperboat-hd.o2r"), "chosen mod");
        Assert.True(!entries.Contains("game/mods/paperboat-hd-WHITE.o2r"), "unchosen mod left out");
        Assert.True(!entries.Any(e => e.EndsWith("pm64.o2r", StringComparison.Ordinal)), "never the ROM archive");
    }

    [Test]
    public static void TexturePacksAreStoredWithoutRecompressing()
    {
        using var pc = PaperBoatPc(out var paperboat);
        var progress = new List<double>();
        string pack = pc.Manager.ExportGame(paperboat, Path.Combine(pc.Dir, "pb"), null,
            new SyncProgress(progress.Add)).File;

        using var zip = ZipFile.OpenRead(pack);
        var mod = zip.GetEntry("game/mods/paperboat-hd-WHITE.o2r")!;
        var cfg = zip.GetEntry("program/config.yml")!;
        Assert.True(mod.CompressedLength >= mod.Length, "texture pack stored as-is");
        Assert.True(cfg.CompressedLength < cfg.Length, "other files compressed");
        Assert.Equal(1.0, progress.Last(), "progress reaches 100%");
    }

    [Test]
    public static void CancellingLeavesNoFileBehind()
    {
        using var pc = PaperBoatPc(out var paperboat);
        string target = Path.Combine(pc.Dir, "pb.marinapack");
        Assert.Throws<OperationCanceledException>(() =>
            pc.Manager.ExportGame(paperboat, target, null, null, new CancellationToken(canceled: true)));
        Assert.True(!File.Exists(target) && !File.Exists(target + ".tmp"), "no partial pack");
    }

    [Test]
    public static void ChoosingNothingIsAnError()
    {
        using var pc = PaperBoatPc(out var paperboat);
        Assert.Throws<InvalidOperationException>(() => pc.Manager.ExportGame(paperboat, Path.Combine(pc.Dir, "pb"), []));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
