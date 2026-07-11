using System.Text.Json;
using ModernYedek.Core.Models;
using ModernYedek.Core.Storage;

namespace ModernYedek.Core.Logging;

public interface IBackupLogger
{
    Task WriteAsync(BackupLogEntry entry, CancellationToken cancellationToken = default);
}

public sealed class JsonLinesBackupLogger : IBackupLogger
{
    private readonly string _logFile;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonLinesBackupLogger(string logFile)
    {
        _logFile = logFile;
    }

    public async Task WriteAsync(BackupLogEntry entry, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
        var line = JsonSerializer.Serialize(entry, JsonOptions.Compact);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_logFile, line + Environment.NewLine, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<BackupLogEntry>> ReadRecentAsync(int maxEntries = 200, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_logFile))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(_logFile, cancellationToken);
        return lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(maxEntries)
            .Select(line =>
            {
                try
                {
                    return JsonSerializer.Deserialize<BackupLogEntry>(line, JsonOptions.Compact);
                }
                catch
                {
                    return null;
                }
            })
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .OrderByDescending(entry => entry.Timestamp)
            .ToList();
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_logFile))
        {
            File.Delete(_logFile);
        }

        return Task.CompletedTask;
    }
}

public sealed class NullBackupLogger : IBackupLogger
{
    public Task WriteAsync(BackupLogEntry entry, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
