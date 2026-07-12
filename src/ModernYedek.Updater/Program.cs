using System.Diagnostics;
using System.IO.Compression;

var options = UpdaterOptions.Parse(args);
var logPath = Path.Combine(Path.GetTempPath(), "ModernYedekUpdater.log");

try
{
    Log(logPath, "Updater started.");
    options.Validate();

    await WaitForProcessExitAsync(options.ProcessId, TimeSpan.FromSeconds(90));

    var backupDirectory = CreateBackup(options.TargetDirectory);
    Log(logPath, $"Backup created: {backupDirectory}");

    var stagingDirectory = Path.Combine(Path.GetTempPath(), "ModernYedekUpdateStaging", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(stagingDirectory);
    ZipFile.ExtractToDirectory(options.ZipPath, stagingDirectory, overwriteFiles: true);

    try
    {
        CopyDirectory(stagingDirectory, options.TargetDirectory);
        Log(logPath, "Files copied.");
    }
    catch
    {
        RestoreBackup(backupDirectory, options.TargetDirectory);
        throw;
    }
    finally
    {
        TryDeleteDirectory(stagingDirectory);
    }

    StartApplication(options.ExePath);
    Log(logPath, "Application restarted.");
}
catch (Exception ex)
{
    Log(logPath, ex.ToString());
    TryShowError(ex.Message);
}

static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout)
{
    if (processId <= 0)
    {
        await Task.Delay(1200);
        return;
    }

    try
    {
        using var process = Process.GetProcessById(processId);
        using var cts = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cts.Token);
    }
    catch (ArgumentException)
    {
    }
    catch (OperationCanceledException)
    {
        throw new TimeoutException("Ana uygulama kapanmadigi icin guncelleme uygulanamadi.");
    }
}

static string CreateBackup(string targetDirectory)
{
    var backupRoot = Path.Combine(Path.GetTempPath(), "ModernYedekBackups");
    var backupDirectory = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
    Directory.CreateDirectory(backupDirectory);
    CopyDirectory(targetDirectory, backupDirectory);
    return backupDirectory;
}

static void RestoreBackup(string backupDirectory, string targetDirectory)
{
    CopyDirectory(backupDirectory, targetDirectory);
}

static void CopyDirectory(string sourceDirectory, string targetDirectory)
{
    Directory.CreateDirectory(targetDirectory);
    foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(sourceDirectory, directory);
        Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
    }

    foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(sourceDirectory, file);
        var target = Path.Combine(targetDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

static void StartApplication(string exePath)
{
    if (!File.Exists(exePath))
    {
        return;
    }

    Process.Start(new ProcessStartInfo
    {
        FileName = exePath,
        UseShellExecute = true,
        WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
    });
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch
    {
    }
}

static void Log(string logPath, string message)
{
    File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
}

static void TryShowError(string message)
{
    try
    {
        System.Windows.Forms.MessageBox.Show(
            message,
            "MYedek Guncelleme Hatasi",
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Error);
    }
    catch
    {
    }
}

sealed class UpdaterOptions
{
    public int ProcessId { get; private init; }
    public string ZipPath { get; private init; } = string.Empty;
    public string TargetDirectory { get; private init; } = string.Empty;
    public string ExePath { get; private init; } = string.Empty;

    public static UpdaterOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length - 1; index += 2)
        {
            if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                values[args[index][2..]] = args[index + 1];
            }
        }

        return new UpdaterOptions
        {
            ProcessId = values.TryGetValue("pid", out var pid) && int.TryParse(pid, out var value) ? value : 0,
            ZipPath = values.GetValueOrDefault("zip", string.Empty),
            TargetDirectory = values.GetValueOrDefault("target", string.Empty),
            ExePath = values.GetValueOrDefault("exe", string.Empty)
        };
    }

    public void Validate()
    {
        if (!File.Exists(ZipPath))
        {
            throw new FileNotFoundException("Guncelleme ZIP dosyasi bulunamadi.", ZipPath);
        }

        if (string.IsNullOrWhiteSpace(TargetDirectory) || !Directory.Exists(TargetDirectory))
        {
            throw new DirectoryNotFoundException("Uygulama klasoru bulunamadi.");
        }

        if (string.IsNullOrWhiteSpace(ExePath))
        {
            throw new InvalidOperationException("Ana uygulama yolu bos.");
        }
    }
}
