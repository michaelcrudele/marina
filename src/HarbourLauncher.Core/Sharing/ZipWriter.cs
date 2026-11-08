using System.IO.Compression;
using System.Text.Json;

namespace HarbourLauncher.Core.Sharing;

/// <summary>Writes .marinapack and .marinabackup files: a JSON manifest plus files, with progress and cancel.</summary>
internal static class ZipWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Writes to a temporary file and renames it at the end, so a stopped or
    /// failed write never leaves a half-finished file. Already-compressed files
    /// (texture packs, images) are stored as-is, which makes multi-gigabyte
    /// files far faster to create. Progress is 0.0 to 1.0 by bytes written.
    /// </summary>
    public static void Write<TManifest>(string destination, string manifestName, TManifest manifest,
                                        IReadOnlyList<(string Source, string Name)> files,
                                        IProgress<double>? progress, CancellationToken ct)
    {
        long total = Math.Max(1, files.Sum(f => new FileInfo(f.Source).Length));
        long done = 0;
        var buffer = new byte[1024 * 1024];

        string temp = destination + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(manifestName, CompressionLevel.Optimal);
                using (var writer = entry.Open())
                    JsonSerializer.Serialize(writer, manifest, JsonOptions);

                foreach (var (source, name) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var level = ShareRules.IsAlreadyCompressed(name) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                    var zipEntry = zip.CreateEntry(name, level);
                    zipEntry.LastWriteTime = File.GetLastWriteTime(source);
                    using var input = File.OpenRead(source);
                    using var output = zipEntry.Open();
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        done += read;
                        progress?.Report((double)done / total);
                    }
                }
            }
            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }
}
