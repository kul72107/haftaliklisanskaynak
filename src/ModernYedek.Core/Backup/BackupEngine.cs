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
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new BackupRunResult
        {
            OperationId = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.Now
        };

        ReportProgress(progress, 0, "Baslatiliyor", "Yedekleme basladi.", isIndeterminate: true);
        await LogAsync(result, BackupLogLevel.Info, "RUN_STARTED", "Yedekleme basladi.", cancellationToken: cancellationToken);

        var sources = settings.Sources.Where(source => source.Enabled).ToList();
        var targets = settings.Targets.Where(target => target.Enabled).ToList();
        if (sources.Count == 0)
        {
            ReportProgress(progress, 0, "Basarisiz", "Etkin kaynak yok.");
            await FailAsync(result, "NO_SOURCE", "Etkin kaynak yok.", cancellationToken);
            return result;
        }

        if (targets.Count == 0)
        {
            ReportProgress(progress, 0, "Basarisiz", "Etkin hedef yok.");
            await FailAsync(result, "NO_TARGET", "Etkin hedef yok.", cancellationToken);
            return result;
        }

        ReportProgress(progress, 3, "Kaynaklar kontrol ediliyor", $"Etkin kaynak kontrol ediliyor: {sources.Count}");
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
            ReportProgress(progress, 3, "Basarisiz", "Kaynak dogrulamasi basarisiz.");
            await FailAsync(result, "VALIDATION_FAILED", "Kaynak dogrulamasi basarisiz.", cancellationToken);
            return result;
        }

        long estimatedBytes;
        try
        {
            ReportProgress(progress, 6, "Kaynaklar olculuyor", "Toplam yedek boyutu hesaplaniyor.", isIndeterminate: true);
            estimatedBytes = EstimateSourceBytes(sources);
        }
        catch (Exception ex)
        {
            ReportProgress(progress, 6, "Basarisiz", $"Kaynak boyutu hesaplanamadi: {ex.Message}");
            await FailAsync(result, "SOURCE_ESTIMATE_FAILED", $"Kaynak boyutu hesaplanamadi: {ex.Message}", cancellationToken);
            return result;
        }

        var totalFiles = CountSourceFilesSafe(sources);
        var targetIndex = 0;
        foreach (var target in targets)
        {
            targetIndex++;
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(
                progress,
                10,
                "Hedef kontrol ediliyor",
                $"Hedef kontrol ediliyor ({targetIndex}/{targets.Count}): {target.Path}",
                totalFiles: totalFiles * targets.Count,
                targetPath: target.Path);

            if (!PrepareTarget(target.Path, estimatedBytes, out var validationMessage))
            {
                ReportProgress(
                    progress,
                    10,
                    "Hedef hazir degil",
                    validationMessage,
                    totalFiles: totalFiles * targets.Count,
                    targetPath: target.Path);
                await LogAsync(result, BackupLogLevel.Error, "TARGET_NOT_READY", validationMessage, target: target.Path, cancellationToken: cancellationToken);
                continue;
            }

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            var targetRunDirectory = Path.Combine(target.Path, stamp);
            var archiveName = SanitizeFileName(settings.ProfileName) + "-" + stamp + ".zip";
            var archivePath = Path.Combine(targetRunDirectory, archiveName);
            Directory.CreateDirectory(targetRunDirectory);

            var archiveProgress = new ArchiveProgressState(progress, estimatedBytes, totalFiles, targets.Count, targetIndex, target.Path);
            var archiveResult = await Task.Run(() => CreateArchive(sources, archivePath, result.OperationId, archiveProgress, cancellationToken), cancellationToken);
            result.FilesAdded += archiveResult.FilesAdded;
            result.FilesSkipped += archiveResult.FilesSkipped;

            foreach (var entry in archiveResult.Entries)
            {
                result.Entries.Add(entry);
                await _logger.WriteAsync(entry, cancellationToken);
            }

            if (!archiveResult.Success)
            {
                ReportProgress(
                    progress,
                    archiveProgress.CurrentPercent,
                    "ZIP olusturulamadi",
                    archiveResult.Message,
                    archiveProgress.FilesProcessed,
                    archiveProgress.TotalFiles,
                    targetPath: target.Path);
                await LogAsync(result, BackupLogLevel.Error, "ZIP_FAILED", archiveResult.Message, target: target.Path, cancellationToken: cancellationToken);
                continue;
            }

            var postTargetBase = 75 + (targetIndex - 1) * (15d / targets.Count);
            var postTargetStep = 15d / targets.Count;
            ReportProgress(
                progress,
                postTargetBase + postTargetStep * 0.25,
                "ZIP dogrulaniyor",
                $"ZIP dogrulaniyor: {archivePath}",
                archiveProgress.FilesProcessed,
                archiveProgress.TotalFiles,
                targetPath: target.Path);
            if (!ValidateArchive(archivePath, out var validateMessage))
            {
                ReportProgress(
                    progress,
                    postTargetBase + postTargetStep * 0.25,
                    "ZIP dogrulanamadi",
                    validateMessage,
                    archiveProgress.FilesProcessed,
                    archiveProgress.TotalFiles,
                    targetPath: target.Path);
                await LogAsync(result, BackupLogLevel.Error, "ZIP_VALIDATE_FAILED", validateMessage, target: target.Path, cancellationToken: cancellationToken);
                continue;
            }

            ReportProgress(
                progress,
                postTargetBase + postTargetStep * 0.50,
                "SHA256 hesaplaniyor",
                $"SHA256 hesaplaniyor: {archivePath}",
                archiveProgress.FilesProcessed,
                archiveProgress.TotalFiles,
                targetPath: target.Path);
            var hash = await ComputeSha256Async(archivePath, cancellationToken);
            var info = new FileInfo(archivePath);
            result.ArchivePath ??= archivePath;
            result.Sha256 ??= hash;
            result.ArchiveBytes += info.Length;
            await LogAsync(result, BackupLogLevel.Info, "ZIP_SUCCESS", $"ZIP yedek olusturuldu. SHA256={hash}", target: archivePath, cancellationToken: cancellationToken);

            if (settings.Cloud.Enabled && settings.Cloud.UploadAfterBackup && cloudClient is not null)
            {
                await UploadToCloudAsync(settings, cloudClient, archivePath, hash, result, progress, archiveProgress, postTargetBase + postTargetStep * 0.75, cancellationToken);
            }
        }

        ReportProgress(progress, 95, "Eski yedekler temizleniyor", "Retention kurallari uygulaniyor.", totalFiles: totalFiles * targets.Count);
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
        ReportProgress(
            progress,
            100,
            result.Outcome == BackupOutcome.Failed ? "Yedekleme basarisiz" : "Yedekleme tamamlandi",
            $"Yedekleme bitti: {result.Outcome}",
            totalFiles * targets.Count,
            totalFiles * targets.Count);
        return result;
    }

    private async Task UploadToCloudAsync(
        BackupSettings settings,
        ICloudStorageClient cloudClient,
        string archivePath,
        string sha256,
        BackupRunResult result,
        IProgress<BackupProgress>? progress,
        ArchiveProgressState archiveProgress,
        double percent,
        CancellationToken cancellationToken)
    {
        var prefix = (settings.Cloud.ObjectPrefix ?? string.Empty).Trim('/').Replace('\\', '/');
        var objectName = string.IsNullOrWhiteSpace(prefix)
            ? Path.GetFileName(archivePath)
            : $"{prefix}/{DateTime.Now:yyyy/MM}/{Path.GetFileName(archivePath)}";

        ReportProgress(
            progress,
            percent,
            "Buluta yukleniyor",
            $"Buluta yukleniyor: {objectName}",
            archiveProgress.FilesProcessed,
            archiveProgress.TotalFiles,
            targetPath: settings.Cloud.BucketName);
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
            ReportProgress(
                progress,
                percent,
                "Yerel ZIP siliniyor",
                "Bulut yuklemesi sonrasi yerel ZIP siliniyor.",
                archiveProgress.FilesProcessed,
                archiveProgress.TotalFiles,
                targetPath: archivePath);
            await LogAsync(result, BackupLogLevel.Info, "LOCAL_ARCHIVE_DELETED", "Bulut yuklemesi sonrasi yerel ZIP silindi.", target: archivePath, cancellationToken: cancellationToken);
        }
    }

    private static ArchiveCreateResult CreateArchive(
        IReadOnlyCollection<BackupSource> sources,
        string archivePath,
        string operationId,
        ArchiveProgressState progress,
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
                    if (TryAddFile(archive, source.Path, $"{sourceRoot}/{Path.GetFileName(source.Path)}", operationId, entries, progress))
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
                    if (TryAddFile(archive, file, $"{sourceRoot}/{relative}", operationId, entries, progress))
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

    private static bool TryAddFile(
        ZipArchive archive,
        string filePath,
        string entryName,
        string operationId,
        List<BackupLogEntry> entries,
        ArchiveProgressState progress)
    {
        var fileLength = 0L;
        var copiedBytes = 0L;
        try
        {
            fileLength = new FileInfo(filePath).Length;
            using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = File.GetLastWriteTime(filePath);
            using var output = entry.Open();
            var buffer = new byte[128 * 1024];
            int bytesRead;
            while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, bytesRead);
                copiedBytes += bytesRead;
                progress.AddBytes(bytesRead, filePath);
            }

            progress.CompleteFile(filePath);
            return true;
        }
        catch (Exception ex)
        {
            if (fileLength > copiedBytes)
            {
                progress.AddBytes(fileLength - copiedBytes, filePath);
            }

            progress.CompleteFile(filePath);
            entries.Add(CreateLog(BackupLogLevel.Warning, operationId, filePath, string.Empty, "FILE_SKIPPED", $"Dosya atlandi: {ex.Message}"));
            return false;
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string operationId, List<BackupLogEntry>? entries)
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
                entries?.Add(CreateLog(BackupLogLevel.Warning, operationId, directory, string.Empty, "DIRECTORY_SKIPPED", $"Dizin okunamadi: {ex.Message}"));
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

    private static int CountSourceFilesSafe(IEnumerable<BackupSource> sources)
    {
        var count = 0;
        foreach (var source in sources)
        {
            if (source.Type == BackupSourceType.File)
            {
                count++;
                continue;
            }

            foreach (var _ in EnumerateFilesSafe(source.Path, string.Empty, null))
            {
                count++;
            }
        }

        return count;
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

    private static void ReportProgress(
        IProgress<BackupProgress>? progress,
        double percent,
        string stage,
        string message,
        int filesProcessed = 0,
        int totalFiles = 0,
        string? currentFile = null,
        string? targetPath = null,
        bool isIndeterminate = false)
    {
        progress?.Report(new BackupProgress
        {
            Stage = stage,
            Message = message,
            Percent = Math.Clamp(percent, 0, 100),
            IsIndeterminate = isIndeterminate,
            FilesProcessed = filesProcessed,
            TotalFiles = totalFiles,
            CurrentFile = currentFile,
            TargetPath = targetPath
        });
    }

    private static string FormatBytes(long bytes)
    {
        return bytes < 1024 * 1024
            ? $"{bytes / 1024.0:N1} KB"
            : $"{bytes / 1024.0 / 1024.0:N1} MB";
    }

    private sealed class ArchiveProgressState
    {
        private const long ReportStepBytes = 1024 * 1024;
        private readonly IProgress<BackupProgress>? _progress;
        private readonly long _totalBytes;
        private readonly long _baseBytes;
        private readonly int _baseFiles;
        private readonly string _targetPath;
        private long _currentBytes;
        private long _lastReportedBytes;
        private int _currentFiles;

        public ArchiveProgressState(
            IProgress<BackupProgress>? progress,
            long sourceBytes,
            int sourceFiles,
            int targetCount,
            int targetIndex,
            string targetPath)
        {
            _progress = progress;
            _totalBytes = Math.Max(sourceBytes, 1) * Math.Max(targetCount, 1);
            _baseBytes = Math.Max(sourceBytes, 1) * Math.Max(targetIndex - 1, 0);
            _baseFiles = Math.Max(sourceFiles, 0) * Math.Max(targetIndex - 1, 0);
            TotalFiles = Math.Max(sourceFiles, 0) * Math.Max(targetCount, 1);
            _targetPath = targetPath;
            Report("ZIP olusturuluyor", $"ZIP olusturuluyor ({targetIndex}/{Math.Max(targetCount, 1)}): {targetPath}", force: true);
        }

        public int FilesProcessed => Math.Min(_baseFiles + _currentFiles, TotalFiles);

        public int TotalFiles { get; }

        public double CurrentPercent
        {
            get
            {
                var completedBytes = Math.Min(_baseBytes + _currentBytes, _totalBytes);
                return 15 + completedBytes / (double)_totalBytes * 60;
            }
        }

        public void AddBytes(long bytes, string currentFile)
        {
            if (bytes <= 0)
            {
                return;
            }

            _currentBytes += bytes;
            if (_currentBytes - _lastReportedBytes >= ReportStepBytes)
            {
                _lastReportedBytes = _currentBytes;
                Report("ZIP olusturuluyor", $"Dosya ekleniyor: {Path.GetFileName(currentFile)}", currentFile);
            }
        }

        public void CompleteFile(string currentFile)
        {
            _currentFiles++;
            Report("ZIP olusturuluyor", $"Dosya eklendi: {Path.GetFileName(currentFile)}", currentFile, force: true);
        }

        private void Report(string stage, string message, string? currentFile = null, bool force = false)
        {
            if (!force && _progress is null)
            {
                return;
            }

            ReportProgress(
                _progress,
                CurrentPercent,
                stage,
                message,
                FilesProcessed,
                TotalFiles,
                currentFile,
                _targetPath);
        }
    }

    private sealed record ArchiveCreateResult(
        bool Success,
        string Message,
        int FilesAdded,
        int FilesSkipped,
        List<BackupLogEntry> Entries);
}
