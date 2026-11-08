namespace HarbourLauncher.Core.Sharing;

/// <summary>
/// Windows marks files downloaded from the internet (the Zone.Identifier stream),
/// and warns before running them. A program installed from a downloaded .marinapack
/// keeps that mark, so Windows still asks before running it the first time.
/// Does nothing on other systems or drives that don't support it.
/// </summary>
internal static class MarkOfTheWeb
{
    private const string Stream = ":Zone.Identifier";

    public static string? Read(string file)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            string path = Path.GetFullPath(file) + Stream;
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public static void Write(string file, string zone)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            File.WriteAllText(Path.GetFullPath(file) + Stream, zone);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Not every drive supports it (e.g. FAT32 or some network shares).
        }
    }
}
