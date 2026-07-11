namespace ModernYedek.Core.Updates;

public sealed class UpdateManifest
{
    public string Version { get; set; } = string.Empty;
    public bool Mandatory { get; set; } = true;
    public string Url { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed class UpdateCheckResult
{
    public bool HasUpdate { get; init; }
    public UpdateManifest? Manifest { get; init; }
    public string Message { get; init; } = string.Empty;
}
