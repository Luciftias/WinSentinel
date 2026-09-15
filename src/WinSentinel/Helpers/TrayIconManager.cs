using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Windows.Threading;
using WinSentinel.Models;
using WinSentinel.Services;

namespace WinSentinel.Helpers;

/// <summary>
/// Owns the notification-area (system tray) icon — the only place System.Windows.Forms is used.
/// The hover tooltip shows live CPU/RAM/GPU values; the context menu exposes the most common
/// actions (dashboard, balloon toggle, trim, alerts toggle, settings, exit). All updates are
/// marshalled onto the UI thread because NotifyIcon is not thread-safe.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly SystemMonitorService _monitor;
    private readonly Dispatcher _dispatcher;
    private readonly ToolStripMenuItem _balloonItem;
    private readonly ToolStripMenuItem _alertsItem;

    public event Action? OpenDashboardRequested;
    public event Action? ToggleBalloonRequested;
    public event Action? TrimMemoryRequested;
    public event Action<bool>? AlertsToggled;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayIconManager(SystemMonitorService monitor)
    {
        _monitor = monitor;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _icon = new NotifyIcon
        {
            Text = "WinSentinel",
            Visible = true,
            Icon = LoadTrayIcon()
        };

        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("Open Dashboard", null, (_, _) => OpenDashboardRequested?.Invoke());
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);

        _balloonItem = new ToolStripMenuItem("Show Floating Balloon", null, (_, _) => ToggleBalloonRequested?.Invoke())
        {
            CheckOnClick = false
        };

        menu.Items.Add(openItem);
        menu.Items.Add(_balloonItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Trim Memory Now", null, (_, _) => TrimMemoryRequested?.Invoke());
        menu.Items.Add("Purge Standby List", null, (_, _) => PurgeStandbyRequested?.Invoke());
        _alertsItem = new ToolStripMenuItem("Resource Alerts", null, OnAlertsClicked)
        {
            CheckOnClick = true
        };
        menu.Items.Add(_alertsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;

        _icon.DoubleClick += (_, _) => OpenDashboardRequested?.Invoke();
        _monitor.SampleUpdated += OnSample;
    }

    /// <summary>Raised when the user asks to purge the standby list from the tray.</summary>
    public event Action? PurgeStandbyRequested;

    /// <summary>Reflects external settings changes into the menu check state.</summary>
    public bool BalloonVisible
    {
        get => _balloonItem.Checked;
        set => _balloonItem.Checked = value;
    }

    /// <summary>Reflects external settings changes into the menu check state.</summary>
    public bool AlertsEnabled
    {
        get => _alertsItem.Checked;
        set => _alertsItem.Checked = value;
    }

    private void OnAlertsClicked(object? sender, EventArgs e)
        => AlertsToggled?.Invoke(_alertsItem.Checked);

    private static Icon LoadTrayIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
            if (File.Exists(path)) return new Icon(path);
        }
        catch { /* fall through */ }
        return SystemIcons.Application;
    }

    private void OnSample(object? sender, MetricSample sample)
    {
        if (!_dispatcher.CheckAccess())
        {
            // Coalesce: only keep the latest text queued.
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () => UpdateTooltip(sample));
            return;
        }
        UpdateTooltip(sample);
    }

    private void UpdateTooltip(MetricSample sample)
    {
        var sb = new StringBuilder(127);
        sb.Append("CPU ").Append(sample.CpuPercent.ToString("0")).Append("%  ");
        sb.Append("RAM ").Append(sample.RamPercent.ToString("0")).Append('%');
        if (sample.GpuAvailable) sb.Append("  GPU ").Append(sample.GpuPercent.ToString("0")).Append('%');

        string text = sb.ToString();
        if (text.Length > 120) text = text[..120];
        try { _icon.Text = text; } catch { /* races during shell shutdown */ }
    }

    /// <summary>Shows a balloon notification. Safe to call from any thread.</summary>
    public void ShowBalloon(string title, string message)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => ShowBalloon(title, message));
            return;
        }
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(4000);
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        _monitor.SampleUpdated -= OnSample;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
