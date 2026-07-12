using System.Threading;
using System.Windows;

namespace ModernYedek.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\MYedek.SingleInstance";
    private const string BringToFrontEventName = @"Local\MYedek.BringToFront";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _bringToFrontEvent;
    private RegisteredWaitHandle? _bringToFrontRegistration;
    private bool _ownsSingleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        _ownsSingleInstance = createdNew;
        if (!createdNew)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        _bringToFrontEvent = new EventWaitHandle(false, EventResetMode.AutoReset, BringToFrontEventName);
        _bringToFrontRegistration = ThreadPool.RegisterWaitForSingleObject(
            _bringToFrontEvent,
            (_, _) => Dispatcher.BeginInvoke((Action)(() =>
            {
                if (MainWindow is MainWindow mainWindow)
                {
                    mainWindow.ShowFromExternalActivation();
                }
            })),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bringToFrontRegistration?.Unregister(null);
        _bringToFrontEvent?.Dispose();
        if (_ownsSingleInstance)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var existingEvent = EventWaitHandle.OpenExisting(BringToFrontEventName);
            existingEvent.Set();
        }
        catch
        {
            // Existing instance may be starting; second instance still exits.
        }
    }
}
