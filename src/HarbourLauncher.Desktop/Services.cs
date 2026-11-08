using HarbourLauncher.Core;
using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.GitHub;
using HarbourLauncher.Core.Install;

namespace HarbourLauncher.Desktop;

/// <summary>Builds the PortManager the same way the CLI does.</summary>
internal static class Services
{
    public static PortManager CreateManager()
    {
        var paths = LauncherPaths.Default();
        var http = new HttpClient();
        return new PortManager(
            PortCatalog.LoadBuiltIn(),
            new GitHubReleaseClient(
                http,
                new ReleaseCache(paths.ReleaseCacheFile),
                Environment.GetEnvironmentVariable("GITHUB_TOKEN")),
            new AssetDownloader(http),
            new PortInstaller(),
            paths);
    }
}
