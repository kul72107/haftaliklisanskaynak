using System.Net.Http.Json;
using System.Security.Cryptography;

namespace ModernYedek.Core.Updates;

public sealed class UpdateClient
{
    private readonly HttpClient _httpClient;

    public UpdateClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string manifestUrl,
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return new UpdateCheckResult { Message = "Guncelleme manifest URL bos." };
        }

        var manifest = await _httpClient.GetFromJsonAsync<UpdateManifest>(manifestUrl, cancellationToken);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Url))
        {
            return new UpdateCheckResult { Message = "Guncelleme manifest dosyasi gecersiz." };
        }

        var latestVersion = ParseVersion(manifest.Version);
        if (latestVersion <= NormalizeVersion(currentVersion))
        {
            return new UpdateCheckResult { Message = $"Uygulama guncel: {currentVersion}" };
        }

        return new UpdateCheckResult
        {
            HasUpdate = true,
            Manifest = manifest,
            Message = $"Yeni surum bulundu: {manifest.Version}"
        };
    }

    public async Task<string> DownloadAndVerifyAsync(
        UpdateManifest manifest,
        string downloadDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifest.Url))
        {
            throw new InvalidOperationException("Guncelleme indirme URL bos.");
        }

        Directory.CreateDirectory(downloadDirectory);
        var fileName = Path.GetFileName(new Uri(manifest.Url).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"ModernYedek-{manifest.Version}.zip";
        }

        var targetPath = Path.Combine(downloadDirectory, fileName);
        using var response = await _httpClient.GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(targetPath);
        var buffer = new byte[81920];
        long readTotal = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            readTotal += read;
            if (totalBytes is > 0)
            {
                progress?.Report(readTotal / (double)totalBytes.Value);
            }
        }

        await output.FlushAsync(cancellationToken);
        VerifySha256(targetPath, manifest.Sha256);
        progress?.Report(1);
        return targetPath;
    }

    public static void VerifySha256(string filePath, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            throw new InvalidOperationException("Guncelleme SHA256 degeri bos.");
        }

        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Guncelleme dosyasi SHA256 dogrulamasindan gecemedi.");
        }
    }

    private static Version ParseVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out var version)
            ? NormalizeVersion(version)
            : new Version(0, 0, 0, 0);
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            Math.Max(0, version.Major),
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build),
            Math.Max(0, version.Revision));
    }
}
