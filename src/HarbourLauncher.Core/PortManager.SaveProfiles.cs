using HarbourLauncher.Core.Catalog;
using HarbourLauncher.Core.Launch;
using HarbourLauncher.Core.Saves;

namespace HarbourLauncher.Core;

/// <summary>
/// Save profiles: several separate sets of saves for one game (a main playthrough,
/// a randomizer, a challenge run). The game only ever reads one save folder, so
/// switching profiles moves the current saves into .marina/profiles/ and moves the
/// chosen profile's saves into their place. Settings are shared by every profile.
/// </summary>
public sealed partial class PortManager
{
    /// <summary>Every save profile for the game, the active one first.</summary>
    public IReadOnlyList<SaveProfileInfo> GetSaveProfiles(PortDefinition port)
    {
        string folder = InstallFolder(port);
        var profiles = SaveProfiles.Load(folder);
        return profiles.Profiles
            .Select(p =>
            {
                bool active = p.Id == profiles.ActiveId;
                var entries = active
                    ? FindSaveEntries(port, folder)
                    : EntriesIn(SaveProfiles.ProfileFolder(folder, p.Id));
                var (bytes, changed) = Measure(entries);
                return new SaveProfileInfo(p, active, bytes, changed);
            })
            .OrderByDescending(p => p.IsActive)
            .ToList();
    }

    /// <summary>The profile the game is using now.</summary>
    public SaveProfile GetActiveSaveProfile(PortDefinition port) => SaveProfiles.Load(InstallFolder(port)).Active;

    /// <summary>
    /// Adds a profile without switching to it. It starts empty (the game starts
    /// fresh, as on a new install) or as a copy of the active profile's saves.
    /// </summary>
    public SaveProfile CreateSaveProfile(PortDefinition port, string name, bool copyActive)
    {
        string folder = RequireInstalled(port);
        var profiles = SaveProfiles.Load(folder);
        string trimmed = profiles.CheckName(name);
        var profile = new SaveProfile(NewProfileId(folder), trimmed, DateTimeOffset.UtcNow);

        string store = SaveProfiles.ProfileFolder(folder, profile.Id);
        Directory.CreateDirectory(store);
        try
        {
            if (copyActive)
            {
                if (GameLauncher.IsRunning(port, folder))
                    throw new InvalidOperationException($"Close {port.Name} before copying its saves, so the copy is complete.");
                foreach (string entry in FindSaveEntries(port, folder))
                    CopyEntry(entry, Path.Combine(store, Path.GetFileName(entry)));
            }
            (profiles with { Profiles = [.. profiles.Profiles, profile] }).Save(folder);
        }
        catch
        {
            TryDelete(store);
            throw;
        }
        return profile;
    }

    /// <summary>
    /// Makes another profile the one the game uses. The game must be closed:
    /// it writes its saves when it exits, which would land in the wrong profile.
    /// If anything fails part-way, every move is undone.
    /// </summary>
    public void SwitchSaveProfile(PortDefinition port, string id)
    {
        string folder = RequireInstalled(port);
        var profiles = SaveProfiles.Load(folder);
        var target = profiles.Profiles.FirstOrDefault(p => p.Id == id)
                     ?? throw new InvalidOperationException("That save profile no longer exists.");
        if (target.Id == profiles.ActiveId) return;
        if (GameLauncher.IsRunning(port, folder))
            throw new InvalidOperationException($"Close {port.Name} before switching save profiles.");

        string outgoing = SaveProfiles.ProfileFolder(folder, profiles.ActiveId);
        string incoming = SaveProfiles.ProfileFolder(folder, target.Id);
        if (Directory.Exists(outgoing) && Directory.EnumerateFileSystemEntries(outgoing).Any())
            throw new IOException($"Marina's copy of {profiles.Active.Name} isn't empty, so switching would mix saves. " +
                                  $"Look in {outgoing}.");

        var undo = new Stack<Action>();
        try
        {
            // 1. Put the current saves away.
            Directory.CreateDirectory(outgoing);
            foreach (string entry in FindSaveEntries(port, folder))
            {
                string stored = Path.Combine(outgoing, Path.GetFileName(entry));
                MoveEntry(entry, stored);
                undo.Push(() => MoveEntry(stored, entry));
            }

            // 2. Bring the chosen profile's saves in.
            foreach (string stored in EntriesIn(incoming))
            {
                string entry = Path.Combine(folder, Path.GetFileName(stored));
                if (File.Exists(entry) || Directory.Exists(entry))
                    throw new IOException($"{Path.GetFileName(entry)} is still in the game folder. Close anything using it and try again.");
                MoveEntry(stored, entry);
                undo.Push(() => MoveEntry(entry, stored));
            }

            (profiles with { ActiveId = target.Id }).Save(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            while (undo.Count > 0)
            {
                try { undo.Pop()(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            throw new IOException($"Couldn't switch to {target.Name}, so your saves were left as they were. {e.Message}", e);
        }

        TryDelete(incoming); // Empty now; the profile's saves are in the game folder.
    }

    public void RenameSaveProfile(PortDefinition port, string id, string name)
    {
        string folder = RequireInstalled(port);
        var profiles = SaveProfiles.Load(folder);
        if (profiles.Profiles.All(p => p.Id != id))
            throw new InvalidOperationException("That save profile no longer exists.");
        string trimmed = profiles.CheckName(name, exceptId: id);
        (profiles with { Profiles = profiles.Profiles.Select(p => p.Id == id ? p with { Name = trimmed } : p).ToList() })
            .Save(folder);
    }

    /// <summary>Deletes a profile and its saves for good. The active profile can't be deleted; switch away first.</summary>
    public void DeleteSaveProfile(PortDefinition port, string id)
    {
        string folder = RequireInstalled(port);
        var profiles = SaveProfiles.Load(folder);
        if (id == profiles.ActiveId)
            throw new InvalidOperationException("Switch to another profile before deleting this one.");
        if (profiles.Profiles.All(p => p.Id != id)) return;

        string store = SaveProfiles.ProfileFolder(folder, id);
        if (Directory.Exists(store))
        {
            foreach (var f in Directory.EnumerateFiles(store, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(store, recursive: true);
        }
        (profiles with { Profiles = profiles.Profiles.Where(p => p.Id != id).ToList() }).Save(folder);
    }

    // ---- Helpers ----

    private string RequireInstalled(PortDefinition port) =>
        GetInstalled(port) is null ? throw new InvalidOperationException($"{port.Name} isn't installed.") : InstallFolder(port);

    /// <summary>The save files/folders that exist at the top of the game folder, with their real names.</summary>
    private static List<string> FindSaveEntries(PortDefinition port, string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var wanted = new HashSet<string>(port.Saves, StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFileSystemEntries(folder)
            .Where(e => wanted.Contains(Path.GetFileName(e)))
            .ToList();
    }

    private static List<string> EntriesIn(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder).ToList() : [];

    private static (long Bytes, DateTimeOffset? LastChanged) Measure(IEnumerable<string> entries)
    {
        long bytes = 0;
        DateTime? latest = null;
        foreach (string entry in entries)
        {
            var files = Directory.Exists(entry)
                ? Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f))
                : [new FileInfo(entry)];
            foreach (var f in files)
            {
                bytes += f.Length;
                if (latest is null || f.LastWriteTimeUtc > latest) latest = f.LastWriteTimeUtc;
            }
        }
        return (bytes, latest is { } l ? new DateTimeOffset(l, TimeSpan.Zero) : null);
    }

    private static string NewProfileId(string folder)
    {
        string id;
        do id = Guid.NewGuid().ToString("N")[..8];
        while (Directory.Exists(SaveProfiles.ProfileFolder(folder, id)));
        return id;
    }

    private static void MoveEntry(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private static void CopyEntry(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            File.Copy(from, to);
            return;
        }
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        Directory.CreateDirectory(to); // Keep an empty save folder empty rather than missing.
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
