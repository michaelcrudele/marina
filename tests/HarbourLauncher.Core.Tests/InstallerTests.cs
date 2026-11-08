using System.IO.Compression;
using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.Install;

namespace HarbourLauncher.Core.Tests;

public static class InstallerTests
{
    private static readonly PortDefinition Soh = new()
    {
        Id = "soh", Name = "Ship of Harkinian", Game = "OoT", Repo = "HarbourMasters/Shipwright",
        WindowsAsset = "SoH-*-Win64.zip", Exe = "soh.exe", Skip = ["debug/"],
    };

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hl-test-" + Guid.NewGuid().ToString("N"));
        public string Install => Path.Combine(Root, "install");
        public Sandbox() => Directory.CreateDirectory(Root);

        /// <summary>Builds a release zip. Entries are written in the given order.</summary>
        public string Zip(string name, params (string Path, string Content)[] entries)
        {
            string path = Path.Combine(Root, name);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (p, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(p).Open());
                writer.Write(content);
            }
            return path;
        }

        public string Read(string rel) => File.ReadAllText(Path.Combine(Install, rel));
        public bool Has(string rel) => File.Exists(Path.Combine(Install, rel));
        public void Write(string rel, string content)
        {
            string path = Path.Combine(Install, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static string V1(Sandbox s) => s.Zip("v1.zip",
        ("soh.exe", "exe v1"), ("soh.o2r", "port archive v1"), ("readme.txt", "readme"),
        ("gamecontrollerdb.txt", "db v1"), ("assets/config.yml", "cfg v1"), ("debug/soh.pdb", "huge symbols"));

    private static string V2(Sandbox s) => s.Zip("v2.zip",
        ("soh.exe", "exe v2"), ("soh.o2r", "port archive v2"),
        ("gamecontrollerdb.txt", "db v2"), ("assets/config.yml", "cfg v2"), ("newfile.dll", "new"));

    [Test]
    public static void FreshInstallExtractsReleaseAndSkipsDebugSymbols()
    {
        using var s = new Sandbox();
        var result = new PortInstaller().Install(Soh, V1(s), "1.0", "SoH-A-Win64.zip", s.Install);

        Assert.Equal("exe v1", s.Read("soh.exe"), "exe");
        Assert.Equal("cfg v1", s.Read("assets/config.yml"), "nested file");
        Assert.True(!s.Has("debug/soh.pdb"), "debug symbols skipped");
        Assert.Equal(5, result.Written.Count, "files written");
        Assert.Equal("1.0", InstallState.Load(s.Install)!.Tag, "state tag");
    }

    [Test]
    public static void UpdateReplacesReleaseFilesAndNeverTouchesUserFiles()
    {
        using var s = new Sandbox();
        var installer = new PortInstaller();
        installer.Install(Soh, V1(s), "1.0", "a.zip", s.Install);

        // Everything a player accumulates. None of it is in a release zip.
        s.Write("oot.o2r", "generated from my ROM");
        s.Write("Save/file1.sav", "100 hours");
        s.Write("mods/custom-tunic.o2r", "my mod");
        s.Write("shipofharkinian.json", "my settings");
        s.Write("imgui.ini", "window layout");

        var result = installer.Install(Soh, V2(s), "2.0", "b.zip", s.Install);

        Assert.Equal("exe v2", s.Read("soh.exe"), "exe updated");
        Assert.Equal("port archive v2", s.Read("soh.o2r"), "the port's own .o2r is replaced, not preserved");
        Assert.Equal("new", s.Read("newfile.dll"), "new release file added");
        Assert.Equal("generated from my ROM", s.Read("oot.o2r"), "ROM-generated archive kept");
        Assert.Equal("100 hours", s.Read("Save/file1.sav"), "save kept");
        Assert.Equal("my mod", s.Read("mods/custom-tunic.o2r"), "mod kept");
        Assert.Equal("my settings", s.Read("shipofharkinian.json"), "settings kept");
        Assert.Equal("window layout", s.Read("imgui.ini"), "imgui layout kept");

        Assert.True(!s.Has("readme.txt"), "file dropped by the new release is removed");
        Assert.Equal("readme.txt", result.Removed.Single(), "removed list");
        Assert.Equal("readme", File.ReadAllText(Path.Combine(result.BackupFolder, "readme.txt")), "removed file backed up");
        Assert.Equal("exe v1", File.ReadAllText(Path.Combine(result.BackupFolder, "soh.exe")), "replaced file backed up");
    }

    [Test]
    public static void KeepPatternsProtectEditableReleaseFiles()
    {
        using var s = new Sandbox();
        var port = Soh with { Keep = ["assets/config.yml"] };
        var installer = new PortInstaller();
        installer.Install(port, V1(s), "1.0", "a.zip", s.Install);
        s.Write("assets/config.yml", "user edited");

        var result = installer.Install(port, V2(s), "2.0", "b.zip", s.Install);

        Assert.Equal("user edited", s.Read("assets/config.yml"), "kept file");
        Assert.Equal("assets/config.yml", result.Kept.Single(), "kept list");
    }

    [Test]
    public static void AdoptingAManualInstallKeepsSavesAndBacksUpOldFiles()
    {
        using var s = new Sandbox();
        s.Write("soh.exe", "manually installed exe");
        s.Write("Save/file1.sav", "progress");

        var result = new PortInstaller().Install(Soh, V1(s), "1.0", "a.zip", s.Install);

        Assert.Equal("exe v1", s.Read("soh.exe"), "exe replaced");
        Assert.Equal("progress", s.Read("Save/file1.sav"), "save kept");
        Assert.Equal("manually installed exe", File.ReadAllText(Path.Combine(result.BackupFolder, "soh.exe")), "old exe backed up");
    }

    [Test]
    public static void FailureMidUpdateRollsEverythingBack()
    {
        using var s = new Sandbox();
        var installer = new PortInstaller();
        installer.Install(Soh, V1(s), "1.0", "a.zip", s.Install);
        s.Write("Save/file1.sav", "progress");

        // A folder sits where the new release wants a file, so the move fails
        // after soh.exe and soh.o2r have already been replaced.
        Directory.CreateDirectory(Path.Combine(s.Install, "blocker.dll"));
        string bad = s.Zip("bad.zip", ("soh.exe", "exe v2"), ("soh.o2r", "port archive v2"), ("blocker.dll", "x"));

        Assert.Throws<Exception>(() => installer.Install(Soh, bad, "2.0", "bad.zip", s.Install));

        Assert.Equal("exe v1", s.Read("soh.exe"), "exe restored");
        Assert.Equal("port archive v1", s.Read("soh.o2r"), "archive restored");
        Assert.Equal("readme", s.Read("readme.txt"), "untouched file still there");
        Assert.Equal("progress", s.Read("Save/file1.sav"), "save untouched");
        Assert.Equal("1.0", InstallState.Load(s.Install)!.Tag, "state still says 1.0");
    }

    [Test]
    public static void ZipSlipIsRejectedBeforeAnythingChanges()
    {
        using var s = new Sandbox();
        new PortInstaller().Install(Soh, V1(s), "1.0", "a.zip", s.Install);
        string evil = s.Zip("evil.zip", ("soh.exe", "exe v2"), ("../../outside.txt", "gotcha"));

        Assert.Throws<InvalidDataException>(() => new PortInstaller().Install(Soh, evil, "2.0", "e.zip", s.Install));

        Assert.True(!File.Exists(Path.Combine(s.Root, "outside.txt")), "nothing written outside");
        Assert.Equal("exe v1", s.Read("soh.exe"), "install untouched");
    }

    [Test]
    public static void ReleaseWithoutTheExeIsRejectedBeforeAnythingChanges()
    {
        using var s = new Sandbox();
        new PortInstaller().Install(Soh, V1(s), "1.0", "a.zip", s.Install);
        string renamed = s.Zip("renamed.zip", ("ship.exe", "renamed exe"), ("soh.o2r", "v2"));

        var e = Assert.Throws<InvalidDataException>(() => new PortInstaller().Install(Soh, renamed, "2.0", "r.zip", s.Install));

        Assert.True(e.Message.Contains("soh.exe"), "error names the missing exe");
        Assert.Equal("port archive v1", s.Read("soh.o2r"), "install untouched");
    }
}
