using System.Security.Cryptography;
using System.Text;
using ModernYedek.Core.Models;

namespace ModernYedek.Core.Licensing;

public sealed class StaticLicenseClient
{
    private readonly HttpClient _httpClient;

    public StaticLicenseClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<LicenseValidationResult> ActivateAsync(
        string licenseKey,
        LicenseSettings settings,
        string machineId,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(licenseKey);
        var hash = HashLicenseKey(normalizedKey);

        var revocation = await CheckRevocationAsync(hash, settings.RevokedListUrl, cancellationToken);
        if (!revocation.Success)
        {
            return Invalid($"Iptal listesi kontrol edilemedi: {revocation.Message}", hash);
        }

        if (revocation.Success && revocation.IsRevoked)
        {
            return Invalid("Bu lisans iptal edilmis.", hash);
        }

        var licenses = await LoadLicensesAsync(settings.LicenseListUrl, cancellationToken);
        var match = licenses.FirstOrDefault(license => string.Equals(license.Hash, hash, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return Invalid("Lisans listesinde bu key bulunamadi.", hash);
        }

        if (!string.Equals(match.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid($"Lisans aktif degil: {match.Status}", hash);
        }

        var now = DateTimeOffset.UtcNow;
        var durationDays = Math.Max(1, match.DurationDays);
        var paidUntil = now.AddDays(durationDays);

        return new LicenseValidationResult
        {
            IsValid = true,
            State = LicenseState.Active,
            Message = $"Lisans aktiflestirildi. Sure: {durationDays} gun.",
            Provider = "github-pages-txt",
            LicenseId = hash,
            InstanceId = BuildInstanceId(hash, machineId),
            CustomerEmail = string.Empty,
            ProductId = "modern-yedek",
            VariantId = durationDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Plan = $"manual_{durationDays}d",
            Note = match.Note,
            PaidUntil = paidUntil,
            OfflineUntil = paidUntil,
            ActivationLimit = 1,
            ActivationCount = 1
        };
    }

    public static string HashLicenseKey(string licenseKey)
    {
        var normalized = NormalizeKey(licenseKey);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }

    public async Task<RevocationCheckResult> CheckRevocationAsync(
        string licenseHash,
        string revokedListUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(revokedListUrl))
        {
            return new RevocationCheckResult { Success = true, Message = "Iptal listesi kapali." };
        }

        try
        {
            var hash = licenseHash.Trim().ToUpperInvariant();
            var text = await _httpClient.GetStringAsync(revokedListUrl, cancellationToken);
            foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(line, hash, StringComparison.OrdinalIgnoreCase))
                {
                    return new RevocationCheckResult
                    {
                        Success = true,
                        IsRevoked = true,
                        Message = "Lisans iptal listesinde bulundu."
                    };
                }
            }

            return new RevocationCheckResult
            {
                Success = true,
                IsRevoked = false,
                Message = "Lisans iptal listesinde yok."
            };
        }
        catch (Exception ex)
        {
            return new RevocationCheckResult
            {
                Success = false,
                IsRevoked = false,
                Message = ex.Message
            };
        }
    }

    private async Task<List<StaticLicenseRecord>> LoadLicensesAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return [];
        }

        var text = await _httpClient.GetStringAsync(url, cancellationToken);
        var records = new List<StaticLicenseRecord>();
        foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('|', 4);
            if (parts.Length < 3)
            {
                continue;
            }

            if (!int.TryParse(parts[1], out var durationDays))
            {
                durationDays = 7;
            }

            records.Add(new StaticLicenseRecord
            {
                Hash = parts[0].Trim().ToUpperInvariant(),
                DurationDays = durationDays,
                Status = parts[2].Trim(),
                Note = parts.Length >= 4 ? parts[3].Trim() : string.Empty
            });
        }

        return records;
    }

    private static LicenseValidationResult Invalid(string message, string hash)
    {
        return new LicenseValidationResult
        {
            IsValid = false,
            State = LicenseState.Invalid,
            Message = message,
            Provider = "github-pages-txt",
            LicenseId = hash,
            Plan = "manual"
        };
    }

    private static string NormalizeKey(string licenseKey)
    {
        return licenseKey.Trim().ToUpperInvariant();
    }

    private static string BuildInstanceId(string hash, string machineId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(hash + "|" + machineId));
        return "txt_" + Convert.ToHexString(bytes)[..24];
    }

    private sealed class StaticLicenseRecord
    {
        public string Hash { get; init; } = string.Empty;
        public int DurationDays { get; init; }
        public string Status { get; init; } = string.Empty;
        public string Note { get; init; } = string.Empty;
    }
}
