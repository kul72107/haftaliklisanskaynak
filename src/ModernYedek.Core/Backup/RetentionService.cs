using ModernYedek.Core.Models;

namespace ModernYedek.Core.Backup;

public sealed class RetentionService
{
    public List<BackupLogEntry> Apply(BackupSettings settings, string operationId)
    {
        var logs = new List<BackupLogEntry>();
        if (!settings.Retention.Enabled)
        {
            return logs;
        }

        foreach (var target in settings.Targets.Where(target => target.Enabled && Directory.Exists(target.Path)))
        {
            var zipFiles = Directory
                .EnumerateFiles(target.Path, "*.zip", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            if (zipFiles.Count == 0)
            {
                continue;
            }

            var newest = zipFiles[0].FullName;
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, settings.Retention.KeepDays));
            foreach (var file in zipFiles.Where(file => file.LastWriteTimeUtc < cutoff && file.FullName != newest).ToList())
            {
                TryDelete(file, operationId, target.Path, logs, "RETENTION_OLD", "Eski yedek rotasyon ile silindi.");
            }

            zipFiles = Directory
                .EnumerateFiles(target.Path, "*.zip", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            var maxBytes = settings.Retention.MaxTotalSizeGb <= 0
                ? long.MaxValue
                : (long)(settings.Retention.MaxTotalSizeGb * 1024 * 1024 * 1024);

            var total = zipFiles.Sum(file => file.Length);
            foreach (var file in zipFiles.OrderBy(file => file.LastWriteTimeUtc).ToList())
            {
                if (total <= maxBytes || file.FullName == newest)
                {
                    continue;
                }

                var length = file.Length;
                if (TryDelete(file, operationId, target.Path, logs, "RETENTION_SIZE", "Toplam yedek boyutu siniri icin silindi."))
                {
                    total -= length;
                }
            }
        }

        return logs;
    }

    private static bool TryDelete(FileInfo file, string operationId, string target, List<BackupLogEntry> logs, string code, string message)
    {
        try
        {
            file.Delete();
            logs.Add(CreateLog(BackupLogLevel.Info, operationId, target, code, $"{message} Dosya: {file.FullName}"));
            return true;
        }
        catch (Exception ex)
        {
            logs.Add(CreateLog(BackupLogLevel.Warning, operationId, target, "RETENTION_DELETE_FAILED", $"{file.FullName} silinemedi: {ex.Message}"));
            return false;
        }
    }

    private static BackupLogEntry CreateLog(BackupLogLevel level, string operationId, string target, string code, string message)
    {
        return new BackupLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Level = level,
            OperationId = operationId,
            Target = target,
            Code = code,
            Message = message
        };
    }
}
