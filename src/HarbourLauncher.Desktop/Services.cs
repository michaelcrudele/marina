using HarbourLauncher.Core;
using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;
using HarbourLauncher.Core.SelfUpdate;

namespace HarbourLauncher.Desktop;

/// <summary>Builds the PortManager the same way the CLI does, plus Marina's own updater.</summary>
internal static class Services
{
    public static (PortManager Manager, SelfUpdater? Updater) Create()
    {
        var paths = LauncherPaths.Default();
        var http = new HttpClient();
        // One client (and so one cache file) for the games and for Marina itself.
        var releases = new GitHubReleaseClient(
            http,
            new ReleaseCache(paths.ReleaseCacheFile),
            Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        var downloader = new AssetDownloader(http);

        var manager = new PortManager(PortCatalog.LoadBuiltIn(), releases, downloader, new PortInstaller(), paths);
        var updater = Environment.ProcessPath is { } exe && AppVersion.TryParse(AppInfo.Version, out var current)
            ? new SelfUpdater(releases, downloader, AppInfo.Repo, current, exe)
            : null;
        return (manager, updater);
    }
}
