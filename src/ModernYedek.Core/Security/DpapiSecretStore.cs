using System.Text;
using System.Text.Json;
using ModernYedek.Core.Storage;

namespace ModernYedek.Core.Security;

public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _filePath;
    private Dictionary<string, string>? _cache;

    public DpapiSecretStore(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        var data = await LoadAsync(cancellationToken);
        return data.TryGetValue(key, out var value) ? value : null;
    }

    public async Task SetSecretAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        var data = await LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
        {
            data.Remove(key);
        }
        else
        {
            data[key] = value;
        }

        await SaveAsync(data, cancellationToken);
    }

    public async Task<bool> ContainsAsync(string key, CancellationToken cancellationToken = default)
    {
        var data = await LoadAsync(cancellationToken);
        return data.ContainsKey(key);
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        if (!File.Exists(_filePath))
        {
            _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return _cache;
        }

        var encrypted = await File.ReadAllBytesAsync(_filePath, cancellationToken);
        if (encrypted.Length == 0)
        {
            _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return _cache;
        }

        var plain = WindowsDpapi.Unprotect(encrypted);
        var json = Encoding.UTF8.GetString(plain);
        _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions.Compact)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        _cache = new Dictionary<string, string>(_cache, StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    private async Task SaveAsync(Dictionary<string, string> data, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var json = JsonSerializer.Serialize(data, JsonOptions.Compact);
        var encrypted = WindowsDpapi.Protect(Encoding.UTF8.GetBytes(json));
        await File.WriteAllBytesAsync(_filePath, encrypted, cancellationToken);
        _cache = new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);
    }
}
