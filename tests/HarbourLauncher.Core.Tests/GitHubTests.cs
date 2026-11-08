using System.Net;
using HarbourLauncher.Core.GitHub;

namespace HarbourLauncher.Core.Tests;

public static class GitHubTests
{
    private const string ReleaseJson = """
        {
          "tag_name": "9.3.1",
          "name": "Dewey Bravo",
          "published_at": "2026-10-19T14:10:00Z",
          "html_url": "https://github.com/HarbourMasters/Shipwright/releases/tag/9.3.1",
          "assets": [
            { "name": "SoH-Dewey-Bravo-Win64.zip", "size": 1234,
              "browser_download_url": "https://example.invalid/win.zip",
              "digest": "sha256:ABCDEF" },
            { "name": "SoH-Dewey-Bravo.appimage", "size": 99,
              "browser_download_url": "https://example.invalid/linux", "digest": null }
          ]
        }
        """;

    /// <summary>Replays scripted responses and records the requests it saw.</summary>
    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _next;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(script[_next++](request));
        }
    }

    private static HttpResponseMessage Ok()
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson) };
        r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
        return r;
    }

    private static HttpResponseMessage RateLimited()
    {
        var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
        r.Headers.Add("x-ratelimit-remaining", "0");
        r.Headers.Add("x-ratelimit-reset", "1800000000");
        return r;
    }

    private static ReleaseCache TempCache() =>
        new(Path.Combine(Path.GetTempPath(), "hl-cache-" + Guid.NewGuid().ToString("N") + ".json"));

    [Test]
    public static void ParsesTagAssetsAndDigest()
    {
        var release = GitHubReleaseClient.Parse("HarbourMasters/Shipwright", ReleaseJson);
        Assert.Equal("9.3.1", release.Tag, "tag");
        Assert.Equal(2, release.Assets.Count, "asset count");
        Assert.Equal("ABCDEF", release.Assets[0].Sha256, "sha256 from digest");
        Assert.Equal<string?>(null, release.Assets[1].Sha256, "missing digest");
    }

    [Test]
    public static async Task SecondCheckSendsETagAndUsesCacheOnNotModified()
    {
        var handler = new FakeHandler(_ => Ok(), _ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var client = new GitHubReleaseClient(new HttpClient(handler), TempCache());

        await client.GetLatestReleaseAsync("HarbourMasters/Shipwright");
        var second = await client.GetLatestReleaseAsync("HarbourMasters/Shipwright", force: true);

        Assert.True(handler.Requests[1].Headers.IfNoneMatch.ToString().Contains("\"v1\""), "conditional request sent");
        Assert.Equal("9.3.1", second.Tag, "tag from cache");
        Assert.True(!second.FromStaleCache, "304 means the cache is current, not stale");
    }

    [Test]
    public static async Task RateLimitFallsBackToCacheAndSaysSo()
    {
        var handler = new FakeHandler(_ => Ok(), _ => RateLimited());
        var client = new GitHubReleaseClient(new HttpClient(handler), TempCache());

        await client.GetLatestReleaseAsync("HarbourMasters/Shipwright");
        var second = await client.GetLatestReleaseAsync("HarbourMasters/Shipwright", force: true);

        Assert.Equal("9.3.1", second.Tag, "tag from cache");
        Assert.True(second.FromStaleCache, "marked stale");
        Assert.True(second.StaleReason?.Contains("hourly limit") == true, $"says why: {second.StaleReason}");
    }

    [Test]
    public static async Task RateLimitWithoutCacheReportsResetTime()
    {
        var client = new GitHubReleaseClient(new HttpClient(new FakeHandler(_ => RateLimited())), TempCache());
        var e = await Assert.ThrowsAsync<GitHubRateLimitException>(
            () => client.GetLatestReleaseAsync("HarbourMasters/Shipwright"));
        Assert.True(e.ResetsAt is not null, "reset time read from headers");
    }

    [Test]
    public static async Task TokenIsSentWhenConfigured()
    {
        var handler = new FakeHandler(_ => Ok());
        var client = new GitHubReleaseClient(new HttpClient(handler), TempCache(), token: "abc");
        await client.GetLatestReleaseAsync("HarbourMasters/Shipwright");
        Assert.Equal("Bearer abc", handler.Requests[0].Headers.Authorization?.ToString(), "auth header");
    }

    [Test]
    public static async Task RecentlyConfirmedReleaseIsReusedWithoutAskingGitHub()
    {
        var handler = new FakeHandler(_ => Ok()); // Only one response scripted: a second request would fail.
        var client = new GitHubReleaseClient(new HttpClient(handler), TempCache());

        await client.GetLatestReleaseAsync("HarbourMasters/Shipwright");
        var again = await client.GetLatestReleaseAsync("HarbourMasters/Shipwright");

        Assert.Equal(1, handler.Requests.Count, "one request for two checks");
        Assert.Equal("9.3.1", again.Tag, "from cache");
        Assert.True(!again.FromStaleCache && again.CheckedAt is not null, "fresh, with when it was confirmed");
    }

    [Test]
    public static async Task OldConfirmationIsCheckedAgain()
    {
        var handler = new FakeHandler(_ => Ok(), _ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var client = new GitHubReleaseClient(new HttpClient(handler), TempCache(), freshFor: TimeSpan.Zero);

        await client.GetLatestReleaseAsync("HarbourMasters/Shipwright");
        await client.GetLatestReleaseAsync("HarbourMasters/Shipwright");

        Assert.Equal(2, handler.Requests.Count, "asked again once the hour is up");
    }

    [Test]
    public static async Task ServerErrorsAndTimeoutsFallBackToTheCache()
    {
        var handler = new FakeHandler(_ => Ok(),
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => throw new TaskCanceledException("timed out"));
        var client = new GitHubReleaseClient(new HttpClient(handler), TempCache());

        await client.GetLatestReleaseAsync("HarbourMasters/Shipwright");
        var bad = await client.GetLatestReleaseAsync("HarbourMasters/Shipwright", force: true);
        var slow = await client.GetLatestReleaseAsync("HarbourMasters/Shipwright", force: true);

        Assert.True(bad.FromStaleCache && bad.StaleReason!.Contains("502"), $"502: {bad.StaleReason}");
        Assert.True(slow.FromStaleCache && slow.StaleReason!.Contains("in time"), $"timeout: {slow.StaleReason}");
    }

    [Test]
    public static async Task CachesFromBeforeThisChangeAreCheckedOnce()
    {
        string path = Path.Combine(Path.GetTempPath(), "hl-cache-" + Guid.NewGuid().ToString("N") + ".json");
        new ReleaseCache(path).Set("HarbourMasters/Shipwright", new ReleaseCache.Entry("\"v1\"", ReleaseJson)); // No CheckedAt.
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var client = new GitHubReleaseClient(new HttpClient(handler), new ReleaseCache(path));

        await client.GetLatestReleaseAsync("HarbourMasters/Shipwright");
        await client.GetLatestReleaseAsync("HarbourMasters/Shipwright");

        Assert.Equal(1, handler.Requests.Count, "checked once, then reused");
    }
}
