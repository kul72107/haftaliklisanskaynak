using System.Text.Json.Serialization;

namespace ModernYedek.Core.Models;

public enum BackupSourceType
{
    Folder,
    File
}

public enum BackupOutcome
{
    Success,
    Partial,
    Failed
}

public enum BackupLogLevel
{
    Info,
    Warning,
    Error
}

public sealed class BackupSettings
{
    public string ProfileName { get; set; } = "Varsayilan Yedek";
    public bool ZipEnabled { get; set; } = true;
    public List<BackupSource> Sources { get; set; } = [];
    public List<BackupTarget> Targets { get; set; } = [];
    public ScheduleSettings Schedule { get; set; } = new();
    public RetentionSettings Retention { get; set; } = new();
    public CloudSettings Cloud { get; set; } = new();
    public MailSettings Mail { get; set; } = new();
    public LicenseSettings License { get; set; } = new();
}

public sealed class BackupSource
{
    public string Path { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BackupSourceType Type { get; set; } = BackupSourceType.Folder;
    public bool Enabled { get; set; } = true;
}

public sealed class BackupTarget
{
    public string Path { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed class ScheduleSettings
{
    public bool Enabled { get; set; } = true;
    public List<DayOfWeek> Days { get; set; } = [];
    public List<string> Times { get; set; } = [];
}

public sealed class RetentionSettings
{
    public bool Enabled { get; set; } = true;
    public int KeepDays { get; set; } = 30;
    public double MaxTotalSizeGb { get; set; } = 50;
}

public sealed class CloudSettings
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "GoogleCloudStorage";
    public string BucketName { get; set; } = string.Empty;
    public string ObjectPrefix { get; set; } = "yedekler";
    public bool UploadAfterBackup { get; set; }
    public bool DeleteLocalAfterUpload { get; set; }
}

public sealed class MailSettings
{
    public string Recipient { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Subject { get; set; } = "Yedek Raporu";
}

public sealed class LicenseSettings
{
    public const string DefaultApiBaseUrl = "https://0b95d7d19975e1f8-112-126-72-180.serveousercontent.com";

    public bool Required { get; set; } = true;
    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;
    public string Email { get; set; } = string.Empty;
}

public sealed class BackupLogEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BackupLogLevel Level { get; set; } = BackupLogLevel.Info;
    public string OperationId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class BackupRunResult
{
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BackupOutcome Outcome { get; set; } = BackupOutcome.Failed;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.Now;
    public string? ArchivePath { get; set; }
    public string? Sha256 { get; set; }
    public long ArchiveBytes { get; set; }
    public int FilesAdded { get; set; }
    public int FilesSkipped { get; set; }
    public List<BackupLogEntry> Entries { get; set; } = [];
}

public sealed class CloudUploadRequest
{
    public required string BucketName { get; init; }
    public required string ObjectName { get; init; }
    public required string FilePath { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed class CloudUploadResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ObjectName { get; init; }
}

public sealed class LegacyImportResult
{
    public BackupSettings Settings { get; init; } = new();
    public string? MailPassword { get; init; }
}
