using System.Threading;
using System.Windows;
using WinSentinel.Helpers;
using WinSentinel.Services;
using WinSentinel.ViewModels;
using WinSentinel.Views;

namespace WinSentinel;

/// <summary>
/// Application bootstrapper. WinSentinel is a tray-resident app: there is no main window in
/// the classic sense. On startup it spins up the monitor, the tray icon, and the floating
/// balloon; the dashboard is created on demand. ShutdownMode is OnExplicitShutdown so closing
/// windows never quits the app — only the tray's Exit does.
/// </summary>
public partial class App : Application
{
    private Mutex? _instanceMutex;
    private SystemMonitorService? _monitor;
    private TrayIconManager? _tray;
    private BalloonWindow? _balloon;
    private DashboardWindow? _dashboard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, "WinSentinel_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("WinSentinel is already running — check the system tray.",
                "WinSentinel", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _monitor = new SystemMonitorService(intervalMilliseconds: 1000);
        _monitor.Start();

        _tray = new TrayIconManager(_monitor);
        _tray.OpenDashboardRequested += ShowDashboard;
        _tray.ToggleBalloonRequested += ToggleBalloon;
        _tray.TrimMemoryRequested += TrimMemoryFromTray;
        _tray.ExitRequested += () => Shutdown();

        _balloon = new BalloonWindow { DataContext = new BalloonViewModel(_monitor) };
        _balloon.OpenDashboardRequested += ShowDashboard;
        _balloon.Show();
    }

    private void ShowDashboard()
    {
        if (_monitor is null) return;

        if (_dashboard is null)
        {
            _dashboard = new DashboardWindow { DataContext = new DashboardViewModel(_monitor) };
            _dashboard.Closed += (_, _) => _dashboard = null;
            _dashboard.Show();
        }
        else
        {
            if (_dashboard.WindowState == WindowState.Minimized)
                _dashboard.WindowState = WindowState.Normal;
            _dashboard.Activate();
        }
    }

    private void ToggleBalloon()
    {
        if (_balloon is null) return;
        _balloon.Visibility = _balloon.Visibility == Visibility.Visible
            ? Visibility.Hidden
            : Visibility.Visible;
    }

    private void TrimMemoryFromTray()
    {
        var result = new MemoryOptimizer().TrimAll();
        _tray?.ShowBalloon("WinSentinel",
            $"Trimmed {result.Trimmed} processes • ~{result.FreedMB:0} MB reclaimed.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _monitor?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
