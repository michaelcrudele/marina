using System.Text.Json;

namespace HarbourLauncher.Core.GitHub;

/// <summary>Stores the last GitHub response per repository, with its ETag.</summary>
public sealed class ReleaseCache
{
    /// <param name="CheckedAt">When GitHub last confirmed this was the latest release (null in caches from older versions).</param>
    public sealed record Entry(string? ETag, string Json, DateTimeOffset? CheckedAt = null);

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, Entry> _entries;

    public ReleaseCache(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    public Entry? Get(string repo)
    {
        lock (_lock)
            return _entries.GetValueOrDefault(repo.ToLowerInvariant());
    }

    public void Set(string repo, Entry entry)
    {
        lock (_lock)
        {
            _entries[repo.ToLowerInvariant()] = entry;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
            File.Move(temp, _path, overwrite: true);
        }
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return []; // A corrupt cache only costs a fresh request.
        }
    }
}
