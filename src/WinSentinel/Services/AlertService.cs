using WinSentinel.Models;

namespace WinSentinel.Services;

/// <summary>
/// Watches the metric stream for sustained resource pressure — the "ProcWatch" idea from
/// Process Lasso, kept deliberately simple and honest:
///  • CPU pressure: system CPU stays above a threshold for N consecutive seconds.
///  • Memory pressure: physical memory load stays above a threshold for N consecutive seconds.
/// Alerts are rate-limited per kind (cooldown) and fire at most once per breach window until
/// the metric recovers. The alert only informs — it never kills or re-prioritises anything.
/// </summary>
public sealed class AlertService
{
    public sealed record Alert(string Title, string Message);

    private static readonly TimeSpan MinCooldown = TimeSpan.FromMinutes(1);

    private AppSettings _settings;
    private int _cpuOverSeconds;
    private int _ramOverSeconds;
    private DateTime _lastCpuAlert = DateTime.MinValue;
    private DateTime _lastRamAlert = DateTime.MinValue;
    private double _peakCpu;
    private double _peakRam;

    public AlertService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Raised on the monitor thread when an alert should be shown.</summary>
    public event EventHandler<Alert>? AlertRaised;

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    public void Reset()
    {
        _cpuOverSeconds = _ramOverSeconds = 0;
        _peakCpu = _peakRam = 0;
        _lastCpuAlert = _lastRamAlert = DateTime.MinValue;
    }

    public void OnSample(MetricSample sample)
    {
        if (!_settings.AlertsEnabled) return;

        int sustain = Math.Max(1, _settings.AlertSustainSeconds);
        var now = DateTime.UtcNow;
        var cooldown = TimeSpan.FromMinutes(Math.Max(1, _settings.AlertCooldownMinutes));

        // CPU pressure -------------------------------------------------------
        if (sample.CpuPercent >= _settings.AlertCpuPercent)
        {
            _cpuOverSeconds++;
            _peakCpu = Math.Max(_peakCpu, sample.CpuPercent);
            if (_cpuOverSeconds >= sustain && now - _lastCpuAlert >= Max(cooldown, MinCooldown))
            {
                _lastCpuAlert = now;
                _cpuOverSeconds = 0;
                Raise("High CPU usage", $"CPU has been above {_settings.AlertCpuPercent}% for about {sustain}s (peak {_peakCpu:0}%). Open the dashboard to find the process.");
                _peakCpu = 0;
            }
        }
        else
        {
            _cpuOverSeconds = 0;
            _peakCpu = 0;
        }

        // RAM pressure -------------------------------------------------------
        if (sample.RamPercent >= _settings.AlertRamPercent)
        {
            _ramOverSeconds++;
            _peakRam = Math.Max(_peakRam, sample.RamPercent);
            if (_ramOverSeconds >= sustain && now - _lastRamAlert >= Max(cooldown, MinCooldown))
            {
                _lastRamAlert = now;
                _ramOverSeconds = 0;
                Raise("High memory usage", $"Memory has been above {_settings.AlertRamPercent}% for about {sustain}s (peak {_peakRam:0}%). A working-set trim may help after heavy workloads.");
                _peakRam = 0;
            }
        }
        else
        {
            _ramOverSeconds = 0;
            _peakRam = 0;
        }
    }

    /// <summary>Fires a synthetic alert (used by the Settings page "Test alert" button).</summary>
    public void RaiseTest() => Raise("WinSentinel alerts are working", "This is what an alert looks like. Alerts stay rate-limited and never change system state.");

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private void Raise(string title, string message)
    {
        try { AlertRaised?.Invoke(this, new Alert(title, message)); }
        catch (Exception ex) { Logger.Error("Alert raise", ex); }
    }
}
