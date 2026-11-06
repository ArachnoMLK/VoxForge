using System.Windows;

namespace VoxForge;

/// <summary>Single-instance app: launching VoxForge again brings the running one back from the notification area.</summary>
public partial class App : Application
{
    private const string MutexName = "VoxForge.SingleInstance";
    private const string ShowEventName = "VoxForge.ShowWindow";

    private Mutex? instanceMutex;
    private EventWaitHandle? showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(true, MutexName, out bool firstInstance);
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!firstInstance)
        {
            showSignal.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        var signal = showSignal;
        new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(() => window.ShowFromTray());
        }) { IsBackground = true, Name = "VoxForge show signal" }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
