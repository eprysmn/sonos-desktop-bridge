using System.Windows;

namespace SonosDesktopBridge;

public partial class App : Application
{
    private const string MutexName = @"Local\SonosDesktopBridge.SingleInstance";
    private const string ShowEventName = @"Local\SonosDesktopBridge.Show";
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        var quiet = e.Args.Contains("--quiet");
        if (e.Args.Contains("--install"))
        {
            Installer.Install();
            if (!quiet) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true });
            Shutdown();
            return;
        }
        if (e.Args.Contains("--uninstall"))
        {
            var answer = quiet ? MessageBoxResult.Yes
                : MessageBox.Show("Désinstaller Sonos Desktop Bridge ?", "Sonos Desktop Bridge", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) Installer.Uninstall();
            Shutdown();
            return;
        }

        // Single instance. The wait lets a freshly installed copy take over from the one that launched it.
        _mutex = new Mutex(false, MutexName);
        var owned = false;
        try { owned = _mutex.WaitOne(3000); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            // Already running (probably in the notification area): ask it to show its window.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray());
        }) { IsBackground = true }.Start();

        base.OnStartup(e);
    }
}
