using System.IO.Compression;
using HarbourLauncher.Core.Catalog;

namespace HarbourLauncher.Core;

/// <summary>What adding a mod did: the mod files now in the mods folder, and which of them replaced a file of the same name.</summary>
public sealed record AddModResult(PortDefinition Port, IReadOnlyList<string> Added, IReadOnlyList<string> Replaced);

/// <summary>Adding mods to an installed game.</summary>
public sealed partial class PortManager
{
    /// <summary>Mod archives the ports load directly.</summary>
    private static readonly string[] ModArchiveExtensions = [".o2r", ".otr"];

    /// <summary>Files the "add mod" picker offers: mod archives, and .zip downloads that may hold them.</summary>
    public static IReadOnlyList<string> ModFileExtensions { get; } = [".o2r", ".otr", ".zip"];

    public string ModsFolder(PortDefinition port) => Path.Combine(InstallFolder(port), port.ModsFolder);

    /// <summary>
    /// Moves a mod into the game's mods folder, replacing a mod with the same file name.
    /// An .o2r or .otr is moved as-is. A .zip that holds .o2r/.otr files (how many mods are
    /// downloaded) has those extracted and the .zip is then deleted; any other .zip is moved
    /// as-is, since the ports can load a zipped mod directly.
    /// </summary>
    public AddModResult AddMod(PortDefinition port, string file)
    {
        if (GetInstalled(port) is null)
            throw new InvalidOperationException($"{port.Name} isn't installed.");
        if (!File.Exists(file))
            throw new FileNotFoundException($"Couldn't find {file}.", file);

        string ext = Path.GetExtension(file);
        if (!ModFileExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"{Path.GetFileName(file)} isn't a mod file. Mods are .o2r, .otr or .zip files.");

        string mods = ModsFolder(port);
        Directory.CreateDirectory(mods);

        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) && ExtractModsFromZip(port, file, mods) is { } extracted)
            return extracted;

        string name = Path.GetFileName(file);
        string target = Path.Combine(mods, name);
        if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{name} is already in {port.Name}'s mods folder.");

        bool replacing = File.Exists(target);
        File.Move(file, target, overwrite: true);
        return new AddModResult(port, [name], replacing ? [name] : []);
    }

    /// <summary>Extracts the .o2r/.otr files in a .zip into the mods folder and deletes the .zip; null if it holds none.</summary>
    private static AddModResult? ExtractModsFromZip(PortDefinition port, string zipFile, string mods)
    {
        List<string> added = [];
        List<string> replaced = [];
        using (var zip = ZipFile.OpenRead(zipFile))
        {
            var entries = zip.Entries
                .Where(e => e.Name.Length > 0 && ModArchiveExtensions.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (entries.Count == 0) return null;

            var duplicate = entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
                throw new InvalidDataException(
                    $"{Path.GetFileName(zipFile)} has more than one {duplicate.Key} in it. Unzip it and add the one you want.");

            foreach (var entry in entries)
            {
                // Only the file name is used, so nothing in the zip can be written outside the mods folder.
                string target = Path.Combine(mods, entry.Name);
                string temp = target + ".marina-tmp";
                try
                {
                    entry.ExtractToFile(temp, overwrite: true);
                    if (File.Exists(target)) replaced.Add(entry.Name);
                    File.Move(temp, target, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                added.Add(entry.Name);
            }
        }

        File.Delete(zipFile); // Its mods are now in the mods folder.
        return new AddModResult(port, added, replaced);
    }
}
