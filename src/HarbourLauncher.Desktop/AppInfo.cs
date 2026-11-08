using System.Reflection;

namespace HarbourLauncher.Desktop;

/// <summary>Marina's own version, as stamped into the exe at build time (see Directory.Build.targets).</summary>
public static class AppInfo
{
    static AppInfo()
    {
        // e.g. "0.1.42" or "0.1.42-dev+3f9c2e1a...": the part after '+' is the commit it was built from.
        var info = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = info.IndexOf('+');
        Version = plus >= 0 ? info[..plus] : info;
        var commit = plus >= 0 ? info[(plus + 1)..] : "";
        Commit = commit.Length > 7 ? commit[..7] : commit;
    }

    /// <summary>The version number, e.g. "0.1.42".</summary>
    public static string Version { get; }

    /// <summary>Short id of the commit this build came from, or empty if unknown.</summary>
    public static string Commit { get; }

    /// <summary>What the footer shows, e.g. "Marina v0.1.42".</summary>
    public static string VersionText => $"Marina v{Version}";

    /// <summary>The GitHub repository Marina's own releases come from.</summary>
    public const string Repo = "michaelcrudele/marina";

    /// <summary>
    /// True for the single-file Marina.exe that releases ship. A build run from
    /// its bin folder (e.g. with dotnet run) is an exe plus a pile of .dlls,
    /// which swapping one file can't update.
    /// </summary>
    public static bool IsSingleFile => Environment.ProcessPath is not null
        && !File.Exists(Path.Combine(AppContext.BaseDirectory, "Marina.dll"));

    /// <summary>Hover text for the footer.</summary>
    public static string VersionTip => Commit.Length > 0 ? $"Built from commit {Commit}" : "Version";
}
