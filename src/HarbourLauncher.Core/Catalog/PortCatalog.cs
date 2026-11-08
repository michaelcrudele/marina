using System.Text.Json;

namespace HarbourLauncher.Core.Catalog;

public sealed class PortCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public IReadOnlyList<PortDefinition> Ports { get; }

    public PortCatalog(IReadOnlyList<PortDefinition> ports)
    {
        var duplicate = ports.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                             .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Port id '{duplicate.Key}' appears more than once in the catalog.");
        Ports = ports;
    }

    public PortDefinition Get(string id) =>
        Ports.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException(
            $"Unknown port '{id}'. Known ports: {string.Join(", ", Ports.Select(p => p.Id))}.");

    public static PortCatalog Parse(string json)
    {
        var file = JsonSerializer.Deserialize<CatalogFile>(json, JsonOptions)
                   ?? throw new InvalidDataException("Port catalog is empty.");
        return new PortCatalog(file.Ports);
    }

    /// <summary>Loads the catalog bundled with the launcher.</summary>
    public static PortCatalog LoadBuiltIn()
    {
        using var stream = typeof(PortCatalog).Assembly.GetManifestResourceStream("ports.json")
                           ?? throw new InvalidOperationException("Built-in ports.json resource is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private sealed record CatalogFile(List<PortDefinition> Ports);
}
