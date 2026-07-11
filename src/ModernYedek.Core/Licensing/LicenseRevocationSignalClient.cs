using ModernYedek.Core.Models;

namespace ModernYedek.Core.Licensing;

public sealed class LicenseRevocationSignalClient
{
    private readonly HttpClient _httpClient;

    public LicenseRevocationSignalClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<bool> SendAsync(
        LicenseSettings settings,
        LicenseRevocationSignal signal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.RevocationSignalUrl))
        {
            return false;
        }

        var values = BuildValues(settings.RevocationSignalFields, signal);
        using var content = new FormUrlEncodedContent(values);
        using var response = await _httpClient.PostAsync(settings.RevocationSignalUrl.Trim(), content, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static Dictionary<string, string> BuildValues(
        Dictionary<string, string> fieldMap,
        LicenseRevocationSignal signal)
    {
        var payload = new Dictionary<string, string>
        {
            ["revoked"] = signal.Revoked ? "evet" : "hayır",
            ["license_hash"] = signal.LicenseHash,
            ["machine_id"] = signal.MachineId,
            ["computer_name"] = signal.ComputerName,
            ["windows_user"] = signal.WindowsUser,
            ["revoked_at"] = signal.RevokedAt.ToString("O"),
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

public sealed class LicenseRevocationSignal
{
    public bool Revoked { get; init; } = true;
    public string LicenseHash { get; init; } = string.Empty;
    public string MachineId { get; init; } = string.Empty;
    public string ComputerName { get; init; } = string.Empty;
    public string WindowsUser { get; init; } = string.Empty;
    public DateTimeOffset RevokedAt { get; init; } = DateTimeOffset.UtcNow;
    public string AppVersion { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
}
