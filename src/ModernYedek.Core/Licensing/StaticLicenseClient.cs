using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using ModernYedek.Core.Models;

namespace ModernYedek.Core.Licensing;

public sealed class StaticLicenseClient
{
    private readonly HttpClient _httpClient;

    public StaticLicenseClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<LicenseValidationResult> ActivateAsync(
        string licenseKey,
        LicenseSettings settings,
        string machineId,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(licenseKey);
        var hash = HashLicenseKey(normalizedKey);

        var record = await FindUsableLicenseRecordAsync(hash, settings, cancellationToken);
        if (!record.IsValid)
        {
            return record.Result;
        }

        var now = DateTimeOffset.UtcNow;
        var durationDays = Math.Max(1, record.Match!.DurationDays);
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
            Note = record.Match.Note,
            PaidUntil = paidUntil,
            OfflineUntil = paidUntil,
            ActivationLimit = 1,
            ActivationCount = 1
        };
    }

    public async Task<LicenseValidationResult> ValidateExistingAsync(
        string licenseKey,
        LicenseSettings settings,
        string machineId,
        LicenseValidationResult? existingResult,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(licenseKey);
        var hash = HashLicenseKey(normalizedKey);

        var record = await FindUsableLicenseRecordAsync(hash, settings, cancellationToken);
        if (!record.IsValid)
        {
            return record.Result;
        }

        var now = DateTimeOffset.UtcNow;
        var durationDays = Math.Max(1, record.Match!.DurationDays);
        var paidUntil = existingResult?.PaidUntil ?? existingResult?.OfflineUntil ?? now.AddDays(durationDays);
        if (paidUntil <= now)
        {
            return new LicenseValidationResult
            {
                IsValid = false,
                State = LicenseState.Expired,
                Message = "Lisans suresi dolmus. Ayni key yeniden sure baslatamaz; yeni key gerekir.",
                Provider = "github-pages-txt",
                LicenseId = hash,
                InstanceId = existingResult?.InstanceId ?? BuildInstanceId(hash, machineId),
                ProductId = "modern-yedek",
                VariantId = durationDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Plan = $"manual_{durationDays}d",
                Note = existingResult?.Note ?? record.Match.Note,
                PaidUntil = paidUntil,
                OfflineUntil = paidUntil,
                ActivationLimit = 1,
                ActivationCount = 1
            };
        }

        return new LicenseValidationResult
        {
            IsValid = true,
            State = LicenseState.Active,
            Message = "Lisans dogrulandi. Sure ilk aktivasyondaki bitis tarihine gore korunuyor.",
            Provider = "github-pages-txt",
            LicenseId = hash,
            InstanceId = existingResult?.InstanceId ?? BuildInstanceId(hash, machineId),
            CustomerEmail = string.Empty,
            ProductId = "modern-yedek",
            VariantId = durationDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Plan = $"manual_{durationDays}d",
            Note = record.Match.Note,
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

    private async Task<StaticLicenseRecordCheck> FindUsableLicenseRecordAsync(
        string hash,
        LicenseSettings settings,
        CancellationToken cancellationToken)
    {
        var revocationTask = CheckRevocationAsync(hash, settings.RevokedListUrl, cancellationToken);
        var licensesTask = TryLoadLicensesAsync(settings.LicenseListUrl, cancellationToken);

        await Task.WhenAll(revocationTask, licensesTask);

        var revocation = await revocationTask;
        if (!revocation.Success)
        {
            return StaticLicenseRecordCheck.Invalid(Invalid($"Iptal listesi kontrol edilemedi: {revocation.Message}", hash));
        }

        if (revocation.IsRevoked)
        {
            return StaticLicenseRecordCheck.Invalid(Invalid("Bu lisans iptal edilmis.", hash));
        }

        var licensesResult = await licensesTask;
        if (!licensesResult.Success)
        {
            return StaticLicenseRecordCheck.Invalid(Invalid($"Lisans listesi kontrol edilemedi: {licensesResult.Message}", hash));
        }

        var match = licensesResult.Records.FirstOrDefault(license => string.Equals(license.Hash, hash, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return StaticLicenseRecordCheck.Invalid(Invalid("Lisans listesinde bu key bulunamadi.", hash));
        }

        if (!string.Equals(match.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return StaticLicenseRecordCheck.Invalid(Invalid($"Lisans aktif degil: {match.Status}", hash));
        }

        return StaticLicenseRecordCheck.Valid(match);
    }

    private async Task<LicenseListLoadResult> TryLoadLicensesAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var records = await LoadLicensesAsync(url, cancellationToken);
            return new LicenseListLoadResult { Success = true, Records = records };
        }
        catch (Exception ex)
        {
            return new LicenseListLoadResult { Success = false, Message = ex.Message };
        }
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
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            return string.Empty;
        }

        var normalized = licenseKey.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (char.IsWhiteSpace(character) || char.IsControl(character) || category == UnicodeCategory.Format)
            {
                continue;
            }

            builder.Append(IsDashLike(character) ? '-' : char.ToUpperInvariant(character));
        }

        return builder.ToString();
    }

    private static bool IsDashLike(char character)
    {
        return character is '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212';
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

    private sealed class StaticLicenseRecordCheck
    {
        public bool IsValid { get; init; }
        public StaticLicenseRecord? Match { get; init; }
        public LicenseValidationResult Result { get; init; } = new();

        public static StaticLicenseRecordCheck Valid(StaticLicenseRecord match)
        {
            return new StaticLicenseRecordCheck { IsValid = true, Match = match };
        }

        public static StaticLicenseRecordCheck Invalid(LicenseValidationResult result)
        {
            return new StaticLicenseRecordCheck { IsValid = false, Result = result };
        }
    }

    private sealed class LicenseListLoadResult
    {
        public bool Success { get; init; }
        public List<StaticLicenseRecord> Records { get; init; } = [];
        public string Message { get; init; } = string.Empty;
    }
}
