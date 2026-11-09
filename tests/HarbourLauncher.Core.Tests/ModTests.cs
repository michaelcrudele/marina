using System.IO.Compression;
using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;

namespace HarbourLauncher.Core.Tests;

public static class ModTests
{
    private sealed class Pc : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "hl-mods-" + Guid.NewGuid().ToString("N"));
        public PortManager Manager { get; }
        public PortDefinition Soh => Manager.Catalog.Get("soh");
        public string Downloads => Path.Combine(Dir, "downloads");
        public string Mods => Manager.ModsFolder(Soh);

        public Pc()
        {
            Directory.CreateDirectory(Downloads);
            var paths = new LauncherPaths(Path.Combine(Dir, "appdata"), Path.Combine(Dir, "games"));
            var http = new HttpClient(); // Never used.
            Manager = new PortManager(PortCatalog.LoadBuiltIn(),
                new GitHubReleaseClient(http, new ReleaseCache(paths.ReleaseCacheFile)),
                new AssetDownloader(http), new PortInstaller(), paths);
        }

        public void InstallSoh() =>
            new InstallState("soh", "9.0.0", "x.zip", DateTimeOffset.UtcNow, ["soh.exe"]).Save(Manager.InstallFolder(Soh));

        public string Download(string name, string content = "mod")
        {
            string path = Path.Combine(Downloads, name);
            File.WriteAllText(path, content);
            return path;
        }

        public string DownloadZip(string name, params string[] entries)
        {
            string path = Path.Combine(Downloads, name);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (string entry in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write(entry);
            }
            return path;
        }

        public void Dispose() => Directory.Delete(Dir, recursive: true);
    }

    [Test]
    public static void AddModMovesTheFileIntoTheModsFolder()
    {
        using var pc = new Pc();
        pc.InstallSoh();
        string file = pc.Download("hd-textures.o2r");

        var result = pc.Manager.AddMod(pc.Soh, file);

        Assert.True(File.Exists(Path.Combine(pc.Mods, "hd-textures.o2r")), "the mod is in the mods folder");
        Assert.True(!File.Exists(file), "the mod was moved, not copied");
        Assert.Equal(1, result.Added.Count, "mods added");
        Assert.Equal(0, result.Replaced.Count, "mods replaced");
    }

    [Test]
    public static void AddModReplacesAModWithTheSameName()
    {
        using var pc = new Pc();
        pc.InstallSoh();
        pc.Manager.AddMod(pc.Soh, pc.Download("music.otr", "old"));

        var result = pc.Manager.AddMod(pc.Soh, pc.Download("music.otr", "new"));

        Assert.Equal("new", File.ReadAllText(Path.Combine(pc.Mods, "music.otr")), "mod contents");
        Assert.Equal("music.otr", result.Replaced.Single(), "replaced mod");
    }

    [Test]
    public static void AddModExtractsModsFromAZipAndDeletesTheZip()
    {
        using var pc = new Pc();
        pc.InstallSoh();
        string zip = pc.DownloadZip("pack.zip", "Pack/a.o2r", "Pack/b.otr", "Pack/readme.txt", "../evil.o2r.txt");

        var result = pc.Manager.AddMod(pc.Soh, zip);

        Assert.Equal(2, result.Added.Count, "mods added");
        Assert.True(File.Exists(Path.Combine(pc.Mods, "a.o2r")), "a.o2r extracted to the top of mods/");
        Assert.True(File.Exists(Path.Combine(pc.Mods, "b.otr")), "b.otr extracted to the top of mods/");
        Assert.True(!File.Exists(Path.Combine(pc.Mods, "readme.txt")), "non-mod files are left out");
        Assert.True(!File.Exists(zip), "the zip was moved (its mods extracted)");
        Assert.Equal(2, Directory.GetFiles(pc.Mods).Length, "files in mods/");
    }

    [Test]
    public static void AddModMovesAZipWithNoModArchivesInsideAsIs()
    {
        using var pc = new Pc();
        pc.InstallSoh();
        string zip = pc.DownloadZip("zipped-mod.zip", "textures/link.png");

        var result = pc.Manager.AddMod(pc.Soh, zip);

        Assert.Equal("zipped-mod.zip", result.Added.Single(), "mod added");
        Assert.True(File.Exists(Path.Combine(pc.Mods, "zipped-mod.zip")), "the zip is in the mods folder");
    }

    [Test]
    public static void AddModRejectsFilesThatArentMods()
    {
        using var pc = new Pc();
        pc.InstallSoh();
        string file = pc.Download("virus.exe");

        Assert.Throws<InvalidDataException>(() => pc.Manager.AddMod(pc.Soh, file));
        Assert.True(File.Exists(file), "the file was left where it was");
    }

    [Test]
    public static void AddModNeedsTheGameInstalled()
    {
        using var pc = new Pc();
        Assert.Throws<InvalidOperationException>(() => pc.Manager.AddMod(pc.Soh, pc.Download("a.o2r")));
    }
}
