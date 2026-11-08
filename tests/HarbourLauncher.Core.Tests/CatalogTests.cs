using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;

namespace HarbourLauncher.Core.Tests;

public static class CatalogTests
{
    // Asset names from each port's latest release, recorded November 2026.
    // If a port renames its assets, its windowsAsset pattern must still pick
    // exactly one Windows zip from a list like this.
    private static readonly Dictionary<string, string[]> RealAssets = new()
    {
        ["soh"] = ["SoH-Dewey-Bravo-Mac.zip", "SoH-Dewey-Bravo-Win64.zip", "SoH-Dewey-Bravo.appimage"],
        ["2ship"] = ["2Ship-Battler-Bravo-Linux.zip", "2Ship-Battler-Bravo-Mac.zip", "2Ship-Battler-Bravo-Win64.zip"],
        ["starship"] = ["Starship-Barnard-Alfa-Linux.zip", "Starship-Barnard-Alfa-Switch.zip", "Starship-Barnard-Alfa-Windows.zip"],
        ["spaghettikart"] = ["spaghetti-linux.zip", "spaghetti-mac-arm64.zip", "spaghetti-mac-intel-x64.zip", "spaghetti-windows.zip"],
        ["ghostship"] = ["Nautilus-Alfa-Android.zip", "Nautilus-Alfa-Linux.zip", "Nautilus-Alfa-Mac.zip", "Nautilus-Alfa-Win64.zip", "Nautilus-Alfa-iOS.zip"],
        // Lighthouse and PaperBoat list 5 assets; these are the 3 visible on the release page.
        ["lighthouse"] = ["Lighthouse-Hatteras-Alfa-Linux.zip", "Lighthouse-Hatteras-Alfa-Mac.zip", "Lighthouse-Hatteras-Alfa-Win64.zip"],
        ["paperboat"] = ["Paperboat-Mulberry-Delta-Linux.zip", "Paperboat-Mulberry-Delta-Mac.zip", "Paperboat-Mulberry-Delta-Win64.zip"],
    };

    [Test]
    public static void BuiltInCatalogLoadsEveryPort()
    {
        var catalog = PortCatalog.LoadBuiltIn();
        Assert.Equal(7, catalog.Ports.Count, "port count");
        Assert.Equal("HarbourMasters/Shipwright", catalog.Get("SOH").Repo, "lookup is case-insensitive");
    }

    [Test]
    public static void EachPatternPicksExactlyTheWindowsZipFromRealReleases()
    {
        foreach (var port in PortCatalog.LoadBuiltIn().Ports)
        {
            var release = new ReleaseInfo(port.Repo, "test", "", null, "",
                RealAssets[port.Id].Select(n => new ReleaseAsset(n, "", 1, null)).ToList());
            var asset = release.FindAsset(port.WindowsAsset);
            Assert.True(asset.Name.Contains("win", StringComparison.OrdinalIgnoreCase),
                $"{port.Id} picked {asset.Name}");
        }
    }

    [Test]
    public static void AmbiguousPatternIsAnErrorNotAGuess()
    {
        var release = new ReleaseInfo("x/y", "1", "", null, "",
            [new("a-Win64.zip", "", 1, null), new("b-Win64.zip", "", 1, null)]);
        Assert.Throws<AssetNotFoundException>(() => release.FindAsset("*-Win64.zip"));
    }

    [Test]
    public static void DuplicatePortIdsAreRejected() =>
        Assert.Throws<InvalidDataException>(() => PortCatalog.Parse("""
            {"ports":[{"id":"a","name":"A","game":"g","repo":"o/r","windowsAsset":"*","exe":"a.exe"},
                      {"id":"A","name":"B","game":"g","repo":"o/r","windowsAsset":"*","exe":"b.exe"}]}
            """));

    [Test]
    public static void GlobRules()
    {
        Assert.True(Glob.IsMatch("debug/", "debug/soh.pdb"), "folder pattern matches contents");
        Assert.True(Glob.IsMatch("debug/", "debug/sub/x.pdb"), "folder pattern matches nested contents");
        Assert.True(!Glob.IsMatch("debug/", "debugger.exe"), "folder pattern is not a prefix match");
        Assert.True(Glob.IsMatch("*.yml", "config.yml"), "star matches file name");
        Assert.True(!Glob.IsMatch("*.yml", "assets/config.yml"), "star does not cross folders");
        Assert.True(Glob.IsMatch("**/*.yml", "assets/config.yml"), "double star crosses folders");
        Assert.True(Glob.IsMatch("**/*.yml", "config.yml"), "double star matches zero folders");
        Assert.True(Glob.IsMatch("SOH.EXE", "soh.exe"), "case-insensitive");
        Assert.True(Glob.IsMatch("mods/x.o2r", @"mods\x.o2r"), "backslashes normalized");
    }
}
