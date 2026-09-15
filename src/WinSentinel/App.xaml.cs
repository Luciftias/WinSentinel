using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinSentinel.Abstractions;
using WinSentinel.Helpers;
using WinSentinel.Models;
using WinSentinel.Services;
using WinSentinel.ViewModels;
using WinSentinel.Views;

namespace WinSentinel;

/// <summary>
/// Application bootstrapper. WinSentinel is a tray-resident app: there is no main window in
/// the classic sense. On startup it loads settings, applies the theme, spins up the monitor,
/// the alert watcher, the tray icon and the floating balloon; the dashboard is created on
/// demand. ShutdownMode is OnExplicitShutdown so closing windows never quits the app — only
/// the tray's Exit does.
/// </summary>
public partial class App : Application
{
    private Mutex? _instanceMutex;
    private SettingsService? _settings;
    private ThemeManager? _theme;
    private SystemMonitorService? _monitor;
    private ProcessService? _processes;
    private NetworkService? _network;
    private TemperatureService? _temperature;
    private MemoryOptimizer? _memory;
    private StartupManager? _startup;
    private AlertService? _alerts;
    private PluginHost? _plugins;
    private TrayIconManager? _tray;
    private BalloonWindow? _balloon;
    private DashboardWindow? _dashboard;

    private string _appliedTheme = string.Empty;
    private string _appliedAccent = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        HookCrashHandlers();

        _instanceMutex = new Mutex(initiallyOwned: true, "WinSentinel_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("WinSentinel is already running — check the system tray.",
                "WinSentinel", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Settings + theme first so every window is created already themed.
        _settings = new SettingsService();
        _theme = new ThemeManager(_settings);
        _theme.Apply();
        _appliedTheme = _settings.Current.Theme;
        _appliedAccent = _settings.Current.Accent;

        // Core services
        _network = new NetworkService();
        _temperature = new TemperatureService();
        _monitor = new SystemMonitorService(_settings.Current.SampleIntervalMs, _network, _temperature);
        _processes = new ProcessService(_network);
        _memory = new MemoryOptimizer();
        _startup = new StartupManager();
        _alerts = new AlertService(_settings.Current);
        _monitor.SampleUpdated += (_, sample) => _alerts.OnSample(sample);
        _temperature.Start();

        // Plugins (loaded from %AppData%\WinSentinel\plugins; failures never take the app down)
        _plugins = new PluginHost();
        _plugins.LoadAll();
        _plugins.Start();

        // Tray
        _tray = new TrayIconManager(_monitor)
        {
            BalloonVisible = _settings.Current.BalloonVisible,
            AlertsEnabled = _settings.Current.AlertsEnabled
        };
        _tray.OpenDashboardRequested += () => ShowDashboard();
        _tray.ToggleBalloonRequested += ToggleBalloon;
        _tray.TrimMemoryRequested += TrimMemoryFromTray;
        _tray.PurgeStandbyRequested += PurgeStandbyFromTray;
        _tray.SettingsRequested += () => ShowDashboard(navigateToSettings: true);
        _tray.AlertsToggled += enabled => _settings.Update(s => s.AlertsEnabled = enabled);
        _tray.ExitRequested += () => Shutdown();
        _alerts.AlertRaised += (_, alert) => _tray.ShowBalloon(alert.Title, alert.Message);
        _tray.SetPluginCommands(BuildTrayPluginCommands());
        _plugins.PluginsChanged += (_, _) => _tray?.SetPluginCommands(BuildTrayPluginCommands());

        // Floating balloon
        _balloon = new BalloonWindow { DataContext = new BalloonViewModel(_monitor, _settings) };
        _balloon.OpenDashboardRequested += () => ShowDashboard();
        _balloon.SettingsRequested += () => ShowDashboard(navigateToSettings: true);
        _balloon.TrimRequested += TrimMemoryFromTray;
        _balloon.HideRequested += () => _settings.Update(s => s.BalloonVisible = false);
        _balloon.ExitRequested += () => Shutdown();
        ApplyBalloonSettings();

        _settings.Changed += OnSettingsChanged;
        _monitor.Start();
    }

    // ---------------------------------------------------------------- settings reactions

    private void OnSettingsChanged(object? sender, AppSettings s)
    {
        if (s.Theme != _appliedTheme || s.Accent != _appliedAccent)
        {
            _appliedTheme = s.Theme;
            _appliedAccent = s.Accent;
            _theme?.Apply();
        }

        _alerts?.UpdateSettings(s);
        _monitor?.SetInterval(s.SampleIntervalMs);
        ApplyBalloonSettings();
        if (_tray is not null)
        {
            _tray.BalloonVisible = s.BalloonVisible;
            _tray.AlertsEnabled = s.AlertsEnabled;
        }
    }

    private void ApplyBalloonSettings()
    {
        if (_balloon is null || _settings is null) return;
        var s = _settings.Current;

        _balloon.Opacity = s.BalloonOpacity;
        if (s.BalloonVisible)
        {
            if (!_balloon.IsVisible) _balloon.Show();
        }
        else if (_balloon.IsVisible)
        {
            _balloon.Hide();
        }
    }

    private void ToggleBalloon()
        => _settings?.Update(s => s.BalloonVisible = !s.BalloonVisible);

    // ---------------------------------------------------------------- plugins

    private List<(string Title, Action Execute)> BuildTrayPluginCommands()
    {
        var list = new List<(string, Action)>();
        if (_plugins is null) return list;

        foreach (var entry in _plugins.Commands)
        {
            var captured = entry;
            list.Add((captured.Display, () => ExecutePluginCommand(captured)));
        }
        return list;
    }

    private void ExecutePluginCommand(PluginHost.PluginCommandEntry entry)
    {
        if (_plugins is null) return;
        bool ok = _plugins.TryExecuteCommand(entry, PluginCommandContext.Empty, out string message);
        if (!ok) _tray?.ShowBalloon("Plugin command", message);
    }

    // ---------------------------------------------------------------- dashboard

    private void ShowDashboard(bool navigateToSettings = false)
    {
        if (_monitor is null || _settings is null || _processes is null || _memory is null ||
            _startup is null || _theme is null || _alerts is null || _network is null || _plugins is null) return;

        if (_dashboard is null)
        {
            var vm = new DashboardViewModel(_monitor, _processes, _memory, _startup, _network, _plugins, _settings, _theme, _alerts);
            var window = new DashboardWindow { DataContext = vm };
            window.Closed += (_, _) =>
            {
                vm.Dispose();
                _dashboard = null;
            };
            _dashboard = window;
            window.Show();
        }
        else
        {
            if (_dashboard.WindowState == WindowState.Minimized)
                _dashboard.WindowState = WindowState.Normal;
            _dashboard.Activate();
        }

        if (navigateToSettings && _dashboard?.DataContext is DashboardViewModel dvm)
            dvm.ShowSettings();
    }

    // ---------------------------------------------------------------- tray quick actions

    private void TrimMemoryFromTray()
    {
        if (_memory is null) return;
        _tray?.ShowBalloon("WinSentinel", "Trimming working sets…");
        Task.Run(() => _memory.TrimAll()).ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                Logger.Error("Tray memory trim", task.Exception ?? new Exception("unknown"));
                _tray?.ShowBalloon("WinSentinel", "Memory trim failed — see the log file.");
                return;
            }
            var r = task.Result;
            _tray?.ShowBalloon("WinSentinel", $"Trimmed {r.Trimmed} processes • ~{r.FreedMB:0} MB reclaimed.");
        });
    }

    private void PurgeStandbyFromTray()
    {
        if (_memory is null) return;
        Task.Run(() => _memory.PurgeStandby()).ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                Logger.Error("Tray standby purge", task.Exception ?? new Exception("unknown"));
                _tray?.ShowBalloon("WinSentinel", "Standby purge failed — see the log file.");
                return;
            }
            var r = task.Result;
            _tray?.ShowBalloon("WinSentinel", r.Message);
        });
    }

    // ---------------------------------------------------------------- lifecycle

    private void HookCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("UI thread", args.Exception);
            MessageBox.Show(
                $"WinSentinel hit an unexpected error and recovered.\n\n{args.Exception.Message}\n\nDetails: {Logger.LogPath}",
                "WinSentinel", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Logger.Error("Background thread", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Unobserved task", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _settings?.SaveNow();
        _tray?.Dispose();
        _monitor?.Dispose();
        _processes?.Dispose();
        _temperature?.Dispose();
        _network?.Dispose();
        _plugins?.Dispose();
        _theme?.Dispose();
        _balloon?.Close();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
