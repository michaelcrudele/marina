using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HarbourLauncher.Core.GitHub;

/// <summary>
/// Reads the latest release of a repository from the GitHub REST API.
///
/// Without a token, GitHub allows 60 requests per hour per IP address, shared
/// by everything on that network. Every check of every game is one request
/// (conditional requests answered "not modified" still count unless a token is
/// used), so a release confirmed less than <see cref="FreshFor"/> ago is reused
/// without asking GitHub at all, unless the check is forced (the "Check for
/// updates" button, or just before installing). If GitHub can't be reached or
/// refuses, the cached release is returned, marked stale with the reason.
/// </summary>
public sealed class GitHubReleaseClient
{
    private const string ApiBase = "https://api.github.com";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly ReleaseCache _cache;
    private readonly string? _token;

    public GitHubReleaseClient(HttpClient http, ReleaseCache cache, string? token = null, TimeSpan? freshFor = null)
    {
        _http = http;
        _cache = cache;
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
        FreshFor = freshFor ?? TimeSpan.FromHours(1);
    }

    /// <summary>How long a confirmed release is reused before GitHub is asked again.</summary>
    public TimeSpan FreshFor { get; }

    /// <param name="force">Ask GitHub even if the cached release was confirmed recently.</param>
    public async Task<ReleaseInfo> GetLatestReleaseAsync(string repo, bool force = false, CancellationToken ct = default)
    {
        var cached = _cache.Get(repo);
        if (!force && cached?.CheckedAt is { } checkedAt && DateTimeOffset.UtcNow - checkedAt < FreshFor)
            return Parse(repo, cached.Json) with { CheckedAt = checkedAt };

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/repos/{repo}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Marina", "0.1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (_token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (cached?.ETag is { } etag)
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (cached is not null)
        {
            return Stale(repo, cached, "Couldn't connect to GitHub");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && cached is not null)
        {
            return Stale(repo, cached, "GitHub didn't answer in time");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("GitHub didn't answer in time.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                var now = DateTimeOffset.UtcNow;
                _cache.Set(repo, cached with { CheckedAt = now });
                return Parse(repo, cached.Json) with { CheckedAt = now };
            }

            if (IsRateLimited(response))
            {
                var reset = ReadReset(response);
                if (cached is not null)
                    return Stale(repo, cached, reset is { } t
                        ? $"GitHub's hourly limit for update checks is used up until {t.ToLocalTime():t}"
                        : "GitHub's hourly limit for update checks is used up");
                throw new GitHubRateLimitException(reset);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new HttpRequestException($"{repo} has no published releases, or the repository does not exist.");

            if (!response.IsSuccessStatusCode)
            {
                if (cached is not null)
                    return Stale(repo, cached, $"GitHub had a problem ({(int)response.StatusCode})");
                response.EnsureSuccessStatusCode();
            }

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var confirmed = DateTimeOffset.UtcNow;
            var release = Parse(repo, json) with { CheckedAt = confirmed };
            _cache.Set(repo, new ReleaseCache.Entry(response.Headers.ETag?.ToString(), json, confirmed));
            return release;
        }
    }

    private static ReleaseInfo Stale(string repo, ReleaseCache.Entry cached, string reason) =>
        Parse(repo, cached.Json) with { FromStaleCache = true, StaleReason = reason, CheckedAt = cached.CheckedAt };

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests ||
        (response.StatusCode == HttpStatusCode.Forbidden &&
         response.Headers.TryGetValues("x-ratelimit-remaining", out var values) &&
         values.FirstOrDefault() == "0");

    private static DateTimeOffset? ReadReset(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-ratelimit-reset", out var values) &&
        long.TryParse(values.FirstOrDefault(), out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    internal static ReleaseInfo Parse(string repo, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var assets = new List<ReleaseAsset>();
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            // GitHub reports a digest such as "sha256:abc..." for release assets.
            string? sha256 = a.TryGetProperty("digest", out var d) && d.GetString() is { } digest &&
                             digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest["sha256:".Length..]
                : null;
            assets.Add(new ReleaseAsset(
                a.GetProperty("name").GetString()!,
                a.GetProperty("browser_download_url").GetString()!,
                a.GetProperty("size").GetInt64(),
                sha256));
        }

        return new ReleaseInfo(
            repo,
            root.GetProperty("tag_name").GetString()!,
            root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "",
            root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetDateTimeOffset()
                : null,
            root.GetProperty("html_url").GetString()!,
            assets);
    }
}
