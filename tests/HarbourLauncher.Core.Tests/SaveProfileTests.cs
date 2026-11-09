using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;
using HarbourLauncher.Core.Saves;

namespace HarbourLauncher.Core.Tests;

public static class SaveProfileTests
{
    private sealed class Pc : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "hl-profiles-" + Guid.NewGuid().ToString("N"));
        public PortManager Manager { get; }
        public PortDefinition Soh => Manager.Catalog.Get("soh");
        public string Game => Manager.InstallFolder(Soh);

        public Pc()
        {
            var paths = new LauncherPaths(Path.Combine(Dir, "appdata"), Path.Combine(Dir, "games"));
            var http = new HttpClient(); // Never used.
            Manager = new PortManager(PortCatalog.LoadBuiltIn(),
                new GitHubReleaseClient(http, new ReleaseCache(paths.ReleaseCacheFile)),
                new AssetDownloader(http), new PortInstaller(), paths);
            new InstallState("soh", "9.0.0", "x.zip", DateTimeOffset.UtcNow, ["soh.exe"]).Save(Game);
            Write("shipofharkinian.json", "settings");
        }

        public void Write(string rel, string text)
        {
            string path = Path.Combine(Game, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public string? Read(string rel) => File.Exists(Path.Combine(Game, rel)) ? File.ReadAllText(Path.Combine(Game, rel)) : null;

        public void Dispose() => Directory.Delete(Dir, recursive: true);
    }

    [Test]
    public static void AGameStartsWithOneDefaultProfileHoldingItsSaves()
    {
        using var pc = new Pc();
        pc.Write("Save/file1.sav", "link");

        var profiles = pc.Manager.GetSaveProfiles(pc.Soh);

        Assert.Equal(1, profiles.Count, "profiles");
        Assert.Equal(SaveProfiles.DefaultName, profiles[0].Name, "name");
        Assert.True(profiles[0].IsActive, "the default profile is active");
        Assert.Equal(4L, profiles[0].Bytes, "size of the saves");
    }

    [Test]
    public static void SwitchingToANewEmptyProfileStartsFreshAndSwitchingBackRestores()
    {
        using var pc = new Pc();
        pc.Write("Save/file1.sav", "main");
        pc.Write("Save/global.sav", "global");

        var rando = pc.Manager.CreateSaveProfile(pc.Soh, "Randomizer", copyActive: false);
        pc.Manager.SwitchSaveProfile(pc.Soh, rando.Id);

        Assert.True(!Directory.Exists(Path.Combine(pc.Game, "Save")), "the game folder has no saves, like a new install");
        Assert.Equal("settings", pc.Read("shipofharkinian.json"), "settings are shared by every profile");
        Assert.Equal("Randomizer", pc.Manager.GetActiveSaveProfile(pc.Soh).Name, "active profile");

        pc.Write("Save/file1.sav", "rando"); // The game makes saves in the new profile.
        pc.Manager.SwitchSaveProfile(pc.Soh, SaveProfiles.DefaultId);

        Assert.Equal("main", pc.Read("Save/file1.sav"), "main saves are back");
        Assert.Equal("global", pc.Read("Save/global.sav"), "every save file came back");

        pc.Manager.SwitchSaveProfile(pc.Soh, rando.Id);
        Assert.Equal("rando", pc.Read("Save/file1.sav"), "randomizer saves were kept");
    }

    [Test]
    public static void CopyingTheActiveProfileBranchesOffWithoutChangingIt()
    {
        using var pc = new Pc();
        pc.Write("Save/file1.sav", "before boss");

        var branch = pc.Manager.CreateSaveProfile(pc.Soh, "No damage", copyActive: true);
        pc.Write("Save/file1.sav", "after boss");
        pc.Manager.SwitchSaveProfile(pc.Soh, branch.Id);

        Assert.Equal("before boss", pc.Read("Save/file1.sav"), "the copy has the saves from when it was made");
    }

    [Test]
    public static void SaveFolderNamesMatchIgnoringCaseAndKeepTheirName()
    {
        using var pc = new Pc();
        pc.Write("saves/file0.json", "pm64");
        var other = pc.Manager.CreateSaveProfile(pc.Soh, "Other", copyActive: false);

        pc.Manager.SwitchSaveProfile(pc.Soh, other.Id);
        Assert.True(!Directory.Exists(Path.Combine(pc.Game, "saves")), "lower-case saves/ was switched out");
        pc.Manager.SwitchSaveProfile(pc.Soh, SaveProfiles.DefaultId);

        Assert.True(Directory.GetDirectories(pc.Game).Any(d => Path.GetFileName(d) == "saves"), "it comes back with its own name");
        Assert.Equal("pm64", pc.Read("saves/file0.json"), "saves");
    }

    [Test]
    public static void NamesMustBeUniqueAndNotBlank()
    {
        using var pc = new Pc();
        pc.Manager.CreateSaveProfile(pc.Soh, "  Hundred percent ", copyActive: false);

        Assert.Throws<ArgumentException>(() => pc.Manager.CreateSaveProfile(pc.Soh, "hundred PERCENT", copyActive: false));
        Assert.Throws<ArgumentException>(() => pc.Manager.CreateSaveProfile(pc.Soh, "   ", copyActive: false));
        Assert.True(pc.Manager.GetSaveProfiles(pc.Soh).Any(p => p.Name == "Hundred percent"), "names are trimmed");
    }

    [Test]
    public static void RenameAndDelete()
    {
        using var pc = new Pc();
        var run = pc.Manager.CreateSaveProfile(pc.Soh, "Run", copyActive: false);
        pc.Manager.SwitchSaveProfile(pc.Soh, run.Id);
        pc.Write("Save/file1.sav", "run");

        Assert.Throws<InvalidOperationException>(() => pc.Manager.DeleteSaveProfile(pc.Soh, run.Id));

        pc.Manager.RenameSaveProfile(pc.Soh, run.Id, "Speedrun");
        pc.Manager.SwitchSaveProfile(pc.Soh, SaveProfiles.DefaultId);
        pc.Manager.DeleteSaveProfile(pc.Soh, run.Id);

        Assert.Equal(1, pc.Manager.GetSaveProfiles(pc.Soh).Count, "profiles left");
        Assert.True(!Directory.Exists(SaveProfiles.ProfileFolder(pc.Game, run.Id)), "the deleted profile's saves are gone");
    }

    [Test]
    public static void AFailedSwitchLeavesEverythingAsItWas()
    {
        using var pc = new Pc();
        pc.Write("Save/file1.sav", "main");
        var other = pc.Manager.CreateSaveProfile(pc.Soh, "Other", copyActive: false);
        // Something left in the default profile's store makes the switch refuse rather than mix saves.
        Directory.CreateDirectory(SaveProfiles.ProfileFolder(pc.Game, SaveProfiles.DefaultId));
        File.WriteAllText(Path.Combine(SaveProfiles.ProfileFolder(pc.Game, SaveProfiles.DefaultId), "stray.sav"), "?");

        Assert.Throws<IOException>(() => pc.Manager.SwitchSaveProfile(pc.Soh, other.Id));
        Assert.Equal("main", pc.Read("Save/file1.sav"), "saves untouched");
        Assert.Equal(SaveProfiles.DefaultName, pc.Manager.GetActiveSaveProfile(pc.Soh).Name, "still on the same profile");
    }

    [Test]
    public static void InactiveProfilesAreInBackups()
    {
        using var pc = new Pc();
        pc.Write("Save/file1.sav", "main");
        var other = pc.Manager.CreateSaveProfile(pc.Soh, "Other", copyActive: true);

        var contents = pc.Manager.GetBackupContents(pc.Soh);

        Assert.True(contents.Files.Contains($"{SaveProfiles.StoreFolder}/{other.Id}/Save/file1.sav"), "the other profile's saves are backed up");
        Assert.True(contents.Files.Contains($"{SaveProfiles.StoreFolder}/profiles.json"), "the profile list is backed up");
    }

    [Test]
    public static void ProfileListsWrittenByTheFirstBuildStillLoad()
    {
        using var pc = new Pc();
        // The first build also wrote the computed "Active" property.
        pc.Write(".marina/profiles/profiles.json", """
            { "ActiveId": "b3711dc3",
              "Profiles": [ { "Id": "default", "Name": "Default", "CreatedAt": "2026-11-09T05:52:23+00:00" },
                            { "Id": "b3711dc3", "Name": "Test", "CreatedAt": "2026-11-09T05:52:23+00:00" } ],
              "Active": { "Id": "b3711dc3", "Name": "Test", "CreatedAt": "2026-11-09T05:52:23+00:00" } }
            """);

        Assert.Equal("Test", pc.Manager.GetActiveSaveProfile(pc.Soh).Name, "active profile");
        pc.Manager.RenameSaveProfile(pc.Soh, "b3711dc3", "Test run");
        Assert.True(!pc.Read(".marina/profiles/profiles.json")!.Contains("\"Active\""), "Active is no longer written");
    }
}
