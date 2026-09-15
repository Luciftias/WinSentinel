using System.IO;
using System.Text.Json;
using WinSentinel.Models;

namespace WinSentinel.Services;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> as JSON under %AppData%\WinSentinel. Writes are
/// debounced on the UI thread so slider drags do not hammer the disk, and a corrupt or partial
/// file never breaks startup — it falls back to defaults.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly System.Timers.Timer _saveTimer;
    private AppSettings _current;

    public string SettingsPath { get; } = Path.Combine(Logger.LogDirectory, "settings.json");

    /// <summary>Raised after settings were loaded or changed (on the thread that changed them).</summary>
    public event EventHandler<AppSettings>? Changed;

    public SettingsService()
    {
        _current = Load();
        _saveTimer = new System.Timers.Timer(600) { AutoReset = false };
        _saveTimer.Elapsed += (_, _) => SaveNow();
    }

    public AppSettings Current
    {
        get { lock (_gate) { return _current; } }
    }

    /// <summary>Mutates the settings object and schedules a debounced save.</summary>
    public void Update(Action<AppSettings> mutate)
    {
        AppSettings snapshot;
        lock (_gate)
        {
            mutate(_current);
            snapshot = _current;
        }
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke(this, snapshot);
    }

    /// <summary>Forces an immediate synchronous save (used on shutdown).</summary>
    public void SaveNow()
    {
        AppSettings snapshot;
        lock (_gate) { snapshot = _current; }
        try
        {
            Directory.CreateDirectory(Logger.LogDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Error("Settings save", ex);
        }
    }

    public void ResetToDefaults() => Update(s =>
    {
        var defaults = new AppSettings();
        foreach (var prop in typeof(AppSettings).GetProperties())
        {
            if (prop.CanWrite) prop.SetValue(s, prop.GetValue(defaults));
        }
    });

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null) return Sanitize(loaded);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Settings load", ex);
        }
        return new AppSettings();
    }

    private static AppSettings Sanitize(AppSettings s)
    {
        s.SampleIntervalMs = Math.Clamp(s.SampleIntervalMs, 500, 5000);
        s.ProcessRefreshMs = Math.Clamp(s.ProcessRefreshMs, 1000, 30000);
        s.HistorySeconds = Math.Clamp(s.HistorySeconds, 30, 300);
        s.BalloonOpacity = Math.Clamp(s.BalloonOpacity, 0.3, 1.0);
        s.AlertCpuPercent = Math.Clamp(s.AlertCpuPercent, 50, 100);
        s.AlertRamPercent = Math.Clamp(s.AlertRamPercent, 50, 100);
        s.AlertTempCelsius = Math.Clamp(s.AlertTempCelsius, 50, 110);
        s.AlertSustainSeconds = Math.Clamp(s.AlertSustainSeconds, 3, 120);
        s.AlertCooldownMinutes = Math.Clamp(s.AlertCooldownMinutes, 1, 120);
        s.SpikeSensitivity = Math.Clamp(s.SpikeSensitivity, 1.5, 6.0);
        s.RunawayCpuPercent = Math.Clamp(s.RunawayCpuPercent, 30, 100);
        s.RunawaySustainSeconds = Math.Clamp(s.RunawaySustainSeconds, 5, 300);
        if (s.Theme is not ("Dark" or "Light" or "System")) s.Theme = "Dark";
        if (s.Accent is not ("Cyan" or "Blue" or "Violet" or "Green" or "Orange" or "Rose" or "System")) s.Accent = "Cyan";
        return s;
    }
}
