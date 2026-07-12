using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ModernYedek.Core.Backup;
using ModernYedek.Core.Cloud;
using ModernYedek.Core.Import;
using ModernYedek.Core.Licensing;
using ModernYedek.Core.Models;
using ModernYedek.Core.Security;
using ModernYedek.Core.Storage;
using ModernYedek.Core.Updates;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Legacy INI import", TestLegacyIniImport),
    ("Backup ZIP, validation, SHA256, cloud mock", TestBackupEngine),
    ("DPAPI secret store", TestSecretStore),
    ("Static TXT license activation", TestStaticTxtLicense),
    ("Static TXT license revocation", TestStaticTxtRevocation),
    ("Static TXT license validation keeps expiry", TestStaticTxtLicenseValidationKeepsExpiry),
    ("Admin panel custom duration", TestAdminPanelCustomDuration),
    ("ResurrectSoft copyright notice", TestResurrectSoftCopyrightNotice),
    ("Standard window chrome", TestStandardWindowChrome),
    ("Animated pattern background", TestAnimatedPatternBackground),
    ("Premium visual asset set", TestPremiumVisualAssetSet),
    ("Responsive visual constraints", TestResponsiveVisualConstraints),
    ("Update manifest URL uses CDN", TestUpdateManifestUrlUsesCdn),
    ("Update manifest version check", TestUpdateManifest),
    ("Update download avoids locked stale ZIP", TestUpdateDownloadAvoidsLockedStaleZip),
    ("License cache offline window", TestLicenseCache),
    ("Default app behavior", TestDefaultAppBehavior),
    ("Retention deletes old archives", TestRetention)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine(ex);
    }
}

return failed == 0 ? 0 : 1;

static async Task TestLegacyIniImport()
{
    var root = CreateTempRoot();
    var iniPath = Path.Combine(root, "yedekaldat.ini");
    await File.WriteAllTextAsync(iniPath, """
        [GUNSAY]
        SAY=2
        [SAATSAY]
        SAY=1
        [KLASORSAY]
        SAY=2
        [HEDEFKLASORSAY]
        SAY=1
        [DURUM]
        ZIPMI=YES
        PERIYODIK=YES
        [KLASORLER]
        KLASOR0=C:\Data
        KLASORTİP0=KLASÖR
        KLASOR1=C:\file.txt
        KLASORTİP1=DOSYA
        HEDEFKLASORLER0=D:\Backups
        [GUNLER]
        GUN0=Pazartesi
        GUN1=Cuma
        [SAATLER]
        SAAT0=18:00
        [MAIL]
        ADRES=test@example.com
        USER=mailer
        PASS=secret
        """);

    var result = new LegacyIniImporter().Import(iniPath);
    Assert(result.Settings.Sources.Count == 2, "source count");
    Assert(result.Settings.Sources[0].Type == BackupSourceType.Folder, "folder source");
    Assert(result.Settings.Sources[1].Type == BackupSourceType.File, "file source");
    Assert(result.Settings.Targets.Single().Path == @"D:\Backups", "target path");
    Assert(result.Settings.Schedule.Days.Contains(DayOfWeek.Monday), "monday");
    Assert(result.Settings.Schedule.Days.Contains(DayOfWeek.Friday), "friday");
    Assert(result.Settings.Schedule.Times.Single() == "18:00", "time");
    Assert(result.MailPassword == "secret", "mail password");
}

static async Task TestBackupEngine()
{
    var root = CreateTempRoot();
    var source = Path.Combine(root, "source");
    var nested = Path.Combine(source, "nested");
    var target = Path.Combine(root, "target");
    Directory.CreateDirectory(nested);
    await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "alpha");
    await File.WriteAllTextAsync(Path.Combine(nested, "b.txt"), "beta");

    var settings = new BackupSettings
    {
        ProfileName = "Test Profil",
        Sources = [new BackupSource { Path = source, Type = BackupSourceType.Folder, Enabled = true }],
        Targets = [new BackupTarget { Path = target, Enabled = true }],
        Cloud = new CloudSettings
        {
            Enabled = true,
            UploadAfterBackup = true,
            BucketName = "unit-test-bucket",
            ObjectPrefix = "unit"
        },
        Retention = new RetentionSettings { Enabled = false }
    };

    var cloud = new FakeCloudStorageClient();
    var result = await new BackupEngine().RunAsync(settings, cloud);

    Assert(result.Outcome == BackupOutcome.Success, "backup success");
    Assert(result.ArchivePath is not null && File.Exists(result.ArchivePath), "zip exists");
    Assert(!string.IsNullOrWhiteSpace(result.Sha256) && result.Sha256.Length == 64, "sha");
    Assert(result.FilesAdded == 2, "files added");
    Assert(cloud.Uploads.Count == 1, "cloud upload called");
    Assert(cloud.Uploads[0].Metadata.ContainsKey("sha256"), "metadata sha");

    using var archive = ZipFile.OpenRead(result.ArchivePath!);
    Assert(archive.Entries.Count == 2, "zip entries");
}

static async Task TestSecretStore()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var root = CreateTempRoot();
    var paths = AppPaths.ForDirectory(root);
    var store = new DpapiSecretStore(paths.SecretsFile);
    await store.SetSecretAsync(SecretKeys.GoogleServiceAccountJson, "plain-secret-value");

    var loaded = await store.GetSecretAsync(SecretKeys.GoogleServiceAccountJson);
    Assert(loaded == "plain-secret-value", "secret roundtrip");

    var bytes = await File.ReadAllBytesAsync(paths.SecretsFile);
    var raw = Convert.ToBase64String(bytes);
    Assert(!raw.Contains("plain-secret-value", StringComparison.OrdinalIgnoreCase), "secret not plain");
}

static async Task TestLicenseCache()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var root = CreateTempRoot();
    var paths = AppPaths.ForDirectory(root);
    var store = new DpapiSecretStore(paths.SecretsFile);
    var cache = new LicenseCacheService(store);

    await cache.SaveAsync(new LicenseCache
    {
        LicenseKey = "MY-TEST-TEST-TEST-TEST-TEST-TEST",
        Email = "test@example.com",
        ApiBaseUrl = "http://localhost:5088",
        LastResult = new LicenseValidationResult
        {
            IsValid = true,
            State = LicenseState.Active,
            Message = "ok",
            Provider = "manual",
            InstanceId = "inst_test",
            PaidUntil = DateTimeOffset.UtcNow.AddDays(7),
            OfflineUntil = DateTimeOffset.UtcNow.AddHours(72),
            ActivationLimit = 1,
            ActivationCount = 1
        }
    });

    var loaded = await cache.LoadAsync();
    Assert(loaded is not null, "license cache loaded");
    Assert(loaded!.Email == "test@example.com", "license cache email");
    Assert(loaded.LastResult.InstanceId == "inst_test", "license cache instance");
    Assert(LicenseCacheService.CanUseOffline(loaded, DateTimeOffset.UtcNow), "license cache usable offline");

    await cache.ClearAsync();
    loaded = await cache.LoadAsync();
    Assert(loaded is null, "license cache cleared");
}

static async Task TestStaticTxtLicense()
{
    var key = "MY-TXT-TEST-0001";
    var hash = StaticLicenseClient.HashLicenseKey(key);
    var listUrl = "https://license.test/licenses.txt";
    var revokedUrl = "https://license.test/revoked.txt";
    using var http = new HttpClient(new FakeLicenseHttpHandler(new Dictionary<string, string>
    {
        [listUrl] = $"{hash}|7|active|unit-test",
        [revokedUrl] = ""
    }));

    var result = await new StaticLicenseClient(http).ActivateAsync(key, new LicenseSettings
    {
        LicenseListUrl = listUrl,
        RevokedListUrl = revokedUrl
    }, "machine-test");

    Assert(result.IsValid, "static license valid");
    Assert(result.Provider == "github-pages-txt", "static license provider");
    Assert(result.LicenseId == hash, "static license hash");
    Assert(result.PaidUntil is not null && result.PaidUntil.Value > DateTimeOffset.UtcNow.AddDays(6), "static license paid until");
    Assert(result.OfflineUntil == result.PaidUntil, "static license offline until");
}

static async Task TestStaticTxtRevocation()
{
    var key = "MY-TXT-REVOKED-0001";
    var hash = StaticLicenseClient.HashLicenseKey(key);
    var listUrl = "https://license.test/licenses.txt";
    var revokedUrl = "https://license.test/revoked.txt";
    using var http = new HttpClient(new FakeLicenseHttpHandler(new Dictionary<string, string>
    {
        [listUrl] = $"{hash}|7|active|unit-test",
        [revokedUrl] = hash
    }));

    var client = new StaticLicenseClient(http);
    var revocation = await client.CheckRevocationAsync(hash, revokedUrl);
    var activation = await client.ActivateAsync(key, new LicenseSettings
    {
        LicenseListUrl = listUrl,
        RevokedListUrl = revokedUrl
    }, "machine-test");

    Assert(revocation.Success, "revocation check success");
    Assert(revocation.IsRevoked, "revocation found");
    Assert(!activation.IsValid, "revoked activation blocked");
    Assert(activation.State == LicenseState.Invalid, "revoked activation invalid");
}

static async Task TestStaticTxtLicenseValidationKeepsExpiry()
{
    var key = "MY-KEEP-EXPIRY";
    var hash = StaticLicenseClient.HashLicenseKey(key);
    var licenseUrl = "https://license.test/licenses.txt";
    var revokedUrl = "https://license.test/revoked.txt";
    var paidUntil = DateTimeOffset.UtcNow.AddDays(3);
    using var http = new HttpClient(new FakeLicenseHttpHandler(new Dictionary<string, string>
    {
        [licenseUrl] = $"{hash}|7|active|keep-expiry",
        [revokedUrl] = "# none"
    }));

    var client = new StaticLicenseClient(http);
    var result = await client.ValidateExistingAsync(key, new LicenseSettings
    {
        LicenseListUrl = licenseUrl,
        RevokedListUrl = revokedUrl
    }, "machine-1", new LicenseValidationResult
    {
        IsValid = true,
        State = LicenseState.Active,
        LicenseId = hash,
        InstanceId = "existing-instance",
        PaidUntil = paidUntil,
        OfflineUntil = paidUntil
    });

    Assert(result.IsValid, "existing validation valid");
    Assert(result.PaidUntil == paidUntil, "existing expiry preserved");

    using var missingHttp = new HttpClient(new FakeLicenseHttpHandler(new Dictionary<string, string>
    {
        [licenseUrl] = "# removed",
        [revokedUrl] = "# none"
    }));
    var missing = await new StaticLicenseClient(missingHttp).ValidateExistingAsync(key, new LicenseSettings
    {
        LicenseListUrl = licenseUrl,
        RevokedListUrl = revokedUrl
    }, "machine-1", result);

    Assert(!missing.IsValid, "removed license invalid");
    Assert(missing.Message.Contains("bulunamadi", StringComparison.OrdinalIgnoreCase), "removed license message");
}

static async Task TestAdminPanelCustomDuration()
{
    var html = await File.ReadAllTextAsync(Path.Combine("docs", "admin", "index.html"));
    Assert(html.Contains("<option value=\"custom\">Custom</option>", StringComparison.OrdinalIgnoreCase), "custom duration option");
    Assert(html.Contains("customDurationDays", StringComparison.OrdinalIgnoreCase), "custom duration input");
    Assert(html.Contains("durationMode === \"custom\"", StringComparison.OrdinalIgnoreCase), "custom duration logic");
}

static async Task TestResurrectSoftCopyrightNotice()
{
    var xaml = await File.ReadAllTextAsync(Path.Combine("src", "ModernYedek.App", "MainWindow.xaml"));
    Assert(xaml.Contains("resurrectsoft-banner.png", StringComparison.OrdinalIgnoreCase), "brand banner asset");
    Assert(xaml.Contains("© 2026 ResurrectSoft", StringComparison.Ordinal), "copyright date");
    Assert(xaml.Contains("Tüm hakları saklıdır", StringComparison.OrdinalIgnoreCase), "all rights reserved text");
}

static async Task TestStandardWindowChrome()
{
    var xaml = await File.ReadAllTextAsync(Path.Combine("src", "ModernYedek.App", "MainWindow.xaml"));
    var code = await File.ReadAllTextAsync(Path.Combine("src", "ModernYedek.App", "MainWindow.xaml.cs"));
    Assert(xaml.Contains("WindowStyle=\"SingleBorderWindow\"", StringComparison.OrdinalIgnoreCase), "native title bar xaml");
    Assert(xaml.Contains("ResizeMode=\"CanResizeWithGrip\"", StringComparison.OrdinalIgnoreCase), "resize grip xaml");
    Assert(code.Contains("EnsureStandardWindowChrome", StringComparison.Ordinal), "runtime title bar enforcement");
}

static async Task TestAnimatedPatternBackground()
{
    var xaml = await File.ReadAllTextAsync(Path.Combine("src", "ModernYedek.App", "MainWindow.xaml"));
    var csproj = await File.ReadAllTextAsync(Path.Combine("src", "ModernYedek.App", "ModernYedek.App.csproj"));
    Assert(xaml.Contains("Assets/premium-pattern.png", StringComparison.OrdinalIgnoreCase), "pattern brush image");
    Assert(xaml.Contains("ZipPatternBrushTransform", StringComparison.Ordinal), "pattern animation transform");
    Assert(xaml.Contains("RepeatBehavior=\"Forever\"", StringComparison.OrdinalIgnoreCase), "pattern loops");
    Assert(xaml.Contains("SidebarTextOutlineEffect", StringComparison.Ordinal), "sidebar text outline");
    Assert(csproj.Contains("Assets\\*.png", StringComparison.OrdinalIgnoreCase), "png resources included");
}

static async Task TestPremiumVisualAssetSet()
{
    var assets = Path.Combine("src", "ModernYedek.App", "Assets");
    var xaml = await File.ReadAllTextAsync(Path.Combine("src", "ModernYedek.App", "MainWindow.xaml"));
    var code = await File.ReadAllTextAsync(Path.Combine("src", "ModernYedek.App", "MainWindow.xaml.cs"));
    var csproj = await File.ReadAllTextAsync(Path.Combine("src", "ModernYedek.App", "ModernYedek.App.csproj"));

    Assert(ReadPngSize(Path.Combine(assets, "premium-pattern.png")) == (1024, 1024), "pattern dimensions");
    Assert(ReadPngSize(Path.Combine(assets, "premium-header-banner.png")) == (2400, 520), "header dimensions");
    Assert(ReadPngSize(Path.Combine(assets, "premium-sidebar-texture.png")) == (700, 1600), "sidebar dimensions");
    Assert(ReadPngSize(Path.Combine(assets, "myedek-icon-master.png")) == (1024, 1024), "icon master dimensions");
    Assert(ReadPngSize(Path.Combine(assets, "premium-divider.png")) == (1600, 80), "divider dimensions");
    Assert(ReadPngSize(Path.Combine(assets, "empty-sources.png")) == (1200, 700), "empty sources dimensions");
    Assert(ReadPngSize(Path.Combine(assets, "empty-targets.png")) == (1200, 700), "empty targets dimensions");
    Assert(ReadPngSize(Path.Combine(assets, "empty-logs.png")) == (1200, 700), "empty logs dimensions");

    foreach (var icon in new[]
    {
        "icon-backup-success.png",
        "icon-backup-warning.png",
        "icon-backup-failed.png",
        "icon-cloud-upload.png",
        "icon-license-key.png",
        "icon-encrypted-secrets.png",
        "icon-schedule-clock.png",
        "icon-zip-archive.png"
    })
    {
        Assert(ReadPngSize(Path.Combine(assets, icon)) == (256, 256), $"icon dimensions {icon}");
    }

    Assert(File.Exists(Path.Combine(assets, "myedek.ico")), "ico exists");
    Assert(xaml.Contains("icon-backup-success.png", StringComparison.OrdinalIgnoreCase), "dashboard status icon referenced");
    Assert(xaml.Contains("icon-schedule-clock.png", StringComparison.OrdinalIgnoreCase), "dashboard schedule icon referenced");
    Assert(xaml.Contains("icon-zip-archive.png", StringComparison.OrdinalIgnoreCase), "dashboard zip icon referenced");
    Assert(xaml.Contains("icon-cloud-upload.png", StringComparison.OrdinalIgnoreCase), "dashboard cloud icon referenced");
    Assert(xaml.Contains("premium-header-banner.png", StringComparison.OrdinalIgnoreCase), "header asset referenced");
    Assert(xaml.Contains("premium-sidebar-texture.png", StringComparison.OrdinalIgnoreCase), "sidebar asset referenced");
    Assert(xaml.Contains("empty-sources.png", StringComparison.OrdinalIgnoreCase), "sources empty art referenced");
    Assert(xaml.Contains("empty-targets.png", StringComparison.OrdinalIgnoreCase), "targets empty art referenced");
    Assert(xaml.Contains("empty-logs.png", StringComparison.OrdinalIgnoreCase), "logs empty art referenced");
    Assert(code.Contains("SourcesEmptyArt.Visibility", StringComparison.Ordinal), "sources empty visibility");
    Assert(code.Contains("TargetsEmptyArt.Visibility", StringComparison.Ordinal), "targets empty visibility");
    Assert(code.Contains("LogsEmptyArt.Visibility", StringComparison.Ordinal), "logs empty visibility");
    Assert(csproj.Contains("<ApplicationIcon>Assets\\myedek.ico</ApplicationIcon>", StringComparison.OrdinalIgnoreCase), "application icon");
}

static async Task TestResponsiveVisualConstraints()
{
    var xaml = await File.ReadAllTextAsync(Path.Combine("src", "ModernYedek.App", "MainWindow.xaml"));
    Assert(xaml.Contains("MinWidth=\"1100\"", StringComparison.OrdinalIgnoreCase), "min width below 1366");
    Assert(xaml.Contains("MinHeight=\"720\"", StringComparison.OrdinalIgnoreCase), "min height below 768");
    Assert(xaml.Contains("<ScrollViewer Grid.Row=\"1\" VerticalScrollBarVisibility=\"Auto\">", StringComparison.OrdinalIgnoreCase), "scrollable content");
    Assert(xaml.Contains("Opacity=\"0.10\"", StringComparison.OrdinalIgnoreCase), "subtle background opacity");
    Assert(xaml.Contains("<ColumnDefinition Width=\"236\"/>", StringComparison.OrdinalIgnoreCase), "bounded sidebar width");
    Assert(xaml.Contains("<RowDefinition Height=\"136\"/>", StringComparison.OrdinalIgnoreCase), "bounded header height");
}

static async Task TestUpdateManifestUrlUsesCdn()
{
    var code = await File.ReadAllTextAsync(Path.Combine("src", "ModernYedek.App", "MainWindow.xaml.cs"));
    Assert(UpdateSettings.DefaultManifestUrl.Contains("cdn.jsdelivr.net", StringComparison.OrdinalIgnoreCase), "default update cdn");
    Assert(!UpdateSettings.DefaultManifestUrl.Contains("@main", StringComparison.OrdinalIgnoreCase), "default update cdn avoids main cache");
    Assert(code.Contains("LegacyRawManifestUrl", StringComparison.Ordinal), "legacy raw migration");
    Assert(code.Contains("LegacyCdnMainManifestUrl", StringComparison.Ordinal), "legacy cdn main migration");
}

static async Task TestUpdateManifest()
{
    var manifestUrl = "https://updates.test/latest.json";
    using var http = new HttpClient(new FakeLicenseHttpHandler(new Dictionary<string, string>
    {
        [manifestUrl] = """
            {
              "version": "1.0.18",
              "mandatory": true,
              "url": "https://updates.test/releases/ModernYedek-1.0.18.zip",
              "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "notes": "unit test"
            }
            """
    }));

    var result = await new UpdateClient(http).CheckAsync(manifestUrl, new Version(1, 0, 0, 0));

    Assert(result.HasUpdate, "update available");
    Assert(result.Manifest is not null, "update manifest");
    Assert(result.Manifest!.Mandatory, "update mandatory");
    Assert(result.Manifest.Version == "1.0.18", "update version");
}

static async Task TestUpdateDownloadAvoidsLockedStaleZip()
{
    var root = CreateTempRoot();
    var url = "https://updates.test/releases/ModernYedek-1.0.18.zip";
    var payload = "fake update payload";
    var sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    var staleZipPath = Path.Combine(root, "ModernYedek-1.0.18.zip");
    await File.WriteAllTextAsync(staleZipPath, "locked old file");

    await using var locked = new FileStream(staleZipPath, FileMode.Open, FileAccess.Read, FileShare.None);
    using var http = new HttpClient(new FakeLicenseHttpHandler(new Dictionary<string, string>
    {
        [url] = payload
    }));

    var path = await new UpdateClient(http).DownloadAndVerifyAsync(new UpdateManifest
    {
        Version = "1.0.18",
        Url = url,
        Sha256 = sha256
    }, root);

    Assert(File.Exists(path), "downloaded update exists");
    Assert(!string.Equals(path, staleZipPath, StringComparison.OrdinalIgnoreCase), "download path is unique");
    Assert(Path.GetFileName(path).StartsWith("ModernYedek-1.0.18-", StringComparison.OrdinalIgnoreCase), "download path has version prefix");
    Assert(!File.Exists(path + ".download"), "partial download renamed");
}

static async Task TestDefaultAppBehavior()
{
    var root = CreateTempRoot();
    var service = new SettingsService(Path.Combine(root, "settings.json"));
    var settings = SettingsService.CreateDefault();

    Assert(settings.AppBehavior.MinimizeToTrayOnClose, "default tray on close");
    Assert(settings.License.Required, "license required by default");
    await service.SaveAsync(settings);

    var loaded = await service.LoadAsync();
    Assert(loaded.AppBehavior.MinimizeToTrayOnClose, "saved tray on close");
}

static Task TestRetention()
{
    var root = CreateTempRoot();
    var target = Path.Combine(root, "target");
    Directory.CreateDirectory(target);
    var oldZip = Path.Combine(target, "old.zip");
    var newZip = Path.Combine(target, "new.zip");
    File.WriteAllText(oldZip, "old");
    File.WriteAllText(newZip, "new");
    File.SetLastWriteTimeUtc(oldZip, DateTime.UtcNow.AddDays(-10));
    File.SetLastWriteTimeUtc(newZip, DateTime.UtcNow);

    var settings = new BackupSettings
    {
        Targets = [new BackupTarget { Path = target, Enabled = true }],
        Retention = new RetentionSettings { Enabled = true, KeepDays = 2, MaxTotalSizeGb = 100 }
    };

    new RetentionService().Apply(settings, "test-op");

    Assert(!File.Exists(oldZip), "old deleted");
    Assert(File.Exists(newZip), "new kept");
    return Task.CompletedTask;
}

static string CreateTempRoot()
{
    var path = Path.Combine(Path.GetTempPath(), "ModernYedekTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static (int Width, int Height) ReadPngSize(string path)
{
    Assert(File.Exists(path), $"png exists {path}");
    var bytes = File.ReadAllBytes(path);
    Assert(bytes.Length >= 24, $"png header length {path}");
    Assert(bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47, $"png signature {path}");
    var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
    var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
    return (width, height);
}

static void Assert(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Assertion failed: {name}");
    }
}

sealed class FakeCloudStorageClient : ICloudStorageClient
{
    public List<CloudUploadRequest> Uploads { get; } = [];

    public Task<CloudUploadResult> TestConnectionAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new CloudUploadResult { Success = true, Message = "ok" });
    }

    public Task<CloudUploadResult> UploadAsync(CloudUploadRequest request, CancellationToken cancellationToken = default)
    {
        Uploads.Add(request);
        return Task.FromResult(new CloudUploadResult
        {
            Success = true,
            ObjectName = request.ObjectName,
            Message = "uploaded"
        });
    }
}

sealed class FakeLicenseHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses;

    public FakeLicenseHttpHandler(Dictionary<string, string> responses)
    {
        _responses = responses;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;
        var response = new HttpResponseMessage(_responses.ContainsKey(url) ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.NotFound)
        {
            Content = new StringContent(_responses.TryGetValue(url, out var body) ? body : string.Empty)
        };
        return Task.FromResult(response);
    }
}
