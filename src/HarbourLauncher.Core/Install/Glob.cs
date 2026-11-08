using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace HarbourLauncher.Core.Install;

/// <summary>
/// Minimal, case-insensitive glob matching over forward-slash paths.
/// "*" matches within one path segment, "**" matches across segments,
/// "?" matches one character, and a trailing "/" means "this folder and
/// everything in it".
/// </summary>
public static class Glob
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    public static bool IsMatch(string pattern, string path) =>
        Cache.GetOrAdd(pattern, Compile).IsMatch(NormalizePath(path));

    public static bool MatchesAny(IEnumerable<string> patterns, string path) =>
        patterns.Any(p => IsMatch(p, path));

    public static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static Regex Compile(string pattern)
    {
        pattern = NormalizePath(pattern);
        if (pattern.EndsWith('/'))
            pattern += "**";

        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                bool followedBySlash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                sb.Append(followedBySlash ? "(?:.*/)?" : ".*");
                i += followedBySlash ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
