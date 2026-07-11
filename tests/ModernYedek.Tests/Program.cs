using System.IO.Compression;
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
    ("Update manifest version check", TestUpdateManifest),
    ("License cache offline window", TestLicenseCache),
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

static async Task TestUpdateManifest()
{
    var manifestUrl = "https://updates.test/latest.json";
    using var http = new HttpClient(new FakeLicenseHttpHandler(new Dictionary<string, string>
    {
        [manifestUrl] = """
            {
              "version": "1.0.1",
              "mandatory": true,
              "url": "https://updates.test/releases/ModernYedek-1.0.1.zip",
              "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "notes": "unit test"
            }
            """
    }));

    var result = await new UpdateClient(http).CheckAsync(manifestUrl, new Version(1, 0, 0, 0));

    Assert(result.HasUpdate, "update available");
    Assert(result.Manifest is not null, "update manifest");
    Assert(result.Manifest!.Mandatory, "update mandatory");
    Assert(result.Manifest.Version == "1.0.1", "update version");
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
