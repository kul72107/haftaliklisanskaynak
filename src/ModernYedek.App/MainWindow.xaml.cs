using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using ModernYedek.Core.Backup;
using ModernYedek.Core.Cloud;
using ModernYedek.Core.Import;
using ModernYedek.Core.Licensing;
using ModernYedek.Core.Logging;
using ModernYedek.Core.Models;
using ModernYedek.Core.Scheduling;
using ModernYedek.Core.Security;
using ModernYedek.Core.Storage;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfButton = System.Windows.Controls.Button;
using WinForms = System.Windows.Forms;

namespace ModernYedek.App;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settingsService;
    private readonly DpapiSecretStore _secretStore;
    private readonly LicenseCacheService _licenseCacheService;
    private readonly JsonLinesBackupLogger _logger;
    private readonly DispatcherTimer _schedulerTimer;
    private BackupSettings _settings;
    private bool _isRunning;
    private string? _lastScheduleFireKey;

    public MainWindow()
    {
        InitializeComponent();

        _paths = AppPaths.ForCurrentUser();
        _settingsService = new SettingsService(_paths.SettingsFile);
        _secretStore = new DpapiSecretStore(_paths.SecretsFile);
        _licenseCacheService = new LicenseCacheService(_secretStore);
        _logger = new JsonLinesBackupLogger(_paths.LogFile);
        _settings = SettingsService.CreateDefault();

        _schedulerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _schedulerTimer.Tick += SchedulerTimer_Tick;
        _schedulerTimer.Start();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        StoragePathText.Text = $"Veri klasörü: {_paths.RootDirectory}";
        SettingsPathText.Text = $"Ayar dosyası: {_paths.SettingsFile}{Environment.NewLine}Secret dosyası: {_paths.SecretsFile}";

        try
        {
            if (!_settingsService.Exists && File.Exists(ImportPathBox.Text))
            {
                var import = new LegacyIniImporter().Import(ImportPathBox.Text);
                _settings = import.Settings;
                if (!string.IsNullOrWhiteSpace(import.MailPassword))
                {
                    await _secretStore.SetSecretAsync(SecretKeys.MailPassword, import.MailPassword);
                }

                await _settingsService.SaveAsync(_settings);
                StatusBarText.Text = "Eski yedekaldat.ini otomatik içe aktarıldı.";
            }
            else
            {
                _settings = await _settingsService.LoadAsync();
            }

            NormalizeLicenseSettings();
            BindSettings();
            await RefreshSecretStatusAsync();
            await RefreshLicenseStatusAsync();
            await RefreshLogsAsync();
            UpdateDashboard();
        }
        catch (Exception ex)
        {
            StatusBarText.Text = "Ayarlar yüklenemedi.";
            ShowError(ex.Message, "Modern Yedek");
        }
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not string page)
        {
            ShowWarning("Sayfa acilamadi. Lutfen menu dugmesini tekrar deneyin.");
            return;
        }

        switch (page)
        {
            case "Sources":
                ShowPage(SourcesPanel, "Kaynaklar", "Yedeklenecek klasör ve dosyaları yönetin.");
                break;
            case "Targets":
                ShowPage(TargetsPanel, "Hedefler", "Yerel hedef klasörleri ve rotasyonu ayarlayın.");
                break;
            case "Schedule":
                ShowPage(SchedulePanel, "Zamanlama", "Yedeklerin hangi gün ve saatlerde çalışacağını seçin.");
                break;
            case "Cloud":
                ShowPage(CloudPanel, "Bulut", "Google Cloud Storage bucket bağlantısını ayarlayın.");
                break;
            case "Security":
                ShowPage(SecurityPanel, "Güvenlik", "Parola ve key bilgilerini şifreli secret dosyasında saklayın.");
                break;
            case "License":
                ShowPage(LicensePanel, "Lisans", "Haftalik lisans anahtarini aktiflestirin ve dogrulayin.");
                break;
            case "Logs":
                ShowPage(LogsPanel, "Loglar", "Yedekleme işlemlerinin ayrıntılı kayıtlarını inceleyin.");
                break;
            case "Settings":
                ShowPage(SettingsPanel, "Ayarlar", "Profil, ZIP ve eski ayar içe aktarma seçenekleri.");
                break;
            default:
                ShowPage(DashboardPanel, "Dashboard", "Yedekleme durumunu ve önemli uyarıları tek ekrandan takip edin.");
                break;
        }
    }

    private void ShowPage(FrameworkElement selected, string title, string subtitle)
    {
        foreach (var panel in new[] { DashboardPanel, SourcesPanel, TargetsPanel, SchedulePanel, CloudPanel, SecurityPanel, LicensePanel, LogsPanel, SettingsPanel })
        {
            panel.Visibility = panel == selected ? Visibility.Visible : Visibility.Collapsed;
        }

        PageTitleText.Text = title;
        PageSubtitleText.Text = subtitle;
        StatusBarText.Text = $"{title} sayfasi acildi.";
    }

    private void ShowInfo(string message, string title = "Bilgi")
    {
        ShowPopup(message, title, MessageBoxImage.Information);
    }

    private void ShowSuccess(string message, string title = "Basarili")
    {
        ShowPopup(message, title, MessageBoxImage.Information);
    }

    private void ShowWarning(string message, string title = "Uyari")
    {
        ShowPopup(message, title, MessageBoxImage.Warning);
    }

    private void ShowError(string message, string title = "Hata")
    {
        ShowPopup(message, title, MessageBoxImage.Error);
    }

    private bool ConfirmAction(string message, string title = "Onay")
    {
        return System.Windows.MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void ShowPopup(string message, string title, MessageBoxImage image)
    {
        StatusBarText.Text = message;
        System.Windows.MessageBox.Show(this, message, title, MessageBoxButton.OK, image);
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CollectSettingsFromUi();
            await _settingsService.SaveAsync(_settings);
            BindSettings();
            UpdateDashboard();
            ShowSuccess("Ayarlar kaydedildi.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message, "Ayar kaydetme hatasi");
        }
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourceTypeBox.SelectedIndex == 1)
        {
            var dialog = new OpenFileDialog { Title = "Kaynak dosya seç" };
            if (dialog.ShowDialog(this) == true)
            {
                SourcePathBox.Text = dialog.FileName;
                ShowInfo($"Kaynak dosya secildi:{Environment.NewLine}{dialog.FileName}");
            }
            else
            {
                ShowInfo("Kaynak dosya secimi iptal edildi.");
            }

            return;
        }

        using var folderDialog = new WinForms.FolderBrowserDialog { Description = "Kaynak klasör seç" };
        if (folderDialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            SourcePathBox.Text = folderDialog.SelectedPath;
            ShowInfo($"Kaynak klasor secildi:{Environment.NewLine}{folderDialog.SelectedPath}");
        }
        else
        {
            ShowInfo("Kaynak klasor secimi iptal edildi.");
        }
    }

    private void AddSource_Click(object sender, RoutedEventArgs e)
    {
        var path = SourcePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowWarning("Kaynak yolu bos. Once bir dosya veya klasor secin.");
            return;
        }

        _settings.Sources.Add(new BackupSource
        {
            Path = path,
            Type = SourceTypeBox.SelectedIndex == 1 ? BackupSourceType.File : BackupSourceType.Folder,
            Enabled = true
        });
        SourcePathBox.Clear();
        BindSettings();
        UpdateDashboard();
        ShowSuccess($"Kaynak eklendi:{Environment.NewLine}{path}");
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        var index = SourcesList.SelectedIndex;
        if (index >= 0 && index < _settings.Sources.Count)
        {
            var removed = _settings.Sources[index].Path;
            if (!ConfirmAction($"Bu kaynak silinsin mi?{Environment.NewLine}{removed}"))
            {
                ShowInfo("Kaynak silme islemi iptal edildi.");
                return;
            }

            _settings.Sources.RemoveAt(index);
            BindSettings();
            UpdateDashboard();
            ShowSuccess($"Kaynak silindi:{Environment.NewLine}{removed}");
        }
        else
        {
            ShowWarning("Silmek icin once listeden bir kaynak secin.");
        }
    }

    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        using var folderDialog = new WinForms.FolderBrowserDialog { Description = "Hedef klasör seç" };
        if (folderDialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            TargetPathBox.Text = folderDialog.SelectedPath;
            ShowInfo($"Hedef klasor secildi:{Environment.NewLine}{folderDialog.SelectedPath}");
        }
        else
        {
            ShowInfo("Hedef klasor secimi iptal edildi.");
        }
    }

    private void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        var path = TargetPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowWarning("Hedef yolu bos. Once bir hedef klasor secin.");
            return;
        }

        _settings.Targets.Add(new BackupTarget { Path = path, Enabled = true });
        TargetPathBox.Clear();
        BindSettings();
        UpdateDashboard();
        ShowSuccess($"Hedef eklendi:{Environment.NewLine}{path}");
    }

    private void RemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        var index = TargetsList.SelectedIndex;
        if (index >= 0 && index < _settings.Targets.Count)
        {
            var removed = _settings.Targets[index].Path;
            if (!ConfirmAction($"Bu hedef silinsin mi?{Environment.NewLine}{removed}"))
            {
                ShowInfo("Hedef silme islemi iptal edildi.");
                return;
            }

            _settings.Targets.RemoveAt(index);
            BindSettings();
            UpdateDashboard();
            ShowSuccess($"Hedef silindi:{Environment.NewLine}{removed}");
        }
        else
        {
            ShowWarning("Silmek icin once listeden bir hedef secin.");
        }
    }

    private void AddScheduleTime_Click(object sender, RoutedEventArgs e)
    {
        var time = NewTimeBox.Text.Trim();
        if (!ScheduleCalculator.IsValidTime(time))
        {
            ShowWarning("Saat HH:mm formatinda olmali. Ornek: 18:00");
            return;
        }

        if (!_settings.Schedule.Times.Contains(time, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Schedule.Times.Add(time);
            _settings.Schedule.Times = _settings.Schedule.Times.OrderBy(value => value).ToList();
            BindSettings();
            UpdateDashboard();
            ShowSuccess($"Zamanlama saati eklendi: {time}");
            return;
        }

        ShowInfo($"Bu saat zaten listede var: {time}");
    }

    private void RemoveScheduleTime_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleTimesList.SelectedItem is string selected)
        {
            if (!ConfirmAction($"Bu zamanlama saati silinsin mi? {selected}"))
            {
                ShowInfo("Saat silme islemi iptal edildi.");
                return;
            }

            _settings.Schedule.Times.Remove(selected);
            BindSettings();
            UpdateDashboard();
            ShowSuccess($"Zamanlama saati silindi: {selected}");
        }
        else
        {
            ShowWarning("Silmek icin once listeden bir saat secin.");
        }
    }

    private async void ImportLegacy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = ImportPathBox.Text.Trim();
            var import = new LegacyIniImporter().Import(path);
            _settings = import.Settings;
            if (!string.IsNullOrWhiteSpace(import.MailPassword))
            {
                await _secretStore.SetSecretAsync(SecretKeys.MailPassword, import.MailPassword);
            }

            await _settingsService.SaveAsync(_settings);
            BindSettings();
            await RefreshSecretStatusAsync();
            await RefreshLogsAsync();
            UpdateDashboard();
            ShowSuccess($"Eski ayarlar ice aktarildi:{Environment.NewLine}{path}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message, "Ice aktarma hatasi");
        }
    }

    private async void ImportGoogleKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Google service account JSON seç",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            ShowInfo("Google service account JSON secimi iptal edildi.");
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            _ = new GoogleCloudStorageClient(json);
            await _secretStore.SetSecretAsync(SecretKeys.GoogleServiceAccountJson, json);
            await RefreshSecretStatusAsync();
            ShowSuccess("Google service account key sifreli olarak kaydedildi.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message, "Google key hatasi");
        }
    }

    private async void TestCloud_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CollectSettingsFromUi();
            var client = await CreateCloudClientAsync();
            if (client is null)
            {
                ShowWarning("Google key, bucket bilgisi veya bulut yukleme ayari eksik. Bulut sayfasini kontrol edin.");
                return;
            }

            StatusBarText.Text = "Google Cloud bağlantısı test ediliyor...";
            var result = await client.TestConnectionAsync(_settings.Cloud.BucketName);
            StatusBarText.Text = result.Message;
            GoogleKeyStatusText.Text = result.Message;
            if (result.Success)
            {
                ShowSuccess(result.Message, "Bulut baglantisi basarili");
            }
            else
            {
                ShowWarning(result.Message, "Bulut baglantisi uyarisi");
            }
        }
        catch (Exception ex)
        {
            StatusBarText.Text = "Bulut testi başarısız.";
            GoogleKeyStatusText.Text = ex.Message;
            ShowError(ex.Message, "Bulut testi basarisiz");
        }
    }

    private async void ActivateLicense_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CollectSettingsFromUi();
            var key = LicenseKeyBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                ShowWarning("Lisans anahtari gerekli. Size verilen keyi girip tekrar deneyin.");
                return;
            }

            LicenseStateText.Text = "Lisans aktiflestiriliyor...";
            var client = new LicenseClient(_settings.License.ApiBaseUrl);
            var result = await client.ActivateAsync(key, _settings.License.Email, MachineIdentity.Current());
            await SaveLicenseResultAsync(key, result);
            await _settingsService.SaveAsync(_settings);
            UpdateLicenseUi(result);
            StatusBarText.Text = result.Message;
            if (result.IsValid)
            {
                ShowSuccess($"Lisans aktiflestirildi.{Environment.NewLine}{result.Message}");
            }
            else
            {
                ShowWarning($"Lisans aktiflestirilemedi.{Environment.NewLine}{result.Message}");
            }
        }
        catch (Exception ex)
        {
            StatusBarText.Text = "Lisans aktiflestirme basarisiz.";
            LicenseDetailText.Text = ex.Message;
            ShowError(ex.Message, "Lisans aktiflestirme basarisiz");
        }
    }

    private async void ValidateLicense_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CollectSettingsFromUi();
            var key = LicenseKeyBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                ShowWarning("Lisans anahtari gerekli. Size verilen keyi girip tekrar deneyin.");
                return;
            }

            var result = await ValidateLicenseOnlineAsync(key);
            await SaveLicenseResultAsync(key, result);
            await _settingsService.SaveAsync(_settings);
            UpdateLicenseUi(result);
            StatusBarText.Text = result.Message;
            if (result.IsValid)
            {
                ShowSuccess($"Lisans dogrulandi.{Environment.NewLine}{result.Message}");
            }
            else
            {
                ShowWarning($"Lisans dogrulanamadi.{Environment.NewLine}{result.Message}");
            }
        }
        catch (Exception ex)
        {
            StatusBarText.Text = "Lisans dogrulama basarisiz.";
            LicenseDetailText.Text = ex.Message;
            ShowError(ex.Message, "Lisans dogrulama basarisiz");
        }
    }

    private async void TestLicenseApi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CollectSettingsFromUi();
            using var http = new HttpClient { BaseAddress = new Uri(_settings.License.ApiBaseUrl.TrimEnd('/') + "/") };
            using var response = await http.GetAsync("health");
            StatusBarText.Text = response.IsSuccessStatusCode
                ? "License API calisiyor."
                : $"License API HTTP {(int)response.StatusCode}.";
            if (response.IsSuccessStatusCode)
            {
                ShowSuccess("License API calisiyor.");
            }
            else
            {
                ShowWarning($"License API HTTP {(int)response.StatusCode} dondu.");
            }
        }
        catch (Exception ex)
        {
            StatusBarText.Text = "License API ulasilamadi.";
            LicenseDetailText.Text = ex.Message;
            ShowError(ex.Message, "License API ulasilamadi");
        }
    }

    private async void SaveSecrets_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var savedAny = false;
            if (!string.IsNullOrWhiteSpace(MailPasswordBox.Password))
            {
                await _secretStore.SetSecretAsync(SecretKeys.MailPassword, MailPasswordBox.Password);
                MailPasswordBox.Clear();
                savedAny = true;
            }

            if (!string.IsNullOrWhiteSpace(ZipPasswordBox.Password))
            {
                await _secretStore.SetSecretAsync(SecretKeys.ZipPassword, ZipPasswordBox.Password);
                ZipPasswordBox.Clear();
                savedAny = true;
            }

            if (!savedAny)
            {
                ShowWarning("Kaydedilecek mail parolasi veya ZIP parola notu girilmedi.");
                return;
            }

            await RefreshSecretStatusAsync();
            ShowSuccess("Secret bilgileri sifreli dosyaya kaydedildi.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message, "Secret kaydetme hatasi");
        }
    }

    private async void RunBackup_Click(object sender, RoutedEventArgs e)
    {
        await RunBackupAsync(triggeredBySchedule: false);
    }

    private async Task RunBackupAsync(bool triggeredBySchedule)
    {
        if (_isRunning)
        {
            ShowWarning("Zaten calisan bir yedekleme var. Mevcut islem bitmeden yeni yedek baslatilamaz.");
            return;
        }

        try
        {
            _isRunning = true;
            DashboardStatusText.Text = "Çalışıyor";
            StatusBarText.Text = triggeredBySchedule ? "Zamanlanmış yedek başladı." : "Yedek başlatıldı.";
            if (!triggeredBySchedule)
            {
                ShowInfo("Yedekleme baslatildi. Islem bitince sonuc bildirilecek.");
            }

            CollectSettingsFromUi();
            await _settingsService.SaveAsync(_settings);
            if (!await EnsureLicenseUsableAsync())
            {
                DashboardStatusText.Text = "Lisans gerekli";
                StatusBarText.Text = "Yedekleme icin aktif lisans gerekli.";
                ShowPage(LicensePanel, "Lisans", "Haftalik lisans anahtarini aktiflestirin ve dogrulayin.");
                if (!triggeredBySchedule)
                {
                    ShowWarning("Yedekleme icin aktif lisans gerekli. Lisans ekranindan key girip aktiflestirin.");
                }
                return;
            }

            var cloudClient = await CreateCloudClientAsync();
            var result = await new BackupEngine(_logger).RunAsync(_settings, cloudClient);

            DashboardStatusText.Text = result.Outcome.ToString();
            LastArchiveText.Text = result.ArchivePath is null
                ? "Yedek oluşturulamadı."
                : $"{result.ArchivePath}{Environment.NewLine}SHA256: {result.Sha256}{Environment.NewLine}Dosya: {FormatBytes(result.ArchiveBytes)}";
            ErrorSummaryText.Text = result.Entries.Any(entry => entry.Level == BackupLogLevel.Error)
                ? string.Join(Environment.NewLine, result.Entries.Where(entry => entry.Level == BackupLogLevel.Error).Take(3).Select(entry => $"{entry.Code}: {entry.Message}"))
                : "Kritik hata yok.";
            StatusBarText.Text = $"Yedek tamamlandı: {result.Outcome}";

            await RefreshLogsAsync();
            UpdateDashboard();
            if (!triggeredBySchedule)
            {
                var message = result.ArchivePath is null
                    ? $"Yedek tamamlandi: {result.Outcome}"
                    : $"Yedek tamamlandi: {result.Outcome}{Environment.NewLine}{result.ArchivePath}";
                if (result.Outcome == BackupOutcome.Success)
                {
                    ShowSuccess(message, "Yedekleme basarili");
                }
                else
                {
                    ShowWarning(message, "Yedekleme sonucu");
                }
            }
        }
        catch (Exception ex)
        {
            DashboardStatusText.Text = "Hata";
            StatusBarText.Text = ex.Message;
            if (!triggeredBySchedule)
            {
                ShowError(ex.Message, "Yedekleme hatasi");
            }
        }
        finally
        {
            _isRunning = false;
        }
    }

    private async Task<GoogleCloudStorageClient?> CreateCloudClientAsync()
    {
        if (!_settings.Cloud.Enabled || !_settings.Cloud.UploadAfterBackup || string.IsNullOrWhiteSpace(_settings.Cloud.BucketName))
        {
            return null;
        }

        var json = await _secretStore.GetSecretAsync(SecretKeys.GoogleServiceAccountJson);
        return string.IsNullOrWhiteSpace(json) ? null : new GoogleCloudStorageClient(json);
    }

    private async Task<bool> EnsureLicenseUsableAsync()
    {
        if (!_settings.License.Required)
        {
            return true;
        }

        var cache = await _licenseCacheService.LoadAsync();
        if (LicenseCacheService.CanUseOffline(cache, DateTimeOffset.UtcNow))
        {
            UpdateLicenseUi(cache!.LastResult);
            return true;
        }

        var key = !string.IsNullOrWhiteSpace(LicenseKeyBox.Text)
            ? LicenseKeyBox.Text.Trim()
            : cache?.LicenseKey ?? string.Empty;

        if (string.IsNullOrWhiteSpace(key))
        {
            LicenseStateText.Text = "Lisans gerekli";
            LicenseDetailText.Text = "Lisans anahtari girilmedi.";
            return false;
        }

        try
        {
            var result = await ValidateLicenseOnlineAsync(key);
            await SaveLicenseResultAsync(key, result);
            UpdateLicenseUi(result);
            return result.IsValid;
        }
        catch
        {
            cache = await _licenseCacheService.LoadAsync();
            if (LicenseCacheService.CanUseOffline(cache, DateTimeOffset.UtcNow))
            {
                UpdateLicenseUi(cache!.LastResult);
                return true;
            }

            LicenseStateText.Text = "Lisans dogrulanamadi";
            LicenseDetailText.Text = "License API ulasilamiyor ve offline izin suresi yok.";
            return false;
        }
    }

    private async Task<LicenseValidationResult> ValidateLicenseOnlineAsync(string licenseKey)
    {
        var client = new LicenseClient(_settings.License.ApiBaseUrl);
        var cache = await _licenseCacheService.LoadAsync();
        var instanceId = cache?.LastResult.InstanceId ?? string.Empty;
        return await client.ValidateAsync(licenseKey, _settings.License.Email, MachineIdentity.Current(), instanceId);
    }

    private async Task SaveLicenseResultAsync(string licenseKey, LicenseValidationResult result)
    {
        await _licenseCacheService.SaveAsync(new LicenseCache
        {
            LicenseKey = licenseKey,
            Email = _settings.License.Email,
            ApiBaseUrl = _settings.License.ApiBaseUrl,
            LastResult = result
        });
    }

    private async Task RefreshLicenseStatusAsync()
    {
        var cache = await _licenseCacheService.LoadAsync();
        if (cache is null)
        {
            LicenseRequiredCheck.IsChecked = _settings.License.Required;
            LicenseStateText.Text = "Lisans yok";
            LicenseDetailText.Text = "Lisans anahtari henuz aktiflestirilmedi.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.License.ApiBaseUrl))
        {
            _settings.License.ApiBaseUrl = cache.ApiBaseUrl;
        }

        LicenseRequiredCheck.IsChecked = _settings.License.Required;
        LicenseKeyBox.Text = cache.LicenseKey;
        UpdateLicenseUi(cache.LastResult);
    }

    private void UpdateLicenseUi(LicenseValidationResult result)
    {
        LicenseStateText.Text = result.IsValid
            ? $"Lisans aktif: {result.State}"
            : $"Lisans gecersiz: {result.State}";

        var paidUntil = result.PaidUntil?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";
        var offlineUntil = result.OfflineUntil?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";
        LicenseDetailText.Text =
            $"{result.Message}{Environment.NewLine}" +
            $"Saglayici: {result.Provider}{Environment.NewLine}" +
            $"Plan: {result.Plan}{Environment.NewLine}" +
            $"Odeme bitis: {paidUntil}{Environment.NewLine}" +
            $"Offline izin: {offlineUntil}{Environment.NewLine}" +
            $"Cihaz: {result.ActivationCount}/{result.ActivationLimit}";
    }

    private async void RefreshLogs_Click(object sender, RoutedEventArgs e)
    {
        await RefreshLogsAsync();
        UpdateDashboard();
        var count = LogsList.Items.Count;
        ShowSuccess($"Loglar yenilendi. Gosterilen kayit sayisi: {count}");
    }

    private async void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmAction("Tum log kayitlari silinsin mi? Bu islem geri alinamaz."))
        {
            ShowInfo("Log silme islemi iptal edildi.");
            return;
        }

        await _logger.ClearAsync();
        await RefreshLogsAsync();
        LogDetailBox.Clear();
        ShowSuccess("Loglar silindi.");
    }

    private void LogsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogsList.SelectedItem is not LogListItem item)
        {
            return;
        }

        var entry = item.Entry;
        LogDetailBox.Text =
            $"Tarih: {entry.Timestamp:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"Seviye: {entry.Level}{Environment.NewLine}" +
            $"Kod: {entry.Code}{Environment.NewLine}" +
            $"Kaynak: {entry.Source}{Environment.NewLine}" +
            $"Hedef: {entry.Target}{Environment.NewLine}" +
            $"Mesaj: {entry.Message}{Environment.NewLine}" +
            $"İşlem: {entry.OperationId}";
    }

    private async void SchedulerTimer_Tick(object? sender, EventArgs e)
    {
        if (_isRunning || !_settings.Schedule.Enabled)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (!_settings.Schedule.Days.Contains(now.DayOfWeek))
        {
            return;
        }

        foreach (var value in _settings.Schedule.Times)
        {
            if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                continue;
            }

            if (now.Hour != time.Hour || now.Minute != time.Minute)
            {
                continue;
            }

            var fireKey = $"{now:yyyyMMdd}-{value}";
            if (_lastScheduleFireKey == fireKey)
            {
                return;
            }

            _lastScheduleFireKey = fireKey;
            await RunBackupAsync(triggeredBySchedule: true);
            return;
        }
    }

    private void BindSettings()
    {
        ProfileNameBox.Text = _settings.ProfileName;
        ZipEnabledCheck.IsChecked = _settings.ZipEnabled;

        SourcesList.ItemsSource = _settings.Sources.Select(FormatSource).ToList();
        TargetsList.ItemsSource = _settings.Targets.Select(target => target.Path).ToList();

        RetentionEnabledCheck.IsChecked = _settings.Retention.Enabled;
        KeepDaysBox.Text = _settings.Retention.KeepDays.ToString(CultureInfo.InvariantCulture);
        MaxGbBox.Text = _settings.Retention.MaxTotalSizeGb.ToString(CultureInfo.InvariantCulture);

        ScheduleEnabledCheck.IsChecked = _settings.Schedule.Enabled;
        DayMondayCheck.IsChecked = _settings.Schedule.Days.Contains(DayOfWeek.Monday);
        DayTuesdayCheck.IsChecked = _settings.Schedule.Days.Contains(DayOfWeek.Tuesday);
        DayWednesdayCheck.IsChecked = _settings.Schedule.Days.Contains(DayOfWeek.Wednesday);
        DayThursdayCheck.IsChecked = _settings.Schedule.Days.Contains(DayOfWeek.Thursday);
        DayFridayCheck.IsChecked = _settings.Schedule.Days.Contains(DayOfWeek.Friday);
        DaySaturdayCheck.IsChecked = _settings.Schedule.Days.Contains(DayOfWeek.Saturday);
        DaySundayCheck.IsChecked = _settings.Schedule.Days.Contains(DayOfWeek.Sunday);
        ScheduleTimesList.ItemsSource = _settings.Schedule.Times.ToList();

        CloudEnabledCheck.IsChecked = _settings.Cloud.Enabled;
        UploadAfterBackupCheck.IsChecked = _settings.Cloud.UploadAfterBackup;
        DeleteLocalAfterUploadCheck.IsChecked = _settings.Cloud.DeleteLocalAfterUpload;
        BucketBox.Text = _settings.Cloud.BucketName;
        PrefixBox.Text = _settings.Cloud.ObjectPrefix;

        LicenseRequiredCheck.IsChecked = _settings.License.Required;
    }

    private void CollectSettingsFromUi()
    {
        _settings.ProfileName = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? "Datasoft Yedek" : ProfileNameBox.Text.Trim();
        _settings.ZipEnabled = ZipEnabledCheck.IsChecked == true;

        _settings.Retention.Enabled = RetentionEnabledCheck.IsChecked == true;
        _settings.Retention.KeepDays = int.TryParse(KeepDaysBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var keepDays)
            ? Math.Max(1, keepDays)
            : 30;
        _settings.Retention.MaxTotalSizeGb = double.TryParse(MaxGbBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxGb)
            ? Math.Max(0, maxGb)
            : 50;

        _settings.Schedule.Enabled = ScheduleEnabledCheck.IsChecked == true;
        _settings.Schedule.Days = ReadSelectedDays();

        _settings.Cloud.Enabled = CloudEnabledCheck.IsChecked == true;
        _settings.Cloud.UploadAfterBackup = UploadAfterBackupCheck.IsChecked == true;
        _settings.Cloud.DeleteLocalAfterUpload = DeleteLocalAfterUploadCheck.IsChecked == true;
        _settings.Cloud.BucketName = BucketBox.Text.Trim();
        _settings.Cloud.ObjectPrefix = PrefixBox.Text.Trim();

        _settings.License.Required = LicenseRequiredCheck.IsChecked == true;
        NormalizeLicenseSettings();
    }

    private List<DayOfWeek> ReadSelectedDays()
    {
        var days = new List<DayOfWeek>();
        if (DayMondayCheck.IsChecked == true) days.Add(DayOfWeek.Monday);
        if (DayTuesdayCheck.IsChecked == true) days.Add(DayOfWeek.Tuesday);
        if (DayWednesdayCheck.IsChecked == true) days.Add(DayOfWeek.Wednesday);
        if (DayThursdayCheck.IsChecked == true) days.Add(DayOfWeek.Thursday);
        if (DayFridayCheck.IsChecked == true) days.Add(DayOfWeek.Friday);
        if (DaySaturdayCheck.IsChecked == true) days.Add(DayOfWeek.Saturday);
        if (DaySundayCheck.IsChecked == true) days.Add(DayOfWeek.Sunday);
        return days;
    }

    private async Task RefreshSecretStatusAsync()
    {
        var hasMail = await _secretStore.ContainsAsync(SecretKeys.MailPassword);
        var hasZip = await _secretStore.ContainsAsync(SecretKeys.ZipPassword);
        var hasGoogle = await _secretStore.ContainsAsync(SecretKeys.GoogleServiceAccountJson);

        SecretStatusText.Text = $"Mail parola: {(hasMail ? "var" : "yok")} | ZIP parola notu: {(hasZip ? "var" : "yok")} | Google key: {(hasGoogle ? "var" : "yok")}";
        GoogleKeyStatusText.Text = hasGoogle
            ? "Google service account key secrets.dat içinde şifreli saklanıyor."
            : "Google key yüklenmedi.";
    }

    private async Task RefreshLogsAsync()
    {
        var entries = await _logger.ReadRecentAsync(300);
        LogsList.ItemsSource = entries.Select(entry => new LogListItem(entry)).ToList();
    }

    private void UpdateDashboard()
    {
        var next = ScheduleCalculator.NextRun(_settings.Schedule, DateTimeOffset.Now);
        NextRunText.Text = next?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "Kapalı";

        CloudStateText.Text = _settings.Cloud.Enabled
            ? string.IsNullOrWhiteSpace(_settings.Cloud.BucketName) ? "Bucket eksik" : _settings.Cloud.BucketName
            : "Kapalı";

        DiskSpaceText.Text = GetDiskSummary();
    }

    private void NormalizeLicenseSettings()
    {
        if (string.IsNullOrWhiteSpace(_settings.License.ApiBaseUrl)
            || _settings.License.ApiBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || _settings.License.ApiBaseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || (_settings.License.ApiBaseUrl.Contains("serveousercontent.com", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(_settings.License.ApiBaseUrl.TrimEnd('/'), LicenseSettings.DefaultApiBaseUrl, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.License.ApiBaseUrl = LicenseSettings.DefaultApiBaseUrl;
        }

        _settings.License.Email = string.Empty;
    }

    private string GetDiskSummary()
    {
        var firstTarget = _settings.Targets.FirstOrDefault(target => target.Enabled && !string.IsNullOrWhiteSpace(target.Path));
        if (firstTarget is null)
        {
            return "Hedef yok";
        }

        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(firstTarget.Path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return firstTarget.Path;
            }

            var drive = new DriveInfo(root);
            return $"{root} boş: {FormatBytes(drive.AvailableFreeSpace)}";
        }
        catch
        {
            return "Okunamadı";
        }
    }

    private static string FormatSource(BackupSource source)
    {
        var type = source.Type == BackupSourceType.Folder ? "Klasör" : "Dosya";
        return $"{type}: {source.Path}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{bytes / 1024d / 1024d / 1024d:N2} GB";
        }

        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / 1024d / 1024d:N2} MB";
        }

        return $"{bytes / 1024d:N2} KB";
    }

    private sealed record LogListItem(BackupLogEntry Entry)
    {
        public override string ToString()
        {
            return $"{Entry.Timestamp:yyyy-MM-dd HH:mm:ss} [{Entry.Level}] {Entry.Code} - {Entry.Message}";
        }
    }
}
