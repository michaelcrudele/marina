using HarbourLauncher.Core.GitHub;

namespace HarbourLauncher.Core.SelfUpdate;

/// <summary>A newer Marina found on GitHub, with the files needed to install it.</summary>
public sealed record AppUpdate(AppVersion Version, ReleaseInfo Release, ReleaseAsset Exe, ReleaseAsset? Checksum);

/// <summary>The download had no checksum to check it against, so it wasn't installed.</summary>
public sealed class UnverifiedUpdateException()
    : Exception("This update couldn't be verified, so it wasn't installed. Download it from GitHub instead.");

/// <summary>
/// Keeps Marina itself up to date from its GitHub releases.
///
/// Windows won't let a running .exe be overwritten, but it can be renamed. So an
/// update downloads the new exe next to the current one, checks its size and
/// SHA-256, renames the running exe to Marina.exe.old, and moves the new one into
/// its place. The new exe is started on restart, and the .old file is deleted the
/// next time Marina starts (see <see cref="CleanUp"/>).
/// </summary>
public sealed class SelfUpdater(
    GitHubReleaseClient releases, AssetDownloader downloader, string repo, AppVersion current, string exePath)
{
    /// <summary>The release asset that holds the app.</summary>
    public const string ExeAssetName = "Marina.exe";

    public AppVersion Current { get; } = current;
    public string ExePath { get; } = exePath;
    public string Repo { get; } = repo;

    public static string OldPath(string exePath) => exePath + ".old";
    public static string DownloadPath(string exePath) => exePath + ".download";

    /// <summary>
    /// Returns the latest release if it's newer than this copy and has Marina.exe
    /// attached; otherwise null. A release whose exe is still being built and
    /// attached counts as not there yet.
    /// </summary>
    /// <param name="force">Ask GitHub now, even if the release was confirmed within the last hour.</param>
    public async Task<AppUpdate?> CheckAsync(bool force = false, CancellationToken ct = default)
    {
        var release = await releases.GetLatestReleaseAsync(Repo, force, ct).ConfigureAwait(false);
        if (!AppVersion.TryParse(release.Tag, out var latest) || latest <= Current) return null;

        var exe = release.Assets.FirstOrDefault(a => a.Name.Equals(ExeAssetName, StringComparison.OrdinalIgnoreCase));
        if (exe is null) return null;
        var checksum = release.Assets.FirstOrDefault(
            a => a.Name.Equals(ExeAssetName + ".sha256", StringComparison.OrdinalIgnoreCase));
        return new AppUpdate(latest, release, exe, checksum);
    }

    /// <summary>
    /// Downloads and verifies the new exe next to the current one. Returns where it was saved.
    /// Progress is reported from 0.0 to 1.0.
    /// </summary>
    public async Task<string> DownloadAsync(AppUpdate update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string target = DownloadPath(ExePath);
        try
        {
            var exe = update.Exe;
            if (exe.Sha256 is null && update.Checksum is not null)
                exe = exe with { Sha256 = await ReadChecksumAsync(update.Checksum, ct).ConfigureAwait(false) };
            if (exe.Sha256 is null)
                throw new UnverifiedUpdateException();

            await downloader.DownloadAsync(exe, target, progress, ct).ConfigureAwait(false);
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>Puts a downloaded exe in place of the running one. Rolls back if that fails half way.</summary>
    public void Install(string downloaded)
    {
        string old = OldPath(ExePath);
        TryDelete(old);
        File.Move(ExePath, old, overwrite: true);
        try
        {
            File.Move(downloaded, ExePath);
        }
        catch
        {
            File.Move(old, ExePath, overwrite: true);
            throw;
        }
    }

    /// <summary>Deletes what an earlier update left behind. Safe to call on every start.</summary>
    public static void CleanUp(string exePath)
    {
        TryDelete(OldPath(exePath));
        TryDelete(DownloadPath(exePath));
    }

    // Marina.exe.sha256 holds "<hash>  Marina.exe", as written by sha256sum.
    private async Task<string?> ReadChecksumAsync(ReleaseAsset checksum, CancellationToken ct)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"marina-{Guid.NewGuid():N}.sha256");
        try
        {
            await downloader.DownloadAsync(checksum, temp, null, ct).ConfigureAwait(false);
            string hash = (await File.ReadAllTextAsync(temp, ct).ConfigureAwait(false))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return hash.Length == 64 && hash.All(Uri.IsHexDigit) ? hash : null;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // Try again next time.
    }
}
