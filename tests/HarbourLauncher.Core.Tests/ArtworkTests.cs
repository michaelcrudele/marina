using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;

namespace HarbourLauncher.Core.Tests;

public static class ArtworkTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0];

    private sealed class Sandbox : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "hl-art-" + Guid.NewGuid().ToString("N"));
        public PortManager Manager { get; }
        public PortDefinition Soh => Manager.Catalog.Get("soh");

        public Sandbox()
        {
            Directory.CreateDirectory(Dir);
            var paths = new LauncherPaths(Path.Combine(Dir, "appdata"));
            var http = new HttpClient(); // Never used.
            Manager = new PortManager(PortCatalog.LoadBuiltIn(),
                new GitHubReleaseClient(http, new ReleaseCache(paths.ReleaseCacheFile)),
                new AssetDownloader(http), new PortInstaller(), paths);
        }

        public string File(string name, byte[] bytes)
        {
            string path = Path.Combine(Dir, name);
            System.IO.File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose() => Directory.Delete(Dir, recursive: true);
    }

    [Test]
    public static void NoArtworkByDefault()
    {
        using var s = new Sandbox();
        Assert.Equal(null, s.Manager.GetArtwork(s.Soh), "artwork");
    }

    [Test]
    public static void ChosenImageIsCopiedSoTheOriginalCanGo()
    {
        using var s = new Sandbox();
        string original = s.File("My Box Art.PNG", Png);

        string stored = s.Manager.SetArtwork(s.Soh, original);
        File.Delete(original);

        Assert.Equal(stored, s.Manager.GetArtwork(s.Soh), "artwork path");
        Assert.True(File.Exists(stored), "copy kept");
        Assert.True(stored.EndsWith("soh.png", StringComparison.Ordinal), "named after the game");
    }

    [Test]
    public static void ReplacingWithAnotherFormatLeavesOneImage()
    {
        using var s = new Sandbox();
        s.Manager.SetArtwork(s.Soh, s.File("a.png", Png));
        string jpg = s.Manager.SetArtwork(s.Soh, s.File("b.jpg", Jpeg));

        Assert.Equal(jpg, s.Manager.GetArtwork(s.Soh), "newest wins");
        Assert.Equal(1, Directory.GetFiles(Path.GetDirectoryName(jpg)!).Length, "one file");
    }

    [Test]
    public static void ClearingGoesBackToTheBanner()
    {
        using var s = new Sandbox();
        s.Manager.SetArtwork(s.Soh, s.File("a.png", Png));
        s.Manager.ClearArtwork(s.Soh);
        Assert.Equal(null, s.Manager.GetArtwork(s.Soh), "artwork");
    }

    [Test]
    public static void NonImagesAreRejectedAndKeepTheCurrentArtwork()
    {
        using var s = new Sandbox();
        string current = s.Manager.SetArtwork(s.Soh, s.File("a.png", Png));

        Assert.Throws<InvalidDataException>(() => s.Manager.SetArtwork(s.Soh, s.File("notes.txt", "hello"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => s.Manager.SetArtwork(s.Soh, s.File("fake.png", "not really a png"u8.ToArray())));

        Assert.Equal(current, s.Manager.GetArtwork(s.Soh), "unchanged");
    }

    [Test]
    public static void ArtworkIsPerGame()
    {
        using var s = new Sandbox();
        s.Manager.SetArtwork(s.Soh, s.File("a.png", Png));
        Assert.Equal(null, s.Manager.GetArtwork(s.Manager.Catalog.Get("2ship")), "other game");
    }

    [Test]
    public static void CropChoiceIsRememberedAndResetByANewImage()
    {
        using var s = new Sandbox();
        s.Manager.SetArtwork(s.Soh, s.File("a.png", Png));
        s.Manager.SetArtworkCropped(s.Soh, true);
        Assert.True(s.Manager.IsArtworkCropped(s.Soh), "cropped");
        Assert.True(!s.Manager.IsArtworkCropped(s.Manager.Catalog.Get("2ship")), "per game");

        s.Manager.SetArtworkCropped(s.Soh, true); // Twice is harmless.
        s.Manager.SetArtwork(s.Soh, s.File("b.jpg", Jpeg));
        Assert.True(!s.Manager.IsArtworkCropped(s.Soh), "new image starts uncropped");
    }
}
