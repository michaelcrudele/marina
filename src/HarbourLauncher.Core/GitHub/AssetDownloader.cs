using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace HarbourLauncher.Core.GitHub;

public sealed class AssetDownloader(HttpClient http)
{
    /// <summary>
    /// Downloads <paramref name="asset"/> to <paramref name="destination"/>,
    /// then checks its size and, when GitHub published one, its SHA-256.
    /// Progress is reported from 0.0 to 1.0.
    /// </summary>
    public async Task DownloadAsync(
        ReleaseAsset asset, string destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Marina", "0.1"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? asset.Size;
        using var sha = SHA256.Create();
        long received = 0;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                                                 bufferSize: 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sha.TransformBlock(buffer, 0, read, null, 0);
                received += read;
                if (total > 0) progress?.Report((double)received / total);
            }
        }
        sha.TransformFinalBlock([], 0, 0);

        if (asset.Size > 0 && received != asset.Size)
            throw new InvalidDataException(
                $"Download of {asset.Name} was incomplete ({received:N0} of {asset.Size:N0} bytes).");

        if (asset.Sha256 is { } expected &&
            !string.Equals(Convert.ToHexString(sha.Hash!), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{asset.Name} failed its checksum check. Try downloading again.");
    }
}
