using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModernYedek.Core.Licensing;
using ModernYedek.Core.Storage;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var dataFile = Environment.GetEnvironmentVariable("MODERN_YEDEK_LICENSE_DB")
    ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "licenses.json");
var adminToken = Environment.GetEnvironmentVariable("MODERN_YEDEK_ADMIN_TOKEN") ?? "dev-admin-token";
var hashSalt = Environment.GetEnvironmentVariable("MODERN_YEDEK_LICENSE_SALT") ?? "dev-license-salt-change-me";
var store = new LicenseStore(dataFile, hashSalt);

app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    service = "ModernYedek.LicenseApi",
    mode = "manual-key"
}));

app.MapGet("/admin/keygen", () => Results.Content(AdminKeygenHtml(), "text/html; charset=utf-8"));

app.MapPost("/admin/licenses", async (HttpContext context, AdminCreateLicenseRequest request) =>
{
    if (!IsAdmin(context, adminToken))
    {
        return Results.Unauthorized();
    }

    var response = await store.CreateLicenseAsync(request);
    return Results.Ok(response);
});

app.MapPost("/admin/licenses/{licenseKey}/extend", async (HttpContext context, string licenseKey, AdminExtendLicenseRequest request) =>
{
    if (!IsAdmin(context, adminToken))
    {
        return Results.Unauthorized();
    }

    var response = await store.ExtendLicenseAsync(licenseKey, request);
    return response is null ? Results.NotFound(new { message = "License not found." }) : Results.Ok(response);
});

app.MapPost("/admin/licenses/{licenseKey}/cancel", async (HttpContext context, string licenseKey) =>
{
    if (!IsAdmin(context, adminToken))
    {
        return Results.Unauthorized();
    }

    var ok = await store.CancelLicenseAsync(licenseKey);
    return ok ? Results.Ok(new { message = "License canceled." }) : Results.NotFound(new { message = "License not found." });
});

app.MapPost("/license/activate", async (LicenseActivationRequest request) =>
{
    var result = await store.ActivateAsync(request);
    return Results.Ok(result);
});

app.MapPost("/license/validate", async (LicenseValidationRequest request) =>
{
    var result = await store.ValidateAsync(request);
    return Results.Ok(result);
});

var bindUrl = Environment.GetEnvironmentVariable("MODERN_YEDEK_BIND_URL")
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? "http://localhost:5088";
app.Run(bindUrl);

static bool IsAdmin(HttpContext context, string adminToken)
{
    return context.Request.Headers.TryGetValue("X-Admin-Token", out var provided)
        && string.Equals(provided.ToString(), adminToken, StringComparison.Ordinal);
}

static string AdminKeygenHtml()
{
    return """
<!doctype html>
<html lang="tr">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Modern Yedek Key Uret</title>
  <style>
    :root { color-scheme: light; --ink:#152033; --muted:#657085; --line:#d8dee8; --accent:#1f7a6d; --bg:#f4f7fb; }
    * { box-sizing: border-box; }
    body { margin:0; background:var(--bg); color:var(--ink); font-family: Segoe UI, Arial, sans-serif; }
    main { max-width:560px; margin:0 auto; padding:24px 16px 40px; }
    h1 { margin:0 0 6px; font-size:26px; }
    p { color:var(--muted); line-height:1.45; }
    .panel { background:white; border:1px solid var(--line); border-radius:8px; padding:18px; box-shadow:0 8px 28px rgba(21,32,51,.08); }
    label { display:block; margin-top:14px; font-weight:600; font-size:13px; }
    input, select, textarea { width:100%; height:42px; margin-top:6px; padding:9px 10px; border:1px solid var(--line); border-radius:7px; font:inherit; }
    textarea { height:72px; resize:vertical; }
    button { width:100%; height:44px; margin-top:18px; border:0; border-radius:7px; background:var(--accent); color:white; font-weight:700; font-size:15px; }
    button.secondary { background:#edf2f7; color:var(--ink); border:1px solid var(--line); }
    pre { white-space:pre-wrap; word-break:break-word; background:#101828; color:white; padding:14px; border-radius:8px; min-height:72px; }
    .row { display:grid; grid-template-columns:1fr 1fr; gap:10px; }
    .ok { color:#0b7a42; }
    .error { color:#b42318; }
  </style>
</head>
<body>
  <main>
    <h1>Modern Yedek Key Uret</h1>
    <p>Bu panel tek kullanimlik manuel lisans key uretir. Key ilk aktivasyonda girilen bilgisayara baglanir ve sure o anda baslar.</p>
    <section class="panel">
      <label>Admin token</label>
      <input id="token" type="password" autocomplete="off" placeholder="MODERN_YEDEK_ADMIN_TOKEN">

      <label>Musteri email</label>
      <input id="email" type="email" placeholder="musteri@example.com">

      <div class="row">
        <div>
          <label>Sure</label>
          <input id="days" type="number" min="1" value="7">
        </div>
        <div>
          <label>Cihaz limiti</label>
          <input id="limit" type="number" min="1" value="1">
        </div>
      </div>

      <label>Plan</label>
      <input id="plan" value="weekly_pro">

      <label>Not</label>
      <textarea id="notes" placeholder="Opsiyonel"></textarea>

      <button id="generate">Yeni key olustur</button>
      <button class="secondary" id="copy" type="button">Key kopyala</button>

      <p id="status"></p>
      <pre id="result">Key burada gorunecek.</pre>
    </section>
  </main>
  <script>
    const result = document.getElementById('result');
    const status = document.getElementById('status');
    let lastKey = '';

    document.getElementById('generate').addEventListener('click', async () => {
      status.textContent = 'Key uretiliyor...';
      status.className = '';
      result.textContent = '';
      lastKey = '';

      const token = document.getElementById('token').value.trim();
      const payload = {
        email: document.getElementById('email').value.trim(),
        days: Number(document.getElementById('days').value || 7),
        activationLimit: Number(document.getElementById('limit').value || 1),
        plan: document.getElementById('plan').value.trim() || 'weekly_pro',
        startsOnActivation: true,
        notes: document.getElementById('notes').value.trim()
      };

      try {
        const response = await fetch('/admin/licenses', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-Admin-Token': token },
          body: JSON.stringify(payload)
        });

        if (!response.ok) {
          throw new Error('HTTP ' + response.status);
        }

        const data = await response.json();
        lastKey = data.licenseKey;
        status.textContent = 'Key hazir.';
        status.className = 'ok';
        result.textContent =
          'KEY: ' + data.licenseKey + '\n' +
          'Email: ' + data.email + '\n' +
          'Sure: ' + data.durationDays + ' gun\n' +
          'Cihaz: 0/' + data.activationLimit + '\n' +
          'Sure baslangici: Ilk aktivasyon';
      } catch (error) {
        status.textContent = 'Key uretilemedi: ' + error.message;
        status.className = 'error';
        result.textContent = '';
      }
    });

    document.getElementById('copy').addEventListener('click', async () => {
      if (!lastKey) return;
      await navigator.clipboard.writeText(lastKey);
      status.textContent = 'Key kopyalandi.';
      status.className = 'ok';
    });
  </script>
</body>
</html>
""";
}

public sealed class AdminCreateLicenseRequest
{
    public string Email { get; set; } = string.Empty;
    public string Plan { get; set; } = "weekly_pro";
    public int Days { get; set; } = 7;
    public int ActivationLimit { get; set; } = 1;
    public bool StartsOnActivation { get; set; } = true;
    public string Notes { get; set; } = string.Empty;
}

public sealed class AdminExtendLicenseRequest
{
    public int Days { get; set; } = 7;
}

public sealed class AdminLicenseResponse
{
    public string LicenseId { get; set; } = string.Empty;
    public string LicenseKey { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public DateTimeOffset PaidUntil { get; set; }
    public int DurationDays { get; set; }
    public int ActivationLimit { get; set; }
    public bool StartsOnActivation { get; set; }
    public DateTimeOffset? FirstActivatedAt { get; set; }
}

internal sealed class LicenseStore
{
    private readonly string _filePath;
    private readonly string _hashSalt;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LicenseStore(string filePath, string hashSalt)
    {
        _filePath = filePath;
        _hashSalt = hashSalt;
    }

    public async Task<AdminLicenseResponse> CreateLicenseAsync(AdminCreateLicenseRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            var db = await LoadAsync();
            var licenseKey = GenerateLicenseKey();
            var now = DateTimeOffset.UtcNow;
            var durationDays = Math.Max(1, request.Days);
            var record = new LicenseRecord
            {
                LicenseId = "lic_" + Guid.NewGuid().ToString("N"),
                KeyHash = HashKey(licenseKey),
                Email = NormalizeEmail(request.Email),
                Plan = string.IsNullOrWhiteSpace(request.Plan) ? "weekly_pro" : request.Plan.Trim(),
                Status = "active",
                PaidUntil = now.AddDays(durationDays),
                DurationDays = durationDays,
                StartsOnActivation = request.StartsOnActivation,
                ActivationLimit = Math.Max(1, request.ActivationLimit),
                Notes = request.Notes.Trim(),
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Licenses.Add(record);
            await SaveAsync(db);
            return ToAdminResponse(record, licenseKey);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AdminLicenseResponse?> ExtendLicenseAsync(string licenseKey, AdminExtendLicenseRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            var db = await LoadAsync();
            var record = FindByKey(db, licenseKey);
            if (record is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var days = Math.Max(1, request.Days);
            if (record.StartsOnActivation && record.FirstActivatedAt is null)
            {
                record.DurationDays += days;
                record.PaidUntil = now.AddDays(record.DurationDays);
            }
            else
            {
                var start = record.PaidUntil > now ? record.PaidUntil : now;
                record.PaidUntil = start.AddDays(days);
            }

            record.Status = "active";
            record.UpdatedAt = now;
            await SaveAsync(db);
            return ToAdminResponse(record, licenseKey);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> CancelLicenseAsync(string licenseKey)
    {
        await _lock.WaitAsync();
        try
        {
            var db = await LoadAsync();
            var record = FindByKey(db, licenseKey);
            if (record is null)
            {
                return false;
            }

            record.Status = "canceled";
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync(db);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<LicenseValidationResult> ActivateAsync(LicenseActivationRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            var db = await LoadAsync();
            var record = FindByKey(db, request.LicenseKey);
            if (record is null)
            {
                return Invalid("License key not found.");
            }

            if (!EmailMatches(record, request.Email))
            {
                return Invalid("Email does not match this license.");
            }

            var machineId = NormalizeMachineId(request.MachineId);
            if (string.IsNullOrWhiteSpace(machineId))
            {
                return Invalid("Machine id is empty.");
            }

            if (string.Equals(record.Status, "canceled", StringComparison.OrdinalIgnoreCase))
            {
                return BuildAccess(record);
            }

            var existing = record.Activations.FirstOrDefault(activation => activation.MachineId == machineId);
            if (existing is null)
            {
                if (record.Activations.Count >= record.ActivationLimit)
                {
                    return Invalid("Activation limit reached.");
                }

                var now = DateTimeOffset.UtcNow;
                if (record.StartsOnActivation && record.FirstActivatedAt is null)
                {
                    record.FirstActivatedAt = now;
                    record.PaidUntil = now.AddDays(Math.Max(1, record.DurationDays));
                }

                record.Activations.Add(new LicenseActivation
                {
                    MachineId = machineId,
                    ActivatedAt = now,
                    LastSeenAt = now
                });
            }
            else
            {
                existing.LastSeenAt = DateTimeOffset.UtcNow;
            }

            record.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync(db);
            return BuildAccess(record);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<LicenseValidationResult> ValidateAsync(LicenseValidationRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            var db = await LoadAsync();
            var record = FindByKey(db, request.LicenseKey);
            if (record is null)
            {
                return Invalid("License key not found.");
            }

            if (!EmailMatches(record, request.Email))
            {
                return Invalid("Email does not match this license.");
            }

            var machineId = NormalizeMachineId(request.MachineId);
            var activation = record.Activations.FirstOrDefault(item => item.MachineId == machineId);
            if (activation is null)
            {
                return Invalid("This machine is not activated.");
            }

            activation.LastSeenAt = DateTimeOffset.UtcNow;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync(db);
            return BuildAccess(record);
        }
        finally
        {
            _lock.Release();
        }
    }

    private LicenseValidationResult BuildAccess(LicenseRecord record)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(record.Status, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            return new LicenseValidationResult
            {
                IsValid = false,
                State = LicenseState.Canceled,
                Provider = "manual",
                LicenseId = record.LicenseId,
                CustomerEmail = record.Email,
                Plan = record.Plan,
                PaidUntil = record.PaidUntil,
                Message = "License is canceled.",
                ActivationLimit = record.ActivationLimit,
                ActivationCount = record.Activations.Count
            };
        }

        if (now > record.PaidUntil)
        {
            return new LicenseValidationResult
            {
                IsValid = false,
                State = LicenseState.Expired,
                Provider = "manual",
                LicenseId = record.LicenseId,
                CustomerEmail = record.Email,
                Plan = record.Plan,
                PaidUntil = record.PaidUntil,
                Message = "License expired.",
                ActivationLimit = record.ActivationLimit,
                ActivationCount = record.Activations.Count
            };
        }

        var offlineUntil = Min(now.AddHours(72), record.PaidUntil);
        return new LicenseValidationResult
        {
            IsValid = true,
            State = LicenseState.Active,
            Provider = "manual",
            LicenseId = record.LicenseId,
            CustomerEmail = record.Email,
            Plan = record.Plan,
            PaidUntil = record.PaidUntil,
            OfflineUntil = offlineUntil,
            Message = "License active.",
            ActivationLimit = record.ActivationLimit,
            ActivationCount = record.Activations.Count
        };
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right)
    {
        return left <= right ? left : right;
    }

    private bool EmailMatches(LicenseRecord record, string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return true;
        }

        return string.Equals(record.Email, NormalizeEmail(email), StringComparison.OrdinalIgnoreCase);
    }

    private LicenseRecord? FindByKey(LicenseDatabase db, string licenseKey)
    {
        var hash = HashKey(licenseKey);
        return db.Licenses.FirstOrDefault(record => record.KeyHash == hash);
    }

    private string HashKey(string licenseKey)
    {
        var normalized = NormalizeLicenseKey(licenseKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_hashSalt + "|" + normalized)));
    }

    private async Task<LicenseDatabase> LoadAsync()
    {
        if (!File.Exists(_filePath))
        {
            return new LicenseDatabase();
        }

        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<LicenseDatabase>(stream, JsonOptions.Indented)
            ?? new LicenseDatabase();
    }

    private async Task SaveAsync(LicenseDatabase db)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, db, JsonOptions.Indented);
    }

    private static AdminLicenseResponse ToAdminResponse(LicenseRecord record, string licenseKey)
    {
        return new AdminLicenseResponse
        {
            LicenseId = record.LicenseId,
            LicenseKey = licenseKey,
            Email = record.Email,
            Plan = record.Plan,
            PaidUntil = record.PaidUntil,
            DurationDays = record.DurationDays,
            ActivationLimit = record.ActivationLimit,
            StartsOnActivation = record.StartsOnActivation,
            FirstActivatedAt = record.FirstActivatedAt
        };
    }

    private static LicenseValidationResult Invalid(string message)
    {
        return new LicenseValidationResult
        {
            IsValid = false,
            State = LicenseState.Invalid,
            Provider = "manual",
            Message = message
        };
    }

    private static string GenerateLicenseKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(12);
        var hex = Convert.ToHexString(bytes);
        return "MY-" + string.Join("-", Enumerable.Range(0, 6).Select(index => hex.Substring(index * 4, 4)));
    }

    private static string NormalizeLicenseKey(string value)
    {
        return value.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
    }

    private static string NormalizeEmail(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizeMachineId(string value)
    {
        return value.Trim().ToUpperInvariant();
    }
}

internal sealed class LicenseDatabase
{
    public List<LicenseRecord> Licenses { get; set; } = [];
}

internal sealed class LicenseRecord
{
    public string LicenseId { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Plan { get; set; } = "weekly_pro";
    public string Status { get; set; } = "active";
    public DateTimeOffset PaidUntil { get; set; }
    public int DurationDays { get; set; } = 7;
    public bool StartsOnActivation { get; set; } = true;
    public DateTimeOffset? FirstActivatedAt { get; set; }
    public int ActivationLimit { get; set; } = 1;
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<LicenseActivation> Activations { get; set; } = [];
}

internal sealed class LicenseActivation
{
    public string MachineId { get; set; } = string.Empty;
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
