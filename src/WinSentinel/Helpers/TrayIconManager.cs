using System.Drawing;
using System.IO;
using System.Windows.Forms;
using WinSentinel.Models;
using WinSentinel.Services;

namespace WinSentinel.Helpers;

/// <summary>
/// Owns the notification-area (system tray) icon. The hover tooltip is refreshed every tick
/// with live CPU/RAM; the context menu and double-click raise events the App wires up. This
/// is the one place we touch System.Windows.Forms.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly SystemMonitorService _monitor;

    public event Action? OpenDashboardRequested;
    public event Action? ToggleBalloonRequested;
    public event Action? TrimMemoryRequested;
    public event Action? ExitRequested;

    public TrayIconManager(SystemMonitorService monitor)
    {
        _monitor = monitor;

        _icon = new NotifyIcon
        {
            Text = "WinSentinel",
            Visible = true,
            Icon = LoadTrayIcon()
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => OpenDashboardRequested?.Invoke());
        menu.Items.Add("Toggle Floating Balloon", null, (_, _) => ToggleBalloonRequested?.Invoke());
        menu.Items.Add("Trim Memory Now", null, (_, _) => TrimMemoryRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;

        _icon.DoubleClick += (_, _) => OpenDashboardRequested?.Invoke();
        _monitor.SampleUpdated += OnSample;
    }

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
        // NotifyIcon.Text is capped at 63 characters.
        string text = $"WinSentinel   CPU {sample.CpuPercent:0}%   RAM {sample.RamPercent:0}%";
        if (text.Length > 63) text = text[..63];
        try { _icon.Text = text; } catch { /* ignore races during shutdown */ }
    }

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(3000);
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
