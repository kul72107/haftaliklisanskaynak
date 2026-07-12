using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
using ModernYedek.Core.Updates;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfButton = System.Windows.Controls.Button;
using WinForms = System.Windows.Forms;

namespace ModernYedek.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan RevocationCheckMaxAge = TimeSpan.FromHours(24);
    private const string LicenseRequiredBannerTitle = "LİSANS GEREKLİ";
    private const string LicenseUpgradeBannerTitle = "LİSANSI YÜKSELTMEK İSTİYORSANIZ";
    private readonly AppPaths _paths;
    private readonly SettingsService _settingsService;
    private readonly DpapiSecretStore _secretStore;
    private readonly LicenseCacheService _licenseCacheService;
    private readonly JsonLinesBackupLogger _logger;
    private readonly DispatcherTimer _schedulerTimer;
    private readonly DispatcherTimer _revocationTimer;
    private readonly DispatcherTimer _updateTimer;
    private WinForms.NotifyIcon? _trayIcon;
    private BackupSettings _settings;
    private bool _isRunning;
    private bool _isRevocationCheckRunning;
    private bool _isForegroundLicenseCheckRunning;
    private bool _isUpdateCheckRunning;
    private bool _isMovingToTray;
    private bool _forceClose;
    private DateTimeOffset _lastForegroundLicenseCheckAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastForegroundUpdateCheckAt = DateTimeOffset.MinValue;
    private string? _lastScheduleFireKey;

    public MainWindow()
    {
        InitializeComponent();
        EnsureStandardWindowChrome(visibleInTaskbar: true);

        _paths = AppPaths.ForCurrentUser();
        _settingsService = new SettingsService(_paths.SettingsFile);
        _secretStore = new DpapiSecretStore(_paths.SecretsFile);
        _licenseCacheService = new LicenseCacheService(_secretStore);
        _logger = new JsonLinesBackupLogger(_paths.LogFile);
        _settings = SettingsService.CreateDefault();
        ConfigureTrayIcon();

        _schedulerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _schedulerTimer.Tick += SchedulerTimer_Tick;
        _schedulerTimer.Start();

        _revocationTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _revocationTimer.Tick += RevocationTimer_Tick;
        _revocationTimer.Start();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _updateTimer.Tick += UpdateTimer_Tick;
        _updateTimer.Start();
    }

    private void EnsureStandardWindowChrome(bool visibleInTaskbar)
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = visibleInTaskbar;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureStandardWindowChrome(visibleInTaskbar: true);
        Directory.CreateDirectory(_paths.RootDirectory);
        StoragePathText.Text = $"Veri klasörü: {_paths.RootDirectory}";
        SettingsPathText.Text = $"Ayar dosyası: {_paths.SettingsFile}{Environment.NewLine}Secret dosyası: {_paths.SecretsFile}";

        try
        {
            if (!_settingsService.Exists && File.Exists(ImportPathBox.Text))
            {
                var import = new LegacyIniImporter().Import(ImportPathBox.Text);
                _settings = import.Settings;
                NormalizeAppBehaviorSettings();
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
            NormalizeAppBehaviorSettings();
            BindSettings();
            await RefreshSecretStatusAsync();
            await RefreshLicenseStatusAsync();
            await EnforceRevocationPolicyAsync(showPopup: true);
            await RefreshLogsAsync();
            UpdateDashboard();
            await CheckForUpdatesAsync(showNoUpdateStatus: true, allowOptionalPrompt: true);
        }
        catch (Exception ex)
        {
            StatusBarText.Text = "Ayarlar yüklenemedi.";
            ShowError(ex.Message, "MYedek");
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (!_isMovingToTray
            && WindowState == WindowState.Minimized
            && _settings.AppBehavior.MinimizeToTrayOnClose)
        {
            HideToTray(showBalloon: false);
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _ = CheckLicenseWhenWindowOpensAsync();
        _ = CheckUpdateWhenWindowOpensAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose && _settings.AppBehavior.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            HideToTray(showBalloon: true);
            return;
        }

        DisposeTrayIcon();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        DisposeTrayIcon();
        base.OnClosed(e);
    }

    private void ConfigureTrayIcon()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Aç", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("Şimdi Yedekle", null, (_, _) => Dispatcher.Invoke(() => _ = RunBackupAsync(triggeredBySchedule: false)));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "MYedek - ResurrectSoft",
            ContextMenuStrip = menu,
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private void HideToTray(bool showBalloon)
    {
        ConfigureTrayIcon();
        if (_trayIcon is null)
        {
            return;
        }

        _isMovingToTray = true;
        try
        {
            _trayIcon.Visible = true;
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
            Hide();
        }
        finally
        {
            _isMovingToTray = false;
        }

        StatusBarText.Text = "Uygulama sağ alt simge alanında arka planda çalışıyor.";
        if (showBalloon)
        {
            _trayIcon.ShowBalloonTip(
                3500,
                "MYedek",
                "Uygulama kapanmadı; sağ alt simge alanında çalışmaya devam ediyor.",
                WinForms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        ConfigureTrayIcon();

        _isMovingToTray = true;
        try
        {
            Show();
            EnsureStandardWindowChrome(visibleInTaskbar: true);
            WindowState = WindowState.Normal;
            Activate();
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
            }
        }
        finally
        {
            _isMovingToTray = false;
        }

        StatusBarText.Text = "Uygulama yeniden açıldı.";
        _ = CheckLicenseWhenWindowOpensAsync();
        _ = CheckUpdateWhenWindowOpensAsync();
    }

    public void ShowFromExternalActivation()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || _trayIcon?.Visible == true)
        {
            RestoreFromTray();
            return;
        }

        EnsureStandardWindowChrome(visibleInTaskbar: true);
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        _ = CheckLicenseWhenWindowOpensAsync();
        _ = CheckUpdateWhenWindowOpensAsync();
    }

    private void ExitApplication()
    {
        _forceClose = true;
        DisposeTrayIcon();
        System.Windows.Application.Current.Shutdown();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private async void RevocationTimer_Tick(object? sender, EventArgs e)
    {
        if (_isRevocationCheckRunning)
        {
            return;
        }

        try
        {
            _isRevocationCheckRunning = true;
            await EnforceRevocationPolicyAsync(showPopup: true);
        }
        finally
        {
            _isRevocationCheckRunning = false;
        }
    }

    private async void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        await CheckForUpdatesAsync(showNoUpdateStatus: false, allowOptionalPrompt: false);
    }

    private async Task CheckLicenseWhenWindowOpensAsync()
    {
        if (!IsLoaded || _isForegroundLicenseCheckRunning)
        {
            return;
        }

        if (DateTimeOffset.UtcNow - _lastForegroundLicenseCheckAt < TimeSpan.FromSeconds(15))
        {
            return;
        }

        var cache = await _licenseCacheService.LoadAsync();
        if (!LicenseCacheService.CanUseOffline(cache, DateTimeOffset.UtcNow))
        {
            return;
        }

        try
        {
            _isForegroundLicenseCheckRunning = true;
            _lastForegroundLicenseCheckAt = DateTimeOffset.UtcNow;
            await EnforceRevocationPolicyAsync(showPopup: true);
        }
        finally
        {
            _isForegroundLicenseCheckRunning = false;
        }
    }

    private async Task CheckUpdateWhenWindowOpensAsync()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (DateTimeOffset.UtcNow - _lastForegroundUpdateCheckAt < TimeSpan.FromSeconds(15))
        {
            return;
        }

        _lastForegroundUpdateCheckAt = DateTimeOffset.UtcNow;
        await CheckForUpdatesAsync(showNoUpdateStatus: false, allowOptionalPrompt: false);
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

    private void TooltipsEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var enabled = TooltipsEnabledCheck.IsChecked == true;
        SetTooltipsEnabled(enabled);
        StatusBarText.Text = enabled
            ? "Açıklama kutuları açıldı."
            : "Açıklama kutuları kapatıldı.";
    }

    private void SetTooltipsEnabled(bool enabled)
    {
        ApplyTooltipsEnabled(this, enabled, new HashSet<DependencyObject>());
        ToolTipService.SetIsEnabled(TooltipsEnabledCheck, true);
    }

    private void PatternOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PatternBackgroundLayer is null || PatternAccentLayer is null || PatternOpacityValueText is null)
        {
            return;
        }

        var value = Math.Round(e.NewValue, 2);
        PatternBackgroundLayer.Opacity = value;
        PatternAccentLayer.Opacity = Math.Round(Math.Min(0.20, value * 0.32), 3);
        PatternOpacityValueText.Text = value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void ApplyTooltipsEnabled(DependencyObject current, bool enabled, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(current))
        {
            return;
        }

        if (current is FrameworkElement element
            && !ReferenceEquals(element, TooltipsEnabledCheck)
            && element.ToolTip is not null)
        {
            ToolTipService.SetIsEnabled(element, enabled);
        }

        var visualChildren = 0;
        try
        {
            visualChildren = VisualTreeHelper.GetChildrenCount(current);
        }
        catch (InvalidOperationException)
        {
        }

        for (var i = 0; i < visualChildren; i++)
        {
            ApplyTooltipsEnabled(VisualTreeHelper.GetChild(current, i), enabled, visited);
        }

        if (current is FrameworkElement frameworkElement)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(frameworkElement))
            {
                if (child is DependencyObject dependencyObject)
                {
                    ApplyTooltipsEnabled(dependencyObject, enabled, visited);
                }
            }
        }
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
            NormalizeAppBehaviorSettings();
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
            var result = await ActivateStaticLicenseAsync(key);
            await SaveLicenseResultAsync(key, result);
            await _settingsService.SaveAsync(_settings);
            if (!result.IsValid && ShouldClearLocalLicenseResult(result))
            {
                ClearLocalLicenseUi(result.Message);
            }
            else
            {
                UpdateLicenseUi(result);
            }

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
            if (!result.IsValid && ShouldClearLocalLicenseResult(result))
            {
                ClearLocalLicenseUi(result.Message);
            }
            else
            {
                UpdateLicenseUi(result);
            }

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
            if (!await CheckForUpdatesAsync(showNoUpdateStatus: false, allowOptionalPrompt: false))
            {
                return;
            }

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
        _settings.License.Required = true;
        var cache = await _licenseCacheService.LoadAsync();
        if (LicenseCacheService.CanUseOffline(cache, DateTimeOffset.UtcNow))
        {
            var authority = await EnsureCachedLicenseStillAuthorizedAsync(cache!, showPopup: false);
            if (!authority)
            {
                return false;
            }

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
                var authority = await EnsureCachedLicenseStillAuthorizedAsync(cache!, showPopup: false);
                if (!authority)
                {
                    return false;
                }

                UpdateLicenseUi(cache!.LastResult);
                return true;
            }

            LicenseStateText.Text = "Lisans dogrulanamadi";
            LicenseDetailText.Text = "Lisans listesine ulasilamiyor ve yerel izin suresi yok.";
            return false;
        }
    }

    private async Task<LicenseValidationResult> ValidateLicenseOnlineAsync(string licenseKey)
    {
        var cache = await _licenseCacheService.LoadAsync();
        var existingResult = IsCachedLicenseForKey(cache, licenseKey)
            ? cache!.LastResult
            : null;

        return await new StaticLicenseClient().ValidateExistingAsync(
            licenseKey,
            _settings.License,
            MachineIdentity.Current(),
            existingResult);
    }

    private async Task<LicenseValidationResult> ActivateStaticLicenseAsync(string licenseKey)
    {
        var cache = await _licenseCacheService.LoadAsync();
        if (IsCachedLicenseForKey(cache, licenseKey)
            && !LicenseCacheService.CanUseOffline(cache, DateTimeOffset.UtcNow))
        {
            var paidUntil = cache!.LastResult.PaidUntil ?? cache.LastResult.OfflineUntil;
            if (paidUntil is not null && paidUntil <= DateTimeOffset.UtcNow)
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    State = LicenseState.Expired,
                    Message = "Bu key daha once bu bilgisayarda kullanilmis ve suresi dolmus. Yeni key gerekir.",
                    Provider = cache.LastResult.Provider,
                    LicenseId = cache.LastResult.LicenseId,
                    InstanceId = cache.LastResult.InstanceId,
                    CustomerEmail = cache.LastResult.CustomerEmail,
                    ProductId = cache.LastResult.ProductId,
                    VariantId = cache.LastResult.VariantId,
                    Plan = cache.LastResult.Plan,
                    Note = cache.LastResult.Note,
                    PaidUntil = paidUntil,
                    OfflineUntil = paidUntil,
                    ActivationLimit = cache.LastResult.ActivationLimit,
                    ActivationCount = cache.LastResult.ActivationCount
                };
            }
        }

        if (IsCachedLicenseUsableForKey(cache, licenseKey))
        {
            if (!await EnsureCachedLicenseStillAuthorizedAsync(cache!, showPopup: false))
            {
                return cache!.LastResult;
            }

            return new LicenseValidationResult
            {
                IsValid = true,
                State = cache!.LastResult.State,
                Message = "Lisans bu bilgisayarda zaten aktif. Sure bitene kadar internet listesinden tekrar onay gerekmez.",
                Provider = cache.LastResult.Provider,
                LicenseId = cache.LastResult.LicenseId,
                InstanceId = cache.LastResult.InstanceId,
                CustomerEmail = cache.LastResult.CustomerEmail,
                ProductId = cache.LastResult.ProductId,
                VariantId = cache.LastResult.VariantId,
                Plan = cache.LastResult.Plan,
                Note = cache.LastResult.Note,
                PaidUntil = cache.LastResult.PaidUntil,
                OfflineUntil = cache.LastResult.OfflineUntil,
                ActivationLimit = cache.LastResult.ActivationLimit,
                ActivationCount = cache.LastResult.ActivationCount
            };
        }

        var client = new StaticLicenseClient();
        var result = await client.ActivateAsync(licenseKey, _settings.License, MachineIdentity.Current());
        if (result.IsValid)
        {
            await TrySendActivationSignalAsync(result);
        }

        return result;
    }

    private async Task TrySendActivationSignalAsync(LicenseValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(_settings.License.ActivationSignalUrl))
        {
            return;
        }

        try
        {
            var signal = new LicenseActivationSignal
            {
                LicenseHash = result.LicenseId,
                MachineId = MachineIdentity.Current(),
                ComputerName = Environment.MachineName,
                WindowsUser = Environment.UserName,
                ActivatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = result.PaidUntil,
                Provider = result.Provider,
                AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown",
                Note = result.Note
            };

            _ = await new LicenseActivationSignalClient().SendAsync(_settings.License, signal);
        }
        catch
        {
            // Aktivasyon sinyali lisansin acilmasini engellemez.
        }
    }

    private async Task TrySendRevocationSignalAsync(string licenseHash, string note)
    {
        if (string.IsNullOrWhiteSpace(_settings.License.RevocationSignalUrl))
        {
            return;
        }

        try
        {
            var signal = new LicenseRevocationSignal
            {
                Revoked = true,
                LicenseHash = licenseHash,
                MachineId = MachineIdentity.Current(),
                ComputerName = Environment.MachineName,
                WindowsUser = Environment.UserName,
                RevokedAt = DateTimeOffset.UtcNow,
                AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown",
                Note = note
            };

            _ = await new LicenseRevocationSignalClient().SendAsync(_settings.License, signal);
        }
        catch
        {
            // Iptal sinyali yerel lisans imhasini engellemez.
        }
    }

    private static bool IsCachedLicenseUsableForKey(LicenseCache? cache, string licenseKey)
    {
        if (!LicenseCacheService.CanUseOffline(cache, DateTimeOffset.UtcNow))
        {
            return false;
        }

        return IsCachedLicenseForKey(cache, licenseKey);
    }

    private static bool IsCachedLicenseForKey(LicenseCache? cache, string licenseKey)
    {
        return cache is not null
            && string.Equals(cache.LicenseKey.Trim(), licenseKey.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnforceRevocationPolicyAsync(bool showPopup)
    {
        var cache = await _licenseCacheService.LoadAsync();
        if (!LicenseCacheService.CanUseOffline(cache, DateTimeOffset.UtcNow))
        {
            return;
        }

        _ = await EnsureCachedLicenseStillAuthorizedAsync(cache!, showPopup);
    }

    private async Task<bool> EnsureCachedLicenseStillAuthorizedAsync(LicenseCache cache, bool showPopup)
    {
        if (!cache.LastResult.IsValid)
        {
            return false;
        }

        var hash = !string.IsNullOrWhiteSpace(cache.LastResult.LicenseId)
            ? cache.LastResult.LicenseId.Trim().ToUpperInvariant()
            : StaticLicenseClient.HashLicenseKey(cache.LicenseKey);

        try
        {
            var result = await new StaticLicenseClient().ValidateExistingAsync(
                cache.LicenseKey,
                _settings.License,
                MachineIdentity.Current(),
                cache.LastResult);

            cache.LastRevocationCheckAt = DateTimeOffset.UtcNow;
            cache.LicenseListUrl = _settings.License.LicenseListUrl;
            cache.RevokedListUrl = _settings.License.RevokedListUrl;

            if (!result.IsValid)
            {
                var message = result.Message.Contains("bulunamadi", StringComparison.OrdinalIgnoreCase)
                    ? "Bu lisans artik lisans listesinde yok. Yerel lisans kaydi silindi; tekrar kullanmak icin yeni key gerekir."
                    : result.Message;
                await TrySendRevocationSignalAsync(hash, cache.LastResult.Note);
                await _licenseCacheService.ClearAsync();
                ClearLocalLicenseUi(message);
                if (showPopup)
                {
                    ShowWarning(message, "Lisans gecersiz");
                }

                return false;
            }

            cache.LastResult = result;
            await _licenseCacheService.SaveAsync(cache);
            return true;
        }
        catch
        {
            if (cache.LastRevocationCheckAt is not null
                && DateTimeOffset.UtcNow - cache.LastRevocationCheckAt.Value <= RevocationCheckMaxAge)
            {
                StatusBarText.Text = "Lisans listesi okunamadi, son kontrol 24 saatten yeni oldugu icin devam ediliyor.";
                return true;
            }

            LicenseStateText.Text = "Lisans kontrolu gerekli";
            LicenseDetailText.Text =
                "Lisans listesi 24 saatten uzun suredir kontrol edilemedi. " +
                "Lutfen internete baglanip lisansi tekrar dogrulayin.";
            if (showPopup)
            {
                ShowWarning(LicenseDetailText.Text, "Lisans kontrolu gerekli");
            }

            return false;
        }
    }

    private async Task SaveLicenseResultAsync(string licenseKey, LicenseValidationResult result)
    {
        var existing = await _licenseCacheService.LoadAsync();
        var hadSameCachedLicense = IsCachedLicenseForKey(existing, licenseKey);

        if (!result.IsValid)
        {
            if (hadSameCachedLicense && ShouldClearLocalLicenseResult(result))
            {
                var hash = !string.IsNullOrWhiteSpace(result.LicenseId)
                    ? result.LicenseId
                    : StaticLicenseClient.HashLicenseKey(licenseKey);
                await TrySendRevocationSignalAsync(hash, result.Note);
                await _licenseCacheService.ClearAsync();
                return;
            }

            if (hadSameCachedLicense && result.State == LicenseState.Expired)
            {
                existing!.LastResult = result;
                await _licenseCacheService.SaveAsync(existing);
            }

            return;
        }

        var lastRevocationCheckAt = existing?.LastRevocationCheckAt;
        if (result.IsValid
            && string.Equals(result.Provider, "github-pages-txt", StringComparison.OrdinalIgnoreCase)
            && result.Message.Contains("aktiflestirildi", StringComparison.OrdinalIgnoreCase))
        {
            lastRevocationCheckAt = DateTimeOffset.UtcNow;
        }

        await _licenseCacheService.SaveAsync(new LicenseCache
        {
            LicenseKey = licenseKey,
            Email = _settings.License.Email,
            ApiBaseUrl = _settings.License.ApiBaseUrl,
            LicenseListUrl = _settings.License.LicenseListUrl,
            RevokedListUrl = _settings.License.RevokedListUrl,
            LastRevocationCheckAt = lastRevocationCheckAt,
            LastResult = result
        });
    }

    private static bool ShouldClearLocalLicenseResult(LicenseValidationResult result)
    {
        return !result.IsValid
            && (result.State == LicenseState.Canceled
                || result.Message.Contains("iptal", StringComparison.OrdinalIgnoreCase)
                || result.Message.Contains("bulunamadi", StringComparison.OrdinalIgnoreCase)
                || result.Message.Contains("aktif degil", StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshLicenseStatusAsync()
    {
        var cache = await _licenseCacheService.LoadAsync();
        if (cache is null)
        {
            LicenseStateText.Text = "Lisans yok";
            LicenseDetailText.Text = "Lisans anahtari henuz aktiflestirilmedi.";
            UpdateLicenseBanner(hasActiveLicense: false);
            UpdateLicenseRemainingText(null);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.License.ApiBaseUrl))
        {
            _settings.License.ApiBaseUrl = cache.ApiBaseUrl;
        }

        if (string.IsNullOrWhiteSpace(_settings.License.LicenseListUrl))
        {
            _settings.License.LicenseListUrl = cache.LicenseListUrl;
        }

        if (string.IsNullOrWhiteSpace(_settings.License.RevokedListUrl))
        {
            _settings.License.RevokedListUrl = cache.RevokedListUrl;
        }

        LicenseKeyBox.Text = cache.LicenseKey;
        UpdateLicenseUi(cache.LastResult);
    }

    private void ClearLocalLicenseUi(string detail)
    {
        LicenseKeyBox.Clear();
        LicenseStateText.Text = "Lisans yok";
        LicenseDetailText.Text = detail;
        DashboardStatusText.Text = "Lisans gerekli";
        StatusBarText.Text = detail;
        UpdateLicenseBanner(hasActiveLicense: false);
        UpdateLicenseRemainingText(null);
    }

    private void UpdateLicenseUi(LicenseValidationResult result)
    {
        UpdateLicenseBanner(result.IsValid);
        UpdateLicenseRemainingText(result);
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

    private void UpdateLicenseBanner(bool hasActiveLicense)
    {
        LicenseBannerTitleText.Text = hasActiveLicense
            ? LicenseUpgradeBannerTitle
            : LicenseRequiredBannerTitle;
    }

    private void UpdateLicenseRemainingText(LicenseValidationResult? result)
    {
        if (result?.IsValid != true)
        {
            LicenseRemainingText.Text = "Lisans: yok";
            return;
        }

        var paidUntil = result.PaidUntil ?? result.OfflineUntil;
        if (paidUntil is null)
        {
            LicenseRemainingText.Text = "Lisans: aktif";
            return;
        }

        var remaining = paidUntil.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            LicenseRemainingText.Text = "Lisans: süre doldu";
            return;
        }

        var days = Math.Max(1, (int)Math.Ceiling(remaining.TotalDays));
        LicenseRemainingText.Text = $"Lisans: {days} gün kaldı";
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
        NormalizeAppBehaviorSettings();
        ProfileNameBox.Text = _settings.ProfileName;
        ZipEnabledCheck.IsChecked = _settings.ZipEnabled;
        MinimizeToTrayOnCloseCheck.IsChecked = _settings.AppBehavior.MinimizeToTrayOnClose;

        SourcesList.ItemsSource = _settings.Sources.Select(FormatSource).ToList();
        TargetsList.ItemsSource = _settings.Targets.Select(target => target.Path).ToList();
        SourcesEmptyArt.Visibility = _settings.Sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TargetsEmptyArt.Visibility = _settings.Targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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
    }

    private void CollectSettingsFromUi()
    {
        NormalizeAppBehaviorSettings();
        _settings.ProfileName = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? "Datasoft Yedek" : ProfileNameBox.Text.Trim();
        _settings.ZipEnabled = ZipEnabledCheck.IsChecked == true;
        _settings.AppBehavior.MinimizeToTrayOnClose = MinimizeToTrayOnCloseCheck.IsChecked == true;

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

        _settings.License.Required = true;
        NormalizeLicenseSettings();
        NormalizeUpdateSettings();
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
        LogsEmptyArt.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        _settings.License.Required = true;

        if (string.IsNullOrWhiteSpace(_settings.License.ApiBaseUrl)
            || _settings.License.ApiBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || _settings.License.ApiBaseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || (_settings.License.ApiBaseUrl.Contains("serveousercontent.com", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(_settings.License.ApiBaseUrl.TrimEnd('/'), LicenseSettings.DefaultApiBaseUrl, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.License.ApiBaseUrl = LicenseSettings.DefaultApiBaseUrl;
        }

        _settings.License.Email = string.Empty;
        if (string.IsNullOrWhiteSpace(_settings.License.LicenseListUrl))
        {
            _settings.License.LicenseListUrl = LicenseSettings.DefaultLicenseListUrl;
        }

        if (string.IsNullOrWhiteSpace(_settings.License.RevokedListUrl))
        {
            _settings.License.RevokedListUrl = LicenseSettings.DefaultRevokedListUrl;
        }

        if (string.IsNullOrWhiteSpace(_settings.License.ActivationSignalUrl))
        {
            _settings.License.ActivationSignalUrl = LicenseSettings.DefaultActivationSignalUrl;
        }

        if (_settings.License.ActivationSignalFields is null || _settings.License.ActivationSignalFields.Count == 0)
        {
            _settings.License.ActivationSignalFields = LicenseSettings.CreateDefaultActivationSignalFields();
        }

        if (string.IsNullOrWhiteSpace(_settings.License.RevocationSignalUrl))
        {
            _settings.License.RevocationSignalUrl = LicenseSettings.DefaultRevocationSignalUrl;
        }

        if (_settings.License.RevocationSignalFields is null || _settings.License.RevocationSignalFields.Count == 0)
        {
            _settings.License.RevocationSignalFields = LicenseSettings.CreateDefaultRevocationSignalFields();
        }
    }

    private async Task<bool> CheckForUpdatesAsync(bool showNoUpdateStatus, bool allowOptionalPrompt)
    {
        NormalizeUpdateSettings();
        if (!_settings.Update.Enabled)
        {
            return true;
        }

        if (_isUpdateCheckRunning)
        {
            return true;
        }

        try
        {
            _isUpdateCheckRunning = true;
            if (showNoUpdateStatus)
            {
                StatusBarText.Text = "Guncelleme kontrol ediliyor...";
            }

            var currentVersion = GetCurrentVersion();
            var result = await new UpdateClient().CheckAsync(_settings.Update.ManifestUrl, currentVersion);
            if (!result.HasUpdate || result.Manifest is null)
            {
                if (showNoUpdateStatus)
                {
                    StatusBarText.Text = result.Message;
                }

                return true;
            }

            var manifest = result.Manifest;
            var message =
                $"Yeni surum bulundu: {manifest.Version}{Environment.NewLine}" +
                $"Mevcut surum: {currentVersion}{Environment.NewLine}" +
                $"{manifest.Notes}".Trim();

            if (manifest.Mandatory)
            {
                ShowWarning($"{message}{Environment.NewLine}{Environment.NewLine}Bu guncelleme zorunlu. Uygulama simdi guncellenecek.", "Zorunlu guncelleme");
                await DownloadAndStartUpdaterAsync(manifest);
                return false;
            }

            if (allowOptionalPrompt && ConfirmAction($"{message}{Environment.NewLine}{Environment.NewLine}Simdi guncellemek ister misiniz?", "Guncelleme var"))
            {
                await DownloadAndStartUpdaterAsync(manifest);
                return false;
            }

            if (allowOptionalPrompt)
            {
                StatusBarText.Text = "Guncelleme ertelendi.";
            }
            else
            {
                StatusBarText.Text = $"Guncelleme var: {manifest.Version}";
            }

            return true;
        }
        catch (Exception ex)
        {
            StatusBarText.Text = "Guncelleme kontrol edilemedi.";
            if (showNoUpdateStatus)
            {
                ShowWarning($"Guncelleme kontrol edilemedi:{Environment.NewLine}{ex.Message}", "Guncelleme uyarisi");
            }

            return true;
        }
        finally
        {
            _isUpdateCheckRunning = false;
        }
    }

    private async Task DownloadAndStartUpdaterAsync(UpdateManifest manifest)
    {
        var updatesDirectory = Path.Combine(Path.GetTempPath(), "ModernYedekUpdates");
        var progress = new Progress<double>(value =>
        {
            StatusBarText.Text = $"Guncelleme indiriliyor: {value:P0}";
        });

        var client = new UpdateClient();
        var zipPath = await client.DownloadAndVerifyAsync(manifest, updatesDirectory, progress);
        var updaterExe = PrepareUpdaterExecutable();
        var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var appExe = Environment.ProcessPath ?? Path.Combine(appDirectory, "ModernYedek.App.exe");
        var processId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

        var startInfo = new ProcessStartInfo
        {
            FileName = updaterExe,
            WorkingDirectory = Path.GetDirectoryName(updaterExe),
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(processId);
        startInfo.ArgumentList.Add("--zip");
        startInfo.ArgumentList.Add(zipPath);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(appDirectory);
        startInfo.ArgumentList.Add("--exe");
        startInfo.ArgumentList.Add(appExe);

        Process.Start(startInfo);
        ShowInfo("Guncelleyici baslatildi. Uygulama kapanacak ve guncellemeden sonra yeniden acilacak.", "Guncelleme");
        _forceClose = true;
        System.Windows.Application.Current.Shutdown();
    }

    private static string PrepareUpdaterExecutable()
    {
        var sourceDirectory = AppContext.BaseDirectory;
        var sourceExe = Path.Combine(sourceDirectory, "ModernYedek.Updater.exe");
        if (!File.Exists(sourceExe))
        {
            throw new FileNotFoundException("ModernYedek.Updater.exe publish klasorunde bulunamadi.", sourceExe);
        }

        var targetDirectory = Path.Combine(Path.GetTempPath(), "ModernYedekUpdater", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(targetDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "ModernYedek.Updater*"))
        {
            File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), overwrite: true);
        }

        return Path.Combine(targetDirectory, "ModernYedek.Updater.exe");
    }

    private void NormalizeUpdateSettings()
    {
        if (string.IsNullOrWhiteSpace(_settings.Update.ManifestUrl))
        {
            _settings.Update.ManifestUrl = UpdateSettings.DefaultManifestUrl;
        }

        if (string.Equals(_settings.Update.ManifestUrl, UpdateSettings.LegacyCdnMainManifestUrl, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_settings.Update.ManifestUrl, UpdateSettings.LegacyCdnHeadManifestUrl, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Update.ManifestUrl = UpdateSettings.DefaultManifestUrl;
        }
    }

    private void NormalizeAppBehaviorSettings()
    {
        _settings.AppBehavior ??= new AppBehaviorSettings();
    }

    private static Version GetCurrentVersion()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        return version is null
            ? new Version(1, 0, 0, 0)
            : new Version(
                Math.Max(0, version.Major),
                Math.Max(0, version.Minor),
                Math.Max(0, version.Build),
                Math.Max(0, version.Revision));
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
