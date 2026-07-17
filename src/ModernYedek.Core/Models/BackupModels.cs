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

public enum BackupArchiveFormat
{
    Zip,
    Rar
}

public sealed class BackupSettings
{
    public string ProfileName { get; set; } = "Varsayilan Yedek";
    public bool ZipEnabled { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BackupArchiveFormat ArchiveFormat { get; set; } = BackupArchiveFormat.Zip;
    public List<BackupSource> Sources { get; set; } = [];
    public List<BackupTarget> Targets { get; set; } = [];
    public ScheduleSettings Schedule { get; set; } = new();
    public OneTimeScheduleSettings OneTimeSchedule { get; set; } = new();
    public BackupWarningSettings Warning { get; set; } = new();
    public SqlServiceSettings SqlService { get; set; } = new();
    public RetentionSettings Retention { get; set; } = new();
    public CloudSettings Cloud { get; set; } = new();
    public MailSettings Mail { get; set; } = new();
    public LicenseSettings License { get; set; } = new();
    public UpdateSettings Update { get; set; } = new();
    public AppBehaviorSettings AppBehavior { get; set; } = new();
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

public sealed class OneTimeScheduleSettings
{
    public bool Enabled { get; set; }
    public DateTimeOffset? RunAt { get; set; }
}

public sealed class BackupWarningSettings
{
    public bool Enabled { get; set; }
    public int MinutesBefore { get; set; } = 1;
    public int SnoozeMinutes { get; set; } = 5;
    public bool AutoCloseResultPopup { get; set; }
    public int ResultPopupSeconds { get; set; } = 10;
}

public sealed class SqlServiceSettings
{
    public bool StopBeforeBackup { get; set; }
    public string ServiceName { get; set; } = "MSSQLSERVER";
    public bool RestartAfterBackup { get; set; } = true;
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
    public bool Enabled { get; set; }
    public bool SendLogAfterBackup { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string UserName { get; set; } = string.Empty;
    public string Subject { get; set; } = "Yedek Raporu";
}

public sealed class LicenseSettings
{
    public const string DefaultApiBaseUrl = "https://0b95d7d19975e1f8-112-126-72-180.serveousercontent.com";
    public const string DefaultLicenseListUrl = "https://raw.githubusercontent.com/kul72107/haftaliklisanskaynak/main/docs/licenses.txt";
    public const string DefaultRevokedListUrl = "https://raw.githubusercontent.com/kul72107/haftaliklisanskaynak/main/docs/revoked.txt";
    public const string DefaultActivationSignalUrl = "https://docs.google.com/forms/d/e/1FAIpQLSdOFrMtIMX3FBXRa0u7eTO00y1w-AYB8EKQ0qMzCQmmcP2oIQ/formResponse";
    public const string DefaultRevocationSignalUrl = "https://docs.google.com/forms/d/e/1FAIpQLSe3il0zE3oByaC951UydsfRrPmqic6fMYhsug5rSOBgd0uFug/formResponse";

    public bool Required { get; set; } = true;
    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;
    public string LicenseListUrl { get; set; } = DefaultLicenseListUrl;
    public string RevokedListUrl { get; set; } = DefaultRevokedListUrl;
    public string ActivationSignalUrl { get; set; } = DefaultActivationSignalUrl;
    public Dictionary<string, string> ActivationSignalFields { get; set; } = CreateDefaultActivationSignalFields();
    public string RevocationSignalUrl { get; set; } = DefaultRevocationSignalUrl;
    public Dictionary<string, string> RevocationSignalFields { get; set; } = CreateDefaultRevocationSignalFields();
    public string Email { get; set; } = string.Empty;

    public static Dictionary<string, string> CreateDefaultActivationSignalFields()
    {
        return new Dictionary<string, string>
        {
            ["license_hash"] = "entry.1986987783",
            ["machine_id"] = "entry.1100798267",
            ["computer_name"] = "entry.2085233059",
            ["activated_at"] = "entry.471081137",
            ["app_version"] = "entry.1456895206",
            ["note"] = "entry.1183096403"
        };
    }

    public static Dictionary<string, string> CreateDefaultRevocationSignalFields()
    {
        return new Dictionary<string, string>
        {
            ["revoked"] = "entry.1223292050"
        };
    }
}

public sealed class UpdateSettings
{
    public const string LegacyCdnMainManifestUrl = "https://cdn.jsdelivr.net/gh/kul72107/Yedek-app@main/latest.json";
    public const string LegacyCdnHeadManifestUrl = "https://cdn.jsdelivr.net/gh/kul72107/Yedek-app/latest.json";
    public const string DefaultManifestUrl = "https://raw.githubusercontent.com/kul72107/Yedek-app/main/latest.json";

    public bool Enabled { get; set; } = true;
    public string ManifestUrl { get; set; } = DefaultManifestUrl;
}

public sealed class AppBehaviorSettings
{
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool StartWithWindows { get; set; }
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

public sealed class BackupProgress
{
    public string Stage { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public double Percent { get; init; }
    public bool IsIndeterminate { get; init; }
    public int FilesProcessed { get; init; }
    public int TotalFiles { get; init; }
    public string? CurrentFile { get; init; }
    public string? TargetPath { get; init; }
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
