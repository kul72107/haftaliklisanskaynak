using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace ModernYedek.App;

public enum BackupWarningChoice
{
    StartNow,
    Snooze,
    Cancel
}

public partial class BackupWarningWindow : Window
{
    private readonly DateTimeOffset _dueAt;
    private readonly DispatcherTimer _timer;

    public BackupWarningWindow(DateTimeOffset dueAt, int snoozeMinutes)
    {
        InitializeComponent();
        _dueAt = dueAt;
        SnoozeButton.Content = $"{Math.Max(1, snoozeMinutes)} dk ertele";
        DueText.Text = $"Planlanan zaman: {_dueAt:yyyy-MM-dd HH:mm}";
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        UpdateCountdown();
    }

    public BackupWarningChoice Choice { get; private set; } = BackupWarningChoice.Cancel;

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (DateTimeOffset.Now >= _dueAt)
        {
            Choice = BackupWarningChoice.StartNow;
            DialogResult = true;
            Close();
            return;
        }

        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        var remaining = _dueAt - DateTimeOffset.Now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        CountdownText.Text = $"Yedekleme {Math.Ceiling(remaining.TotalMinutes).ToString("0", CultureInfo.InvariantCulture)} dakika icinde baslayacak.";
    }

    private void StartNow_Click(object sender, RoutedEventArgs e)
    {
        Choice = BackupWarningChoice.StartNow;
        DialogResult = true;
        Close();
    }

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        Choice = BackupWarningChoice.Snooze;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = BackupWarningChoice.Cancel;
        DialogResult = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
