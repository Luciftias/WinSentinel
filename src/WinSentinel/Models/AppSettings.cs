namespace WinSentinel.Models;

/// <summary>
/// User settings, persisted as JSON in %AppData%\WinSentinel\settings.json.
/// Every property has a sensible default so a missing or corrupt file simply yields
/// "factory" behaviour. Defaults aim for a calm, low-overhead experience.
/// </summary>
public sealed class AppSettings
{
    // ── Appearance ────────────────────────────────────────────────────────────
    /// <summary>Dark | Light | System</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Cyan | Blue | Violet | Green | Orange | Rose | System</summary>
    public string Accent { get; set; } = "Cyan";

    /// <summary>Use the Windows 11 Mica backdrop when available (translucent window).</summary>
    public bool TranslucentBackdrop { get; set; } = true;

    /// <summary>UI motion (page transitions, gauge easing) — also requires the Windows animation setting.</summary>
    public bool AnimationsEnabled { get; set; } = true;

    // ── Monitoring ────────────────────────────────────────────────────────────
    /// <summary>System sample interval in ms (500 | 1000 | 2000).</summary>
    public int SampleIntervalMs { get; set; } = 1000;

    /// <summary>Process list auto-refresh interval in ms (1000..10000).</summary>
    public int ProcessRefreshMs { get; set; } = 2000;

    public bool AutoRefreshProcesses { get; set; } = true;

    /// <summary>Seconds of history shown in the sparklines.</summary>
    public int HistorySeconds { get; set; } = 60;

    // ── Behaviour / safety ────────────────────────────────────────────────────
    public bool ConfirmKill { get; set; } = true;
    public bool ConfirmStartup { get; set; } = true;

    // ── Floating balloon ──────────────────────────────────────────────────────
    public bool BalloonVisible { get; set; } = true;

    /// <summary>0.4 .. 1.0 window opacity.</summary>
    public double BalloonOpacity { get; set; } = 0.92;

    public bool BalloonShowGpu { get; set; } = true;
    public bool BalloonShowDisk { get; set; } = false;
    public bool BalloonShowNet { get; set; } = true;

    public double BalloonX { get; set; } = 48;
    public double BalloonY { get; set; } = 96;

    // ── Alerts ────────────────────────────────────────────────────────────────
    public bool AlertsEnabled { get; set; } = true;

    public int AlertCpuPercent { get; set; } = 90;

    public int AlertRamPercent { get; set; } = 90;

    /// <summary>Temperature threshold in °C (ignored when the machine exposes no thermal zones).</summary>
    public int AlertTempCelsius { get; set; } = 85;

    /// <summary>How long the threshold must hold before an alert fires.</summary>
    public int AlertSustainSeconds { get; set; } = 10;

    /// <summary>Minimum gap between repeated alerts of the same kind.</summary>
    public int AlertCooldownMinutes { get; set; } = 5;

    /// <summary>Statistical spike detection (z-score vs the rolling baseline) for CPU/disk/network.</summary>
    public bool SpikeAlertsEnabled { get; set; } = true;

    /// <summary>Standard deviations above the rolling mean that count as a spike.</summary>
    public double SpikeSensitivity { get; set; } = 3.0;

    /// <summary>Warn when one process stays above the CPU threshold (dashboard open).</summary>
    public bool RunawayAlertsEnabled { get; set; } = true;

    public int RunawayCpuPercent { get; set; } = 80;

    public int RunawaySustainSeconds { get; set; } = 15;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
