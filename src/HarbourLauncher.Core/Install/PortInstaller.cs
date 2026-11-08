using System.IO.Compression;
using HarbourLauncher.Core.Catalog;

namespace HarbourLauncher.Core.Install;

public sealed record InstallResult(
    string Tag,
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Kept,
    IReadOnlyList<string> Removed,
    string BackupFolder);

/// <summary>
/// Installs or updates a port from its release zip.
///
/// The rule that keeps user data safe: the installer only ever writes files
/// that are in the release zip, and only ever removes files that the
/// previous release installed. Saves, the archive generated from the user's
/// ROM, mods and settings are never in the release zip, so they are never
/// touched. Every file that is replaced or removed is first moved into
/// .marina/backup, and any failure rolls the folder back.
/// </summary>
public sealed class PortInstaller
{
    private const string StagingFolder = "staging";
    private const string BackupFolder = "backup";

    public InstallResult Install(PortDefinition port, string zipPath, string tag, string assetName, string installDir)
    {
        string root = Path.GetFullPath(installDir);
        Directory.CreateDirectory(root);
        string meta = Path.Combine(root, InstallState.MetaFolder);
        string staging = Path.Combine(meta, StagingFolder);
        string backup = Path.Combine(meta, BackupFolder);

        DeleteFolder(staging);
        try
        {
            var files = ExtractToStaging(port, zipPath, root, staging);
            return Apply(port, tag, assetName, root, staging, backup, files);
        }
        finally
        {
            DeleteFolder(staging);
        }
    }

    /// <summary>Extracts the release into staging. The install folder is not touched.</summary>
    private static List<string> ExtractToStaging(PortDefinition port, string zipPath, string root, string staging)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                continue; // Folder entry.

            string rel = Glob.NormalizePath(entry.FullName);
            if (rel.StartsWith(InstallState.MetaFolder + "/", StringComparison.OrdinalIgnoreCase)) continue;
            if (Glob.MatchesAny(port.Skip, rel)) continue;
            if (!seen.Add(rel)) continue;

            EnsureInside(root, rel); // Rejects "../" and absolute paths (zip-slip).
            string dest = Path.Combine(staging, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
            files.Add(rel);
        }

        string exe = Glob.NormalizePath(port.Exe);
        if (!files.Any(f => string.Equals(f, exe, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(
                $"The release zip for {port.Name} does not contain {port.Exe}. " +
                "The release layout may have changed; check the port's entry in ports.json.");

        return files;
    }

    private static InstallResult Apply(
        PortDefinition port, string tag, string assetName,
        string root, string staging, string backup, List<string> files)
    {
        var previous = InstallState.Load(root);
        DeleteFolder(backup);
        Directory.CreateDirectory(backup);

        var undo = new Stack<Action>();
        var written = new List<string>();
        var kept = new List<string>();
        var removed = new List<string>();

        try
        {
            foreach (string rel in files)
            {
                string target = Path.Combine(root, rel);
                if (File.Exists(target))
                {
                    if (Glob.MatchesAny(port.Keep, rel))
                    {
                        kept.Add(rel);
                        continue;
                    }
                    MoveToBackup(target, Path.Combine(backup, rel), undo);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(Path.Combine(staging, rel), target);
                undo.Push(() => File.Delete(target));
                written.Add(rel);
            }

            if (previous is not null)
            {
                var current = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                foreach (string rel in previous.Files)
                {
                    if (current.Contains(rel) || Glob.MatchesAny(port.Keep, rel)) continue;
                    EnsureInside(root, rel);
                    string target = Path.Combine(root, rel);
                    if (!File.Exists(target)) continue;
                    MoveToBackup(target, Path.Combine(backup, rel), undo);
                    removed.Add(rel);
                }
            }

            new InstallState(port.Id, tag, assetName, DateTimeOffset.UtcNow, files).Save(root);
        }
        catch
        {
            Rollback(undo);
            throw;
        }

        return new InstallResult(tag, written, kept, removed, backup);
    }

    private static void MoveToBackup(string target, string backupPath, Stack<Action> undo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Move(target, backupPath, overwrite: true);
        undo.Push(() => File.Move(backupPath, target, overwrite: true));
    }

    private static void Rollback(Stack<Action> undo)
    {
        while (undo.TryPop(out var step))
        {
            try { step(); }
            catch { /* Keep going; restore as much as possible. */ }
        }
    }

    internal static void EnsureInside(string root, string rel)
    {
        string full = Path.GetFullPath(Path.Combine(root, rel));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Refusing to write outside the install folder: '{rel}'.");
    }

    private static void DeleteFolder(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
