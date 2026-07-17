using System.Windows;
using System.Windows.Threading;

namespace ModernYedek.App;

public partial class AutoCloseMessageWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _remainingSeconds;

    public AutoCloseMessageWindow(string title, string message, int seconds)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        _remainingSeconds = Math.Max(1, seconds);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        UpdateCountdown();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingSeconds--;
        if (_remainingSeconds <= 0)
        {
            Close();
            return;
        }

        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        CountdownText.Text = $"{_remainingSeconds} saniye sonra otomatik kapanacak.";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
