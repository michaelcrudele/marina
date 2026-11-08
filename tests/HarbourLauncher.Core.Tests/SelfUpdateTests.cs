using System.Net;
using System.Security.Cryptography;
using System.Text;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.SelfUpdate;

namespace HarbourLauncher.Core.Tests;

public static class SelfUpdateTests
{
    private static readonly byte[] NewExe = Encoding.UTF8.GetBytes("new marina exe");
    private static readonly string NewExeHash = Convert.ToHexString(SHA256.HashData(NewExe));

    private static AppVersion V(string text) =>
        AppVersion.TryParse(text, out var v) ? v : throw new AssertionException($"'{text}' didn't parse.");

    /// <summary>Answers the API with this release JSON, and asset URLs with their bytes.</summary>
    private sealed class FakeGitHub(string releaseJson, Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            var response = url.Contains("api.github.com")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) }
                : files.TryGetValue(url, out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }

    private static string Release(string tag, bool withExe = true, string? digest = null, bool withChecksum = false)
    {
        var assets = new List<string>();
        if (withExe)
            assets.Add($$"""{ "name": "Marina.exe", "size": {{NewExe.Length}}, "browser_download_url": "https://dl.invalid/exe", "digest": {{(digest is null ? "null" : $"\"sha256:{digest}\"")}} }""");
        if (withChecksum)
            assets.Add($$"""{ "name": "Marina.exe.sha256", "size": {{ChecksumFile.Length}}, "browser_download_url": "https://dl.invalid/sum", "digest": null }""");
        return $$"""
            { "tag_name": "{{tag}}", "name": "Marina {{tag}}", "published_at": "2026-11-08T12:00:00Z",
              "html_url": "https://github.com/owner/marina/releases/tag/{{tag}}",
              "assets": [ {{string.Join(",", assets)}} ] }
            """;
    }

    private static readonly byte[] ChecksumFile = Encoding.UTF8.GetBytes($"{NewExeHash.ToLowerInvariant()}  Marina.exe\n");

    private static (SelfUpdater Updater, string Folder) Make(string releaseJson, string current = "0.1.5")
    {
        string folder = Path.Combine(Path.GetTempPath(), "marina-selfupdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string exe = Path.Combine(folder, "Marina.exe");
        File.WriteAllText(exe, "old marina exe");

        var http = new HttpClient(new FakeGitHub(releaseJson, new()
        {
            ["https://dl.invalid/exe"] = NewExe,
            ["https://dl.invalid/sum"] = ChecksumFile,
        }));
        var releases = new GitHubReleaseClient(http, new ReleaseCache(Path.Combine(folder, "cache.json")));
        return (new SelfUpdater(releases, new AssetDownloader(http), "owner/marina", V(current), exe), folder);
    }

    [Test]
    public static void ParsesVersionsWithOrWithoutPrefixAndBuildInfo()
    {
        Assert.Equal(new AppVersion(0, 1, 42), V("v0.1.42"), "v prefix");
        Assert.Equal(new AppVersion(1, 2, 3, "beta"), V("1.2.3-beta"), "pre-release");
        Assert.Equal(new AppVersion(0, 1, 42, "dev"), V("0.1.42-dev+3f9c2e1"), "build info dropped");
        Assert.Equal(new AppVersion(2, 0, 0), V("2.0"), "two parts");
        Assert.True(!AppVersion.TryParse("latest", out _), "a word isn't a version");
        Assert.True(!AppVersion.TryParse("1.2.3-", out _), "an empty pre-release isn't a version");
    }

    [Test]
    public static void ComparesVersionsInReleaseOrder()
    {
        Assert.True(V("0.1.10") > V("0.1.9"), "numbers compare as numbers");
        Assert.True(V("0.2.0") > V("0.1.99"), "minor beats patch");
        Assert.True(V("0.1.42") > V("0.1.42-dev"), "a release beats its dev build");
        Assert.True(V("1.0.0-beta.2") > V("1.0.0-beta.1"), "pre-release numbers");
        Assert.True(V("1.0.0-rc") > V("1.0.0-beta"), "pre-release words");
        Assert.Equal(0, V("v1.2.3").CompareTo(V("1.2.3")), "same version");
    }

    [Test]
    public static async Task FindsANewerReleaseWithItsExe()
    {
        var (updater, _) = Make(Release("v0.1.7", digest: NewExeHash));
        var update = await updater.CheckAsync(force: true);
        Assert.True(update is not null, "update found");
        Assert.Equal(new AppVersion(0, 1, 7), update!.Version, "version");
        Assert.Equal("Marina.exe", update.Exe.Name, "asset");
    }

    [Test]
    public static async Task IgnoresSameOlderAndExelessReleases()
    {
        Assert.True(await Make(Release("v0.1.5")).Updater.CheckAsync(true) is null, "same version");
        Assert.True(await Make(Release("v0.1.4")).Updater.CheckAsync(true) is null, "older version");
        Assert.True(await Make(Release("v0.1.9", withExe: false)).Updater.CheckAsync(true) is null, "exe not attached yet");
        Assert.True(await Make(Release("nightly")).Updater.CheckAsync(true) is null, "tag that isn't a version");
    }

    [Test]
    public static async Task DevBuildIsOfferedTheReleaseOfTheSameNumber()
    {
        var (updater, _) = Make(Release("v0.1.5", digest: NewExeHash), current: "0.1.5-dev");
        Assert.True(await updater.CheckAsync(true) is not null, "0.1.5 is newer than 0.1.5-dev");
    }

    [Test]
    public static async Task DownloadsAndInstallsInPlaceKeepingTheOldExe()
    {
        var (updater, folder) = Make(Release("v0.1.7", digest: NewExeHash));
        var update = (await updater.CheckAsync(true))!;
        string downloaded = await updater.DownloadAsync(update);
        updater.Install(downloaded);

        Assert.Equal("new marina exe", File.ReadAllText(updater.ExePath), "new exe in place");
        Assert.Equal("old marina exe", File.ReadAllText(SelfUpdater.OldPath(updater.ExePath)), "old exe kept for now");
        Assert.True(!File.Exists(downloaded), "download moved, not copied");

        SelfUpdater.CleanUp(updater.ExePath);
        Assert.True(!File.Exists(SelfUpdater.OldPath(updater.ExePath)), "old exe cleaned up");
        Directory.Delete(folder, true);
    }

    [Test]
    public static async Task UsesTheChecksumFileWhenGitHubGivesNoDigest()
    {
        var (updater, folder) = Make(Release("v0.1.7", digest: null, withChecksum: true));
        var update = (await updater.CheckAsync(true))!;
        string downloaded = await updater.DownloadAsync(update);
        Assert.Equal("new marina exe", File.ReadAllText(downloaded), "downloaded");
        Directory.Delete(folder, true);
    }

    [Test]
    public static async Task RefusesADownloadThatDoesntMatchItsChecksum()
    {
        var (updater, folder) = Make(Release("v0.1.7", digest: new string('0', 64)));
        var update = (await updater.CheckAsync(true))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(update));
        Assert.True(!File.Exists(SelfUpdater.DownloadPath(updater.ExePath)), "bad download deleted");
        Assert.Equal("old marina exe", File.ReadAllText(updater.ExePath), "current exe untouched");
        Directory.Delete(folder, true);
    }

    [Test]
    public static async Task RefusesAnUpdateWithNoChecksumAtAll()
    {
        var (updater, folder) = Make(Release("v0.1.7", digest: null));
        var update = (await updater.CheckAsync(true))!;
        await Assert.ThrowsAsync<UnverifiedUpdateException>(() => updater.DownloadAsync(update));
        Directory.Delete(folder, true);
    }

    [Test]
    public static void InstallPutsTheOldExeBackIfTheNewOneCantBeMoved()
    {
        var (updater, folder) = Make(Release("v0.1.7"));
        Assert.Throws<FileNotFoundException>(() => updater.Install(Path.Combine(folder, "missing.exe")));
        Assert.Equal("old marina exe", File.ReadAllText(updater.ExePath), "old exe restored");
        Directory.Delete(folder, true);
    }
}
