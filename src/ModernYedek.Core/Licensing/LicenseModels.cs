namespace ModernYedek.Core.Licensing;

public enum LicenseState
{
    Unknown,
    Active,
    Trialing,
    PastDue,
    Expired,
    Canceled,
    Invalid
}

public sealed class LicenseActivationRequest
{
    public string LicenseKey { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
}

public sealed class LicenseValidationRequest
{
    public string LicenseKey { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
}

public sealed class LicenseValidationResult
{
    public bool IsValid { get; set; }
    public LicenseState State { get; set; } = LicenseState.Unknown;
    public string Message { get; set; } = string.Empty;
    public string Provider { get; set; } = "manual";
    public string LicenseId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public string VariantId { get; set; } = string.Empty;
    public string Plan { get; set; } = "weekly_pro";
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset? PaidUntil { get; set; }
    public DateTimeOffset? OfflineUntil { get; set; }
    public int ActivationLimit { get; set; }
    public int ActivationCount { get; set; }
}

public sealed class LicenseCache
{
    public string LicenseKey { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string LicenseListUrl { get; set; } = string.Empty;
    public LicenseValidationResult LastResult { get; set; } = new();
    public DateTimeOffset CachedAt { get; set; } = DateTimeOffset.UtcNow;
}
