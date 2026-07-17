using System.Text.Json;
using ModernYedek.Core.Models;

namespace ModernYedek.Core.Storage;

public sealed class SettingsService
{
    private readonly string _settingsFile;

    public SettingsService(string settingsFile)
    {
        _settingsFile = settingsFile;
    }

    public bool Exists => File.Exists(_settingsFile);

    public async Task<BackupSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsFile))
        {
            return CreateDefault();
        }

        await using var stream = File.OpenRead(_settingsFile);
        var settings = await JsonSerializer.DeserializeAsync<BackupSettings>(
            stream,
            JsonOptions.Indented,
            cancellationToken);

        return settings ?? CreateDefault();
    }

    public async Task SaveAsync(BackupSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
        await using var stream = File.Create(_settingsFile);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions.Indented, cancellationToken);
    }

    public static BackupSettings CreateDefault()
    {
        return new BackupSettings
        {
            ProfileName = "Datasoft Yedek",
            ZipEnabled = true,
            ArchiveFormat = BackupArchiveFormat.Zip,
            Schedule = new ScheduleSettings
            {
                Enabled = true,
                Days =
                [
                    DayOfWeek.Monday,
                    DayOfWeek.Tuesday,
                    DayOfWeek.Wednesday,
                    DayOfWeek.Thursday,
                    DayOfWeek.Friday
                ],
                Times = ["18:00"]
            },
            OneTimeSchedule = new OneTimeScheduleSettings(),
            Warning = new BackupWarningSettings
            {
                Enabled = false,
                MinutesBefore = 1,
                SnoozeMinutes = 5,
                AutoCloseResultPopup = false,
                ResultPopupSeconds = 10
            },
            SqlService = new SqlServiceSettings
            {
                StopBeforeBackup = false,
                ServiceName = "MSSQLSERVER",
                RestartAfterBackup = true
            },
            Retention = new RetentionSettings
            {
                Enabled = true,
                KeepDays = 30,
                MaxTotalSizeGb = 50
            },
            Cloud = new CloudSettings
            {
                Provider = "GoogleCloudStorage",
                ObjectPrefix = "yedekler"
            },
            Mail = new MailSettings
            {
                Enabled = false,
                SendLogAfterBackup = false,
                Port = 587,
                UseSsl = true,
                Subject = "Yedek Raporu"
            },
            License = new LicenseSettings
            {
                Required = true,
                ApiBaseUrl = LicenseSettings.DefaultApiBaseUrl,
                LicenseListUrl = LicenseSettings.DefaultLicenseListUrl,
                RevokedListUrl = LicenseSettings.DefaultRevokedListUrl,
                ActivationSignalUrl = LicenseSettings.DefaultActivationSignalUrl,
                ActivationSignalFields = LicenseSettings.CreateDefaultActivationSignalFields(),
                RevocationSignalUrl = LicenseSettings.DefaultRevocationSignalUrl,
                RevocationSignalFields = LicenseSettings.CreateDefaultRevocationSignalFields()
            },
            Update = new UpdateSettings
            {
                Enabled = true,
                ManifestUrl = UpdateSettings.DefaultManifestUrl
            },
            AppBehavior = new AppBehaviorSettings
            {
                MinimizeToTrayOnClose = true,
                StartWithWindows = false
            }
        };
    }
}
