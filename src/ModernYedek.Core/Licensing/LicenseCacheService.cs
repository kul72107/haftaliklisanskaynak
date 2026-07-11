using System.Text.Json;
using ModernYedek.Core.Security;
using ModernYedek.Core.Storage;

namespace ModernYedek.Core.Licensing;

public sealed class LicenseCacheService
{
    private const string CacheKey = "license.cache";
    private readonly ISecretStore _secretStore;

    public LicenseCacheService(ISecretStore secretStore)
    {
        _secretStore = secretStore;
    }

    public async Task<LicenseCache?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await _secretStore.GetSecretAsync(CacheKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LicenseCache>(json, JsonOptions.Compact);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(LicenseCache cache, CancellationToken cancellationToken = default)
    {
        cache.CachedAt = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(cache, JsonOptions.Compact);
        await _secretStore.SetSecretAsync(CacheKey, json, cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        return _secretStore.SetSecretAsync(CacheKey, null, cancellationToken);
    }

    public static bool CanUseOffline(LicenseCache? cache, DateTimeOffset now)
    {
        if (cache?.LastResult is null || !cache.LastResult.IsValid)
        {
            return false;
        }

        if (cache.LastResult.OfflineUntil is null)
        {
            return false;
        }

        return cache.LastResult.OfflineUntil.Value > now;
    }
}
