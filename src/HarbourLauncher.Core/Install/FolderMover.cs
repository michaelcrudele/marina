namespace HarbourLauncher.Core.Install;

/// <summary>Moves a game's folder, including across drives.</summary>
internal static class FolderMover
{
    /// <param name="forceCopy">Copy-then-delete even on one drive (as across drives). For tests.</param>
    public static void Move(string from, string to, bool forceCopy = false)
    {
        from = Path.GetFullPath(from);
        to = Path.GetFullPath(to);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(to)) Directory.Delete(to); // Only ever called on an empty folder.

        if (!forceCopy && string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (IOException) when (!Directory.Exists(to))
            {
                // Same drive letter but a different volume (e.g. a mount point); copy instead.
            }
        }

        try
        {
            Copy(from, to);
        }
        catch
        {
            // Leave the original alone and remove the partial copy.
            try { Directory.Delete(to, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        Directory.Delete(from, recursive: true);
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    public static bool IsEmptyOrMissing(string folder) =>
        !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any();

    public static bool IsSameOrInside(string folder, string parent)
    {
        string f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return string.Equals(f, p, StringComparison.OrdinalIgnoreCase)
               || f.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
