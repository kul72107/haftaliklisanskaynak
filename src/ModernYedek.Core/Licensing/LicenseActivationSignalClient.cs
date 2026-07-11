using System.Net.Http.Json;
using ModernYedek.Core.Models;

namespace ModernYedek.Core.Licensing;

public sealed class LicenseActivationSignalClient
{
    private readonly HttpClient _httpClient;

    public LicenseActivationSignalClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<bool> SendAsync(
        LicenseSettings settings,
        LicenseActivationSignal signal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ActivationSignalUrl))
        {
            return false;
        }

        var url = settings.ActivationSignalUrl.Trim();
        var values = BuildValues(settings.ActivationSignalFields, signal);
        using var content = new FormUrlEncodedContent(values);
        using var response = await _httpClient.PostAsync(url, content, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static Dictionary<string, string> BuildValues(
        Dictionary<string, string> fieldMap,
        LicenseActivationSignal signal)
    {
        var payload = new Dictionary<string, string>
        {
            ["license_hash"] = signal.LicenseHash,
            ["machine_id"] = signal.MachineId,
            ["computer_name"] = signal.ComputerName,
            ["windows_user"] = signal.WindowsUser,
            ["activated_at"] = signal.ActivatedAt.ToString("O"),
            ["expires_at"] = signal.ExpiresAt?.ToString("O") ?? string.Empty,
            ["provider"] = signal.Provider,
            ["app_version"] = signal.AppVersion,
            ["note"] = signal.Note
        };

        if (fieldMap.Count == 0)
        {
            return payload;
        }

        var mapped = new Dictionary<string, string>();
        foreach (var (payloadKey, value) in payload)
        {
            if (fieldMap.TryGetValue(payloadKey, out var formField) && !string.IsNullOrWhiteSpace(formField))
            {
                mapped[formField] = value;
            }
        }

        return mapped;
    }
}

public sealed class LicenseActivationSignal
{
    public string LicenseHash { get; init; } = string.Empty;
    public string MachineId { get; init; } = string.Empty;
    public string ComputerName { get; init; } = string.Empty;
    public string WindowsUser { get; init; } = string.Empty;
    public DateTimeOffset ActivatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string AppVersion { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
}
