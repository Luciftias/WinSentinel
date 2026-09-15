using WinSentinel.Models;

namespace WinSentinel.Services;

public enum AlertKind
{
    CpuHigh,
    RamHigh,
    TempHigh,
    CpuSpike,
    DiskSpike,
    NetSpike,
    ProcessRunaway,
    Test
}

public sealed record Alert(AlertKind Kind, string Title, string Message, DateTime Time)
{
    public string TimeDisplay => Time.ToString("HH:mm:ss");
}

/// <summary>
/// Anomaly watcher with three detector families:
///  1. <b>Sustained thresholds</b> — CPU / RAM / temperature stay above a configured value for
///     N seconds (the classic resource-hog alarm).
///  2. <b>Statistical spikes</b> — current value is more than N standard deviations above the
///     rolling window mean (≈2 minutes): catches "something unusual just happened" even when no
///     fixed threshold exists (disk activity, network traffic, CPU bursts).
///  3. <b>Runaway processes</b> — the same process stays above a CPU threshold for N seconds
///     (fed by the process table while the dashboard is open).
/// Every alert is rate-limited per kind, recorded in a bounded history, and never changes system
/// state — detection only.
/// </summary>
public sealed class AlertService
{
    private sealed class RollingWindow
    {
        private readonly Queue<double> _values;
        private readonly int _capacity;
        private double _sum;
        private double _sumSquares;

        public RollingWindow(int capacity)
        {
            _capacity = capacity;
            _values = new Queue<double>(capacity);
        }

        public int Count => _values.Count;
        public bool IsFull => _values.Count >= _capacity;

        public void Add(double value)
        {
            _values.Enqueue(value);
            _sum += value;
            _sumSquares += value * value;
            if (_values.Count > _capacity)
            {
                double old = _values.Dequeue();
                _sum -= old;
                _sumSquares -= old * old;
            }
        }

        public double Mean => _values.Count == 0 ? 0 : _sum / _values.Count;

        public double StandardDeviation
        {
            get
            {
                if (_values.Count < 2) return 0;
                double variance = Math.Max(0, _sumSquares / _values.Count - Mean * Mean);
                return Math.Sqrt(variance);
            }
        }
    }

    private readonly object _gate = new();
    private readonly List<Alert> _history = new(128);
    private readonly Dictionary<AlertKind, DateTime> _lastFired = new();
    private readonly Dictionary<int, DateTime> _runawayLastFired = new();
    private readonly RollingWindow _cpuWindow = new(120);
    private readonly RollingWindow _diskWindow = new(120);
    private readonly RollingWindow _netWindow = new(120);
    private readonly RollingWindow _tempWindow = new(120);

    private AppSettings _settings;
    private int _cpuOverSeconds;
    private int _ramOverSeconds;
    private int _tempOverSeconds;
    private int _runawayPid;
    private double _runawaySeconds;

    public AlertService(AppSettings settings) => _settings = settings;

    /// <summary>Raised (on the monitor thread) whenever an alert fires.</summary>
    public event EventHandler<Alert>? AlertRaised;

    /// <summary>Newest-first, bounded to 100 entries.</summary>
    public IReadOnlyList<Alert> History
    {
        get { lock (_gate) { return _history.ToArray(); } }
    }

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    public void ClearHistory()
    {
        lock (_gate) { _history.Clear(); }
    }

    public void Reset()
    {
        _cpuOverSeconds = _ramOverSeconds = _tempOverSeconds = 0;
        _runawayPid = 0;
        _runawaySeconds = 0;
        _lastFired.Clear();
        _runawayLastFired.Clear();
    }

    // ══════════════════════════════════════════════════════════ sampling

    /// <summary>Feeds one system sample through all detectors.</summary>
    public void OnSample(MetricSample s)
    {
        double diskMBs = (s.DiskReadBps + s.DiskWriteBps) / (1024.0 * 1024.0);
        double netMBs = (s.NetDownBps + s.NetUpBps) / (1024.0 * 1024.0);

        _cpuWindow.Add(s.CpuPercent);
        _diskWindow.Add(diskMBs);
        _netWindow.Add(netMBs);
        if (s.TempAvailable && s.TempCelsius > 0) _tempWindow.Add(s.TempCelsius);

        if (!_settings.AlertsEnabled) return;

        var now = DateTime.UtcNow;
        var cooldown = Cooldown();

        // ── sustained thresholds ─────────────────────────────────────────
        CheckThreshold(AlertKind.CpuHigh, ref _cpuOverSeconds, s.CpuPercent, _settings.AlertCpuPercent,
            "High CPU usage",
            $"CPU has been above {_settings.AlertCpuPercent}% for about {SustainSeconds()}s (now {s.CpuPercent:0}%). Open the dashboard to find the process.",
            cooldown, now);

        CheckThreshold(AlertKind.RamHigh, ref _ramOverSeconds, s.RamPercent, _settings.AlertRamPercent,
            "High memory usage",
            $"Memory has been above {_settings.AlertRamPercent}% for about {SustainSeconds()}s (now {s.RamPercent:0}%). A working-set trim may help after heavy workloads.",
            cooldown, now);

        if (s.TempAvailable && s.TempCelsius > 0)
        {
            CheckThreshold(AlertKind.TempHigh, ref _tempOverSeconds, s.TempCelsius, _settings.AlertTempCelsius,
                "High temperature",
                $"System temperature has been above {_settings.AlertTempCelsius}°C for about {SustainSeconds()}s (now {s.TempCelsius:0.0}°C • {s.TempZone}).",
                cooldown, now);
        }

        // ── statistical spikes ───────────────────────────────────────────
        if (_settings.SpikeAlertsEnabled)
        {
            CheckSpike(AlertKind.CpuSpike, _cpuWindow, s.CpuPercent, "CPU",
                $"{s.CpuPercent:0}%", cooldown, now);
            CheckSpike(AlertKind.DiskSpike, _diskWindow, diskMBs, "Disk activity",
                $"{diskMBs:0.0} MB/s", cooldown, now);
            CheckSpike(AlertKind.NetSpike, _netWindow, netMBs, "Network traffic",
                $"{netMBs:0.0} MB/s", cooldown, now);
        }
    }

    /// <summary>
    /// Fed by the process table: tracks whether the same high-CPU process persists.
    /// <paramref name="elapsedSeconds"/> is the time since the previous report.
    /// </summary>
    public void ReportTopProcess(int pid, string name, double cpuPercent, double elapsedSeconds)
    {
        if (!_settings.AlertsEnabled || !_settings.RunawayAlertsEnabled) return;
        if (cpuPercent < _settings.RunawayCpuPercent || elapsedSeconds <= 0)
        {
            _runawayPid = 0;
            _runawaySeconds = 0;
            return;
        }

        if (pid == _runawayPid) _runawaySeconds += elapsedSeconds;
        else { _runawayPid = pid; _runawaySeconds = elapsedSeconds; }

        if (_runawaySeconds < Math.Max(3, _settings.RunawaySustainSeconds)) return;

        var now = DateTime.UtcNow;
        if (_runawayLastFired.TryGetValue(pid, out var last) && now - last < Cooldown()) return;

        _runawayLastFired[pid] = now;
        if (_runawayLastFired.Count > 256) _runawayLastFired.Clear();

        Raise(AlertKind.ProcessRunaway, "Runaway process",
            $"'{name}' (PID {pid}) has used ~{cpuPercent:0}% CPU for about {_runawaySeconds:0}s. Consider Efficiency Mode or ending it from the Processes tab.");
        _runawaySeconds = 0;
    }

    /// <summary>Fires a synthetic alert (Settings → "Test alert").</summary>
    public void RaiseTest() => Raise(AlertKind.Test, "WinSentinel alerts are working",
        "This is what an alert looks like. Detection is read-only and rate-limited — it never changes system state.");

    // ══════════════════════════════════════════════════════════ detectors

    private void CheckThreshold(
        AlertKind kind, ref int overSeconds, double value, double threshold,
        string title, string message, TimeSpan cooldown, DateTime now)
    {
        if (value < threshold)
        {
            overSeconds = 0;
            return;
        }

        if (++overSeconds < Math.Max(1, _settings.AlertSustainSeconds)) return;
        overSeconds = 0;
        if (Cooling(kind, cooldown, now)) return;
        Raise(kind, title, message);
    }

    private void CheckSpike(
        AlertKind kind, RollingWindow window, double value, string label, string display,
        TimeSpan cooldown, DateTime now)
    {
        // ~30 samples of baseline are enough to judge "unusual"; near-constant signals are ignored.
        if (window.Count < 30 || window.StandardDeviation < 0.5) return;
        double z = (value - window.Mean) / window.StandardDeviation;
        if (z < Math.Max(1.5, _settings.SpikeSensitivity)) return;
        if (Cooling(kind, cooldown, now)) return;

        Raise(kind, $"Unusual {label.ToLowerInvariant()}",
            $"{label} just spiked to {display} — about {z:0.0}×σ above the recent baseline ({window.Mean:0.0} avg).");
    }

    private TimeSpan Cooldown() => TimeSpan.FromMinutes(Math.Max(1, _settings.AlertCooldownMinutes));

    private int SustainSeconds() => Math.Max(1, _settings.AlertSustainSeconds);

    private bool Cooling(AlertKind kind, TimeSpan cooldown, DateTime now)
    {
        if (_lastFired.TryGetValue(kind, out var last) && now - last < cooldown) return true;
        _lastFired[kind] = now;
        return false;
    }

    private void Raise(AlertKind kind, string title, string message)
    {
        var alert = new Alert(kind, title, message, DateTime.Now);
        lock (_gate)
        {
            _history.Insert(0, alert);
            if (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
        }

        try { AlertRaised?.Invoke(this, alert); }
        catch (Exception ex) { Logger.Error("Alert raise", ex); }
    }
}
