using HarbourLauncher.Core.Install;

namespace HarbourLauncher.Core.GitHub;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256);

public sealed record ReleaseInfo(
    string Repo,
    string Tag,
    string Name,
    DateTimeOffset? PublishedAt,
    string HtmlUrl,
    IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>
    /// True when this came from the local cache because GitHub could not be
    /// reached or refused the request (for example, rate limiting).
    /// </summary>
    public bool FromStaleCache { get; init; }

    /// <summary>Why the cached release was used, in words for the player (when <see cref="FromStaleCache"/>).</summary>
    public string? StaleReason { get; init; }

    /// <summary>When GitHub last confirmed this was the latest release.</summary>
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>Finds the single asset matching <paramref name="pattern"/>.</summary>
    public ReleaseAsset FindAsset(string pattern)
    {
        var matches = Assets.Where(a => Glob.IsMatch(pattern, a.Name)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new AssetNotFoundException(
                $"Release {Tag} of {Repo} has no asset matching '{pattern}'. " +
                $"Assets: {string.Join(", ", Assets.Select(a => a.Name))}."),
            _ => throw new AssetNotFoundException(
                $"Release {Tag} of {Repo} has several assets matching '{pattern}': " +
                $"{string.Join(", ", matches.Select(a => a.Name))}. Make the pattern more specific."),
        };
    }
}

public sealed class AssetNotFoundException(string message) : Exception(message);

public sealed class GitHubRateLimitException(DateTimeOffset? resetsAt)
    : Exception(resetsAt is { } t
        ? $"GitHub's request limit was reached. It resets at {t.ToLocalTime():t}."
        : "GitHub's request limit was reached. Try again in a few minutes.")
{
    public DateTimeOffset? ResetsAt { get; } = resetsAt;
}
