using System.Globalization;
using System.Text;
using ModernYedek.Core.Models;

namespace ModernYedek.Core.Import;

public sealed class LegacyIniImporter
{
    public LegacyImportResult Import(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Eski ayar dosyasi bulunamadi.", filePath);
        }

        var ini = Parse(File.ReadAllLines(filePath, Encoding.UTF8));
        var settings = new BackupSettings
        {
            ProfileName = "Datasoft Yedek",
            ZipEnabled = ReadBool(ini, "DURUM", "ZIPMI", defaultValue: true),
            ArchiveFormat = BackupArchiveFormat.Zip,
            Sources = ReadSources(ini),
            Targets = ReadTargets(ini),
            Schedule = ReadSchedule(ini),
            OneTimeSchedule = ReadOneTimeSchedule(ini),
            Warning = new BackupWarningSettings
            {
                Enabled = ReadBool(ini, "DURUM", "UYARI", defaultValue: false),
                MinutesBefore = Math.Max(1, ReadInt(ini, "DURUM", "UYARIDK", 1)),
                SnoozeMinutes = 5,
                AutoCloseResultPopup = ReadBool(ini, "DURUM", "UYARIKAPAT", defaultValue: false),
                ResultPopupSeconds = 10
            },
            SqlService = new SqlServiceSettings
            {
                StopBeforeBackup = ReadBool(ini, "DURUM", "SERVER", defaultValue: false),
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
                Enabled = ReadBool(ini, "DURUM", "MAILMI", defaultValue: false),
                SendLogAfterBackup = ReadBool(ini, "DURUM", "MAILMI", defaultValue: false),
                Recipient = ReadValue(ini, "MAIL", "ADRES") ?? string.Empty,
                Server = ReadValue(ini, "MAIL", "SUNUCU") ?? string.Empty,
                Port = 587,
                UseSsl = true,
                UserName = ReadValue(ini, "MAIL", "USER") ?? string.Empty,
                Subject = ReadValue(ini, "MAIL", "SUBJECT") ?? "Yedek Raporu"
            }
        };

        return new LegacyImportResult
        {
            Settings = settings,
            MailPassword = ReadValue(ini, "MAIL", "PASS")
        };
    }

    private static OneTimeScheduleSettings ReadOneTimeSchedule(Dictionary<string, Dictionary<string, string>> ini)
    {
        return new OneTimeScheduleSettings
        {
            Enabled = ReadBool(ini, "DURUM", "TEK", defaultValue: false)
        };
    }

    private static List<BackupSource> ReadSources(Dictionary<string, Dictionary<string, string>> ini)
    {
        var count = ReadInt(ini, "KLASORSAY", "SAY", 0);
        var sources = new List<BackupSource>();

        for (var i = 0; i < count; i++)
        {
            var path = ReadValue(ini, "KLASORLER", $"KLASOR{i}");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var typeText = ReadValue(ini, "KLASORLER", $"KLASORTİP{i}") ?? string.Empty;
            sources.Add(new BackupSource
            {
                Path = path.Trim(),
                Type = typeText.Contains("DOSYA", StringComparison.OrdinalIgnoreCase)
                    ? BackupSourceType.File
                    : BackupSourceType.Folder,
                Enabled = true
            });
        }

        return sources;
    }

    private static List<BackupTarget> ReadTargets(Dictionary<string, Dictionary<string, string>> ini)
    {
        var targets = new List<BackupTarget>();
        var count = ReadInt(ini, "HEDEFKLASORSAY", "SAY", 0);

        for (var i = 0; i < count; i++)
        {
            var path = ReadValue(ini, "KLASORLER", $"HEDEFKLASORLER{i}");
            if (!string.IsNullOrWhiteSpace(path))
            {
                targets.Add(new BackupTarget { Path = path.Trim(), Enabled = true });
            }
        }

        var fallback = ReadValue(ini, "HEDEF", "KLASOR");
        if (!string.IsNullOrWhiteSpace(fallback)
            && targets.All(target => !string.Equals(target.Path, fallback.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            targets.Add(new BackupTarget { Path = fallback.Trim(), Enabled = true });
        }

        return targets;
    }

    private static ScheduleSettings ReadSchedule(Dictionary<string, Dictionary<string, string>> ini)
    {
        var days = new List<DayOfWeek>();
        if (ini.TryGetValue("GUNLER", out var daySection))
        {
            foreach (var value in daySection.Values)
            {
                if (TryParseTurkishDay(value, out var day) && !days.Contains(day))
                {
                    days.Add(day);
                }
            }
        }

        var times = new List<string>();
        if (ini.TryGetValue("SAATLER", out var timeSection))
        {
            foreach (var value in timeSection.Values)
            {
                if (TimeOnly.TryParseExact(value.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                {
                    times.Add(time.ToString("HH:mm", CultureInfo.InvariantCulture));
                }
            }
        }

        return new ScheduleSettings
        {
            Enabled = ReadBool(ini, "DURUM", "PERIYODIK", defaultValue: true),
            Days = days.Count > 0 ? days : [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            Times = times.Count > 0 ? times : ["18:00"]
        };
    }

    private static bool TryParseTurkishDay(string value, out DayOfWeek day)
    {
        switch (value.Trim().ToUpperInvariant())
        {
            case "PAZARTESI":
            case "PAZARTESİ":
                day = DayOfWeek.Monday;
                return true;
            case "SALI":
                day = DayOfWeek.Tuesday;
                return true;
            case "CARSAMBA":
            case "ÇARŞAMBA":
                day = DayOfWeek.Wednesday;
                return true;
            case "PERSEMBE":
            case "PERŞEMBE":
                day = DayOfWeek.Thursday;
                return true;
            case "CUMA":
                day = DayOfWeek.Friday;
                return true;
            case "CUMARTESI":
            case "CUMARTESİ":
                day = DayOfWeek.Saturday;
                return true;
            case "PAZAR":
                day = DayOfWeek.Sunday;
                return true;
            default:
                day = default;
                return false;
        }
    }

    private static Dictionary<string, Dictionary<string, string>> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (!result.ContainsKey(section))
                {
                    result[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            if (!result.ContainsKey(section))
            {
                result[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            result[section][key] = value;
        }

        return result;
    }

    private static string? ReadValue(Dictionary<string, Dictionary<string, string>> ini, string section, string key)
    {
        return ini.TryGetValue(section, out var values) && values.TryGetValue(key, out var value)
            ? value
            : null;
    }

    private static int ReadInt(Dictionary<string, Dictionary<string, string>> ini, string section, string key, int defaultValue)
    {
        return int.TryParse(ReadValue(ini, section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    private static bool ReadBool(Dictionary<string, Dictionary<string, string>> ini, string section, string key, bool defaultValue)
    {
        var value = ReadValue(ini, section, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Equals("YES", StringComparison.OrdinalIgnoreCase)
            || value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase);
    }
}
