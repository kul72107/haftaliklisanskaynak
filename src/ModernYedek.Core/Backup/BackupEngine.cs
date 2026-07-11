using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using ModernYedek.Core.Cloud;
using ModernYedek.Core.Logging;
using ModernYedek.Core.Models;

namespace ModernYedek.Core.Backup;

public sealed class BackupEngine
{
    private readonly IBackupLogger _logger;
    private readonly RetentionService _retentionService;

    public BackupEngine(IBackupLogger? logger = null, RetentionService? retentionService = null)
    {
        _logger = logger ?? new NullBackupLogger();
        _retentionService = retentionService ?? new RetentionService();
    }

    public async Task<BackupRunResult> RunAsync(
        BackupSettings settings,
        ICloudStorageClient? cloudClient = null,
        CancellationToken cancellationToken = default)
    {
        var result = new BackupRunResult
        {
            OperationId = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.Now
        };

        await LogAsync(result, BackupLogLevel.Info, "RUN_STARTED", "Yedekleme basladi.", cancellationToken: cancellationToken);

        var sources = settings.Sources.Where(source => source.Enabled).ToList();
        var targets = settings.Targets.Where(target => target.Enabled).ToList();
        if (sources.Count == 0)
        {
            await FailAsync(result, "NO_SOURCE", "Etkin kaynak yok.", cancellationToken);
            return result;
        }

        if (targets.Count == 0)
        {
            await FailAsync(result, "NO_TARGET", "Etkin hedef yok.", cancellationToken);
            return result;
        }

        var sourceValidationFailed = false;
        foreach (var source in sources)
        {
            var exists = source.Type == BackupSourceType.Folder
                ? Directory.Exists(source.Path)
                : File.Exists(source.Path);

            if (!exists)
            {
                sourceValidationFailed = true;
                await LogAsync(result, BackupLogLevel.Error, "SOURCE_NOT_FOUND", $"Kaynak bulunamadi: {source.Path}", source.Path, cancellationToken: cancellationToken);
            }
        }

        if (sourceValidationFailed)
        {
            await FailAsync(result, "VALIDATION_FAILED", "Kaynak dogrulamasi basarisiz.", cancellationToken);
            return result;
        }

        long estimatedBytes;
        try
        {
            estimatedBytes = EstimateSourceBytes(sources);
        }
        catch (Exception ex)
        {
            await FailAsync(result, "SOURCE_ESTIMATE_FAILED", $"Kaynak boyutu hesaplanamadi: {ex.Message}", cancellationToken);
            return result;
        }

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!PrepareTarget(target.Path, estimatedBytes, out var validationMessage))
            {
                await LogAsync(result, BackupLogLevel.Error, "TARGET_NOT_READY", validationMessage, target: target.Path, cancellationToken: cancellationToken);
                continue;
            }

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            var targetRunDirectory = Path.Combine(target.Path, stamp);
            var archiveName = SanitizeFileName(settings.ProfileName) + "-" + stamp + ".zip";
            var archivePath = Path.Combine(targetRunDirectory, archiveName);
            Directory.CreateDirectory(targetRunDirectory);

            var archiveResult = await Task.Run(() => CreateArchive(sources, archivePath, result.OperationId, cancellationToken), cancellationToken);
            result.FilesAdded += archiveResult.FilesAdded;
            result.FilesSkipped += archiveResult.FilesSkipped;

            foreach (var entry in archiveResult.Entries)
            {
                result.Entries.Add(entry);
                await _logger.WriteAsync(entry, cancellationToken);
            }

            if (!archiveResult.Success)
            {
                await LogAsync(result, BackupLogLevel.Error, "ZIP_FAILED", archiveResult.Message, target: target.Path, cancellationToken: cancellationToken);
                continue;
            }

            if (!ValidateArchive(archivePath, out var validateMessage))
            {
                await LogAsync(result, BackupLogLevel.Error, "ZIP_VALIDATE_FAILED", validateMessage, target: target.Path, cancellationToken: cancellationToken);
                continue;
            }

            var hash = await ComputeSha256Async(archivePath, cancellationToken);
            var info = new FileInfo(archivePath);
            result.ArchivePath ??= archivePath;
            result.Sha256 ??= hash;
            result.ArchiveBytes += info.Length;
            await LogAsync(result, BackupLogLevel.Info, "ZIP_SUCCESS", $"ZIP yedek olusturuldu. SHA256={hash}", target: archivePath, cancellationToken: cancellationToken);

            if (settings.Cloud.Enabled && settings.Cloud.UploadAfterBackup && cloudClient is not null)
            {
                await UploadToCloudAsync(settings, cloudClient, archivePath, hash, result, cancellationToken);
            }
        }

        foreach (var entry in _retentionService.Apply(settings, result.OperationId))
        {
            result.Entries.Add(entry);
            await _logger.WriteAsync(entry, cancellationToken);
        }

        result.FinishedAt = DateTimeOffset.Now;
        result.Outcome = result.ArchivePath is null
            ? BackupOutcome.Failed
            : result.FilesSkipped > 0 || result.Entries.Any(entry => entry.Level == BackupLogLevel.Warning || entry.Level == BackupLogLevel.Error)
                ? BackupOutcome.Partial
                : BackupOutcome.Success;

        await LogAsync(result, result.Outcome == BackupOutcome.Failed ? BackupLogLevel.Error : BackupLogLevel.Info, "RUN_FINISHED", $"Yedekleme bitti: {result.Outcome}", cancellationToken: cancellationToken);
        return result;
    }

    private async Task UploadToCloudAsync(
        BackupSettings settings,
        ICloudStorageClient cloudClient,
        string archivePath,
        string sha256,
        BackupRunResult result,
        CancellationToken cancellationToken)
    {
        var prefix = (settings.Cloud.ObjectPrefix ?? string.Empty).Trim('/').Replace('\\', '/');
        var objectName = string.IsNullOrWhiteSpace(prefix)
            ? Path.GetFileName(archivePath)
            : $"{prefix}/{DateTime.Now:yyyy/MM}/{Path.GetFileName(archivePath)}";

        var uploadResult = await cloudClient.UploadAsync(new CloudUploadRequest
        {
            BucketName = settings.Cloud.BucketName,
            ObjectName = objectName,
            FilePath = archivePath,
            Metadata =
            {
                ["sha256"] = sha256,
                ["created_at"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                ["profile"] = settings.ProfileName
            }
        }, cancellationToken);

        await LogAsync(
            result,
            uploadResult.Success ? BackupLogLevel.Info : BackupLogLevel.Error,
            uploadResult.Success ? "CLOUD_UPLOAD_SUCCESS" : "CLOUD_UPLOAD_FAILED",
            uploadResult.Message,
            target: uploadResult.ObjectName ?? settings.Cloud.BucketName,
            cancellationToken: cancellationToken);

        if (uploadResult.Success && settings.Cloud.DeleteLocalAfterUpload)
        {
            File.Delete(archivePath);
            await LogAsync(result, BackupLogLevel.Info, "LOCAL_ARCHIVE_DELETED", "Bulut yuklemesi sonrasi yerel ZIP silindi.", target: archivePath, cancellationToken: cancellationToken);
        }
    }

    private static ArchiveCreateResult CreateArchive(
        IReadOnlyCollection<BackupSource> sources,
        string archivePath,
        string operationId,
        CancellationToken cancellationToken)
    {
        var entries = new List<BackupLogEntry>();
        var filesAdded = 0;
        var filesSkipped = 0;

        try
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            var sourceIndex = 1;
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceRoot = $"Kaynak{sourceIndex:00}_{SanitizeFileName(Path.GetFileName(source.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))}";
                sourceIndex++;

                if (source.Type == BackupSourceType.File)
                {
                    if (TryAddFile(archive, source.Path, $"{sourceRoot}/{Path.GetFileName(source.Path)}", operationId, entries))
                    {
                        filesAdded++;
                    }
                    else
                    {
                        filesSkipped++;
                    }

                    continue;
                }

                foreach (var file in EnumerateFilesSafe(source.Path, operationId, entries))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(source.Path, file).Replace('\\', '/');
                    if (TryAddFile(archive, file, $"{sourceRoot}/{relative}", operationId, entries))
                    {
                        filesAdded++;
                    }
                    else
                    {
                        filesSkipped++;
                    }
                }
            }

            if (filesAdded == 0)
            {
                return new ArchiveCreateResult(false, "ZIP icine eklenecek okunabilir dosya bulunamadi.", filesAdded, filesSkipped, entries);
            }

            return new ArchiveCreateResult(true, "ZIP olusturuldu.", filesAdded, filesSkipped, entries);
        }
        catch (Exception ex)
        {
            return new ArchiveCreateResult(false, ex.Message, filesAdded, filesSkipped, entries);
        }
    }

    private static bool TryAddFile(ZipArchive archive, string filePath, string entryName, string operationId, List<BackupLogEntry> entries)
    {
        try
        {
            using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = File.GetLastWriteTime(filePath);
            using var output = entry.Open();
            input.CopyTo(output);
            return true;
        }
        catch (Exception ex)
        {
            entries.Add(CreateLog(BackupLogLevel.Warning, operationId, filePath, string.Empty, "FILE_SKIPPED", $"Dosya atlandi: {ex.Message}"));
            return false;
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string operationId, List<BackupLogEntry> entries)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;

            try
            {
                files = Directory.EnumerateFiles(directory).ToList();
                directories = Directory.EnumerateDirectories(directory).ToList();
            }
            catch (Exception ex)
            {
                entries.Add(CreateLog(BackupLogLevel.Warning, operationId, directory, string.Empty, "DIRECTORY_SKIPPED", $"Dizin okunamadi: {ex.Message}"));
                continue;
            }

            foreach (var child in directories)
            {
                pending.Push(child);
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private static bool PrepareTarget(string targetPath, long estimatedBytes, out string message)
    {
        try
        {
            Directory.CreateDirectory(targetPath);
            var probe = Path.Combine(targetPath, ".modern-yedek-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            var root = Path.GetPathRoot(Path.GetFullPath(targetPath));
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                var drive = new DriveInfo(root);
                var required = Math.Max(estimatedBytes / 2, 50 * 1024 * 1024);
                if (drive.AvailableFreeSpace < required)
                {
                    message = $"Hedef diskte yeterli bos alan yok. Gerekli yaklasik: {FormatBytes(required)}, bos: {FormatBytes(drive.AvailableFreeSpace)}.";
                    return false;
                }
            }

            message = "Hedef hazir.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Hedef hazirlanamadi: {ex.Message}";
            return false;
        }
    }

    private static long EstimateSourceBytes(IEnumerable<BackupSource> sources)
    {
        long total = 0;
        foreach (var source in sources)
        {
            if (source.Type == BackupSourceType.File)
            {
                total += new FileInfo(source.Path).Length;
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(source.Path, "*", SearchOption.AllDirectories))
            {
                total += new FileInfo(file).Length;
            }
        }

        return total;
    }

    private static bool ValidateArchive(string archivePath, out string message)
    {
        try
        {
            var file = new FileInfo(archivePath);
            if (!file.Exists || file.Length == 0)
            {
                message = "ZIP dosyasi bos veya yok.";
                return false;
            }

            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count == 0)
            {
                message = "ZIP icinde dosya yok.";
                return false;
            }

            message = "ZIP dogrulandi.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"ZIP acilamadi: {ex.Message}";
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private async Task FailAsync(BackupRunResult result, string code, string message, CancellationToken cancellationToken)
    {
        result.Outcome = BackupOutcome.Failed;
        result.FinishedAt = DateTimeOffset.Now;
        await LogAsync(result, BackupLogLevel.Error, code, message, cancellationToken: cancellationToken);
    }

    private async Task LogAsync(
        BackupRunResult result,
        BackupLogLevel level,
        string code,
        string message,
        string source = "",
        string target = "",
        CancellationToken cancellationToken = default)
    {
        var entry = CreateLog(level, result.OperationId, source, target, code, message);
        result.Entries.Add(entry);
        await _logger.WriteAsync(entry, cancellationToken);
    }

    private static BackupLogEntry CreateLog(BackupLogLevel level, string operationId, string source, string target, string code, string message)
    {
        return new BackupLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Level = level,
            OperationId = operationId,
            Source = source,
            Target = target,
            Code = code,
            Message = message
        };
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Yedek" : sanitized;
    }

    private static string FormatBytes(long bytes)
    {
        return bytes < 1024 * 1024
            ? $"{bytes / 1024.0:N1} KB"
            : $"{bytes / 1024.0 / 1024.0:N1} MB";
    }

    private sealed record ArchiveCreateResult(
        bool Success,
        string Message,
        int FilesAdded,
        int FilesSkipped,
        List<BackupLogEntry> Entries);
}
