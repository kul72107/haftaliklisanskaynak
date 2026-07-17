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
        string email,
        LicenseSettings settings,
        string machineId,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(licenseKey);
        var normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return Invalid("Lisans aktivasyonu icin e-posta gerekli.", HashLicenseKey(normalizedKey));
        }

        var hash = HashLicenseKey(normalizedKey);
        var emailHash = HashEmail(normalizedEmail);

        var record = await FindUsableLicenseRecordAsync(hash, normalizedEmail, emailHash, settings, cancellationToken);
        if (!record.IsValid)
        {
            return record.Result;
        }

        var now = DateTimeOffset.UtcNow;
        var paidUntil = ResolvePaidUntil(record.Match!, now, existingResult: null, preserveExisting: false);
        if (paidUntil <= now)
        {
            return BuildExpiredResult(
                record.Match!,
                normalizedEmail,
                machineId,
                existingResult: null,
                "Lisans suresi dolmus. Yeni key gerekir.");
        }

        return BuildActiveResult(
            record.Match!,
            normalizedEmail,
            machineId,
            existingResult: null,
            $"Lisans aktiflestirildi. Kalan sure: {RemainingDays(paidUntil, now)} gun.",
            paidUntil);
    }

    public async Task<LicenseValidationResult> ValidateExistingAsync(
        string licenseKey,
        string email,
        LicenseSettings settings,
        string machineId,
        LicenseValidationResult? existingResult,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(licenseKey);
        var normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return Invalid("Lisans dogrulamasi icin e-posta gerekli.", HashLicenseKey(normalizedKey));
        }

        var hash = HashLicenseKey(normalizedKey);
        var emailHash = HashEmail(normalizedEmail);

        var record = await FindUsableLicenseRecordAsync(hash, normalizedEmail, emailHash, settings, cancellationToken);
        if (!record.IsValid)
        {
            return record.Result;
        }

        var now = DateTimeOffset.UtcNow;
        var paidUntil = ResolvePaidUntil(record.Match!, now, existingResult, preserveExisting: true);
        if (paidUntil <= now)
        {
            return BuildExpiredResult(
                record.Match!,
                normalizedEmail,
                machineId,
                existingResult,
                "Lisans suresi dolmus. Ayni key yeniden sure baslatamaz; yeni key gerekir.");
        }

        return BuildActiveResult(
            record.Match!,
            normalizedEmail,
            machineId,
            existingResult,
            "Lisans dogrulandi. Sure ilk aktivasyondaki bitis tarihine gore korunuyor.",
            paidUntil);
    }

    public async Task<LicenseValidationResult> ValidateByEmailAsync(
        string email,
        LicenseSettings settings,
        string machineId,
        LicenseValidationResult? existingResult,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return Invalid("E-posta dogrulamasi icin e-posta gerekli.", string.Empty);
        }

        var emailHash = HashEmail(normalizedEmail);
        var record = await FindUsableLicenseRecordByEmailAsync(
            normalizedEmail,
            emailHash,
            existingResult?.LicenseId,
            settings,
            cancellationToken);
        if (!record.IsValid)
        {
            return record.Result;
        }

        var now = DateTimeOffset.UtcNow;
        var paidUntil = ResolvePaidUntil(record.Match!, now, existingResult, preserveExisting: true);
        if (paidUntil <= now)
        {
            return BuildExpiredResult(
                record.Match!,
                normalizedEmail,
                machineId,
                existingResult,
                "Bu e-posta uzerindeki lisans suresi dolmus. Yeni lisans gerekir.");
        }

        return BuildActiveResult(
            record.Match!,
            normalizedEmail,
            machineId,
            existingResult,
            $"E-posta dogrulandi. Kalan sure: {RemainingDays(paidUntil, now)} gun.",
            paidUntil);
    }

    public static string HashLicenseKey(string licenseKey)
    {
        var normalized = NormalizeKey(licenseKey);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }

    public static string HashEmail(string email)
    {
        var normalized = NormalizeEmail(email);
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

            var parts = line.Split(new[] { '|' }, 5);
            if (parts.Length < 3)
            {
                continue;
            }

            var isEmailBound = parts.Length >= 5;
            var emailIdentity = isEmailBound ? parts[1].Trim() : string.Empty;
            var durationIndex = isEmailBound ? 2 : 1;
            var statusIndex = isEmailBound ? 3 : 2;
            var noteIndex = isEmailBound ? 4 : 3;

            var durationValue = parts[durationIndex].Trim();
            DateTimeOffset? expiresAt = null;
            if (!int.TryParse(durationValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var durationDays))
            {
                if (DateTimeOffset.TryParse(
                    durationValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsedExpiresAt))
                {
                    expiresAt = parsedExpiresAt;
                }
                else
                {
                    durationDays = 7;
                }
            }

            records.Add(new StaticLicenseRecord
            {
                Hash = parts[0].Trim().ToUpperInvariant(),
                EmailIdentity = emailIdentity,
                DurationDays = durationDays,
                ExpiresAt = expiresAt,
                Status = parts.Length > statusIndex ? parts[statusIndex].Trim() : string.Empty,
                Note = parts.Length > noteIndex ? parts[noteIndex].Trim() : string.Empty
            });
        }

        return records;
    }

    private async Task<StaticLicenseRecordCheck> FindUsableLicenseRecordAsync(
        string hash,
        string normalizedEmail,
        string emailHash,
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

        var hashMatches = licensesResult.Records
            .Where(license => string.Equals(license.Hash, hash, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (hashMatches.Count == 0)
        {
            return StaticLicenseRecordCheck.Invalid(Invalid("Lisans listesinde bu key bulunamadi.", hash));
        }

        var match = hashMatches.FirstOrDefault(license => license.MatchesEmail(normalizedEmail, emailHash));
        if (match is null)
        {
            if (hashMatches.Any(license => string.IsNullOrWhiteSpace(license.EmailIdentity)))
            {
                return StaticLicenseRecordCheck.Invalid(Invalid("Bu lisans kaydi eski formatta. E-posta kontrolu icin licenseHash|emailHash|gun|status|not formatina tasiyin.", hash));
            }

            return StaticLicenseRecordCheck.Invalid(Invalid("Lisans keyi bulundu ama e-posta eslesmedi.", hash));
        }

        if (!string.Equals(match.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return StaticLicenseRecordCheck.Invalid(Invalid($"Lisans aktif degil: {match.Status}", hash));
        }

        return StaticLicenseRecordCheck.Valid(match);
    }

    private async Task<StaticLicenseRecordCheck> FindUsableLicenseRecordByEmailAsync(
        string normalizedEmail,
        string emailHash,
        string? preferredLicenseHash,
        LicenseSettings settings,
        CancellationToken cancellationToken)
    {
        var licensesResult = await TryLoadLicensesAsync(settings.LicenseListUrl, cancellationToken);
        if (!licensesResult.Success)
        {
            return StaticLicenseRecordCheck.Invalid(Invalid($"Lisans listesi kontrol edilemedi: {licensesResult.Message}", string.Empty));
        }

        var preferredHash = preferredLicenseHash?.Trim().ToUpperInvariant() ?? string.Empty;
        var emailMatches = licensesResult.Records
            .Where(license => license.MatchesEmail(normalizedEmail, emailHash))
            .ToList();
        if (emailMatches.Count == 0)
        {
            return StaticLicenseRecordCheck.Invalid(Invalid("Bu e-posta icin lisans kaydi bulunamadi.", string.Empty));
        }

        var activeMatches = emailMatches
            .Where(license => string.Equals(license.Status, "active", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(license => string.Equals(license.Hash, preferredHash, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(license => license.SortExpiry(DateTimeOffset.UtcNow))
            .ToList();
        if (activeMatches.Count == 0)
        {
            return StaticLicenseRecordCheck.Invalid(Invalid("Bu e-posta icin aktif lisans kaydi yok.", emailMatches[0].Hash));
        }

        foreach (var match in activeMatches)
        {
            var revocation = await CheckRevocationAsync(match.Hash, settings.RevokedListUrl, cancellationToken);
            if (!revocation.Success)
            {
                return StaticLicenseRecordCheck.Invalid(Invalid($"Iptal listesi kontrol edilemedi: {revocation.Message}", match.Hash));
            }

            if (!revocation.IsRevoked)
            {
                return StaticLicenseRecordCheck.Valid(match);
            }
        }

        return StaticLicenseRecordCheck.Invalid(Invalid("Bu e-posta icin bulunan lisans iptal edilmis.", activeMatches[0].Hash));
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

    private static LicenseValidationResult BuildActiveResult(
        StaticLicenseRecord record,
        string normalizedEmail,
        string machineId,
        LicenseValidationResult? existingResult,
        string message,
        DateTimeOffset paidUntil)
    {
        return new LicenseValidationResult
        {
            IsValid = true,
            State = LicenseState.Active,
            Message = message,
            Provider = "github-pages-txt",
            LicenseId = record.Hash,
            InstanceId = existingResult?.InstanceId ?? BuildInstanceId(record.Hash, machineId),
            CustomerEmail = normalizedEmail,
            ProductId = "modern-yedek",
            VariantId = GetVariantId(record),
            Plan = GetPlan(record),
            Note = record.Note,
            PaidUntil = paidUntil,
            OfflineUntil = paidUntil,
            ActivationLimit = 1,
            ActivationCount = 1
        };
    }

    private static LicenseValidationResult BuildExpiredResult(
        StaticLicenseRecord record,
        string normalizedEmail,
        string machineId,
        LicenseValidationResult? existingResult,
        string message)
    {
        var paidUntil = ResolvePaidUntil(record, DateTimeOffset.UtcNow, existingResult, preserveExisting: true);
        return new LicenseValidationResult
        {
            IsValid = false,
            State = LicenseState.Expired,
            Message = message,
            Provider = "github-pages-txt",
            LicenseId = record.Hash,
            InstanceId = existingResult?.InstanceId ?? BuildInstanceId(record.Hash, machineId),
            CustomerEmail = normalizedEmail,
            ProductId = "modern-yedek",
            VariantId = GetVariantId(record),
            Plan = GetPlan(record),
            Note = existingResult?.Note ?? record.Note,
            PaidUntil = paidUntil,
            OfflineUntil = paidUntil,
            ActivationLimit = 1,
            ActivationCount = 1
        };
    }

    private static DateTimeOffset ResolvePaidUntil(
        StaticLicenseRecord record,
        DateTimeOffset now,
        LicenseValidationResult? existingResult,
        bool preserveExisting)
    {
        if (record.ExpiresAt is not null)
        {
            return record.ExpiresAt.Value;
        }

        if (preserveExisting)
        {
            var existingPaidUntil = existingResult?.PaidUntil ?? existingResult?.OfflineUntil;
            if (existingPaidUntil is not null)
            {
                return existingPaidUntil.Value;
            }
        }

        return now.AddDays(Math.Max(1, record.DurationDays));
    }

    private static int RemainingDays(DateTimeOffset paidUntil, DateTimeOffset now)
    {
        return Math.Max(0, (int)Math.Ceiling((paidUntil - now).TotalDays));
    }

    private static string GetVariantId(StaticLicenseRecord record)
    {
        return record.ExpiresAt is not null
            ? record.ExpiresAt.Value.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : Math.Max(1, record.DurationDays).ToString(CultureInfo.InvariantCulture);
    }

    private static string GetPlan(StaticLicenseRecord record)
    {
        return record.ExpiresAt is not null
            ? "manual_until"
            : $"manual_{Math.Max(1, record.DurationDays)}d";
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

    public static string NormalizeEmail(string email)
    {
        return string.IsNullOrWhiteSpace(email)
            ? string.Empty
            : email.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
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
        public string EmailIdentity { get; init; } = string.Empty;
        public int DurationDays { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public string Status { get; init; } = string.Empty;
        public string Note { get; init; } = string.Empty;

        public bool MatchesEmail(string normalizedEmail, string emailHash)
        {
            if (string.IsNullOrWhiteSpace(EmailIdentity))
            {
                return false;
            }

            return string.Equals(EmailIdentity.Trim(), emailHash, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeEmail(EmailIdentity), normalizedEmail, StringComparison.OrdinalIgnoreCase);
        }

        public DateTimeOffset SortExpiry(DateTimeOffset now)
        {
            return ExpiresAt ?? now.AddDays(Math.Max(1, DurationDays));
        }
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
