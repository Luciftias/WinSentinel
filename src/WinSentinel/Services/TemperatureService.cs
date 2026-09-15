using System.Management;
using WinSentinel.Models;

namespace WinSentinel.Services;

/// <summary>
/// System temperature from ACPI thermal zones — two sources, best first:
///  1. <c>Win32_PerfFormattedData_Counters_ThermalZoneInformation</c> (root\cimv2, usually no admin),
///  2. <c>MSAcpi_ThermalZoneTemperature</c> (root\wmi, needs admin).
/// Values are reported by firmware; on machines that expose no zones the service simply reports
/// nothing (the UI hides the card). Polling is slow (10 s) because WMI queries are not cheap.
/// </summary>
public sealed class TemperatureService : IDisposable
{
    public sealed record Reading(double Celsius, string Zone, DateTime Time);

    private readonly System.Timers.Timer _timer;
    private readonly object _gate = new();
    private bool _querying;
    private int _consecutiveMisses;

    public TemperatureService(TimeSpan? interval = null)
    {
        _timer = new System.Timers.Timer((interval ?? TimeSpan.FromSeconds(10)).TotalMilliseconds) { AutoReset = true };
        _timer.Elapsed += (_, _) => _ = QueryAsync();
    }

    /// <summary>Latest successful reading, or null when the firmware exposes no thermal zones.</summary>
    public Reading? Latest { get; private set; }

    /// <summary>True once a reading was obtained.</summary>
    public bool Available => Latest is not null;

    /// <summary>Raised after every successful reading (on a background thread).</summary>
    public event EventHandler<Reading>? Updated;

    public void Start()
    {
        _ = QueryAsync();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private async Task QueryAsync()
    {
        lock (_gate)
        {
            if (_querying) return;
            _querying = true;
        }

        try
        {
            var reading = await Task.Run(Query);
            if (reading is null)
            {
                // No zones on this machine — stop polling entirely after a few tries.
                if (++_consecutiveMisses >= 3 && Latest is null)
                {
                    _timer.Stop();
                    Logger.Info("Temperature: no ACPI thermal zones exposed; monitoring disabled.");
                }
                return;
            }

            _consecutiveMisses = 0;
            Latest = reading;
            Updated?.Invoke(this, reading);
        }
        catch (Exception ex)
        {
            Logger.Error("Temperature query", ex);
        }
        finally
        {
            lock (_gate) { _querying = false; }
        }
    }

    private static Reading? Query()
    {
        var candidates = new List<(double Celsius, string Zone)>(4);

        TryQueryThermalZonePerf(candidates);
        if (candidates.Count == 0) TryQueryAcpiWmi(candidates);

        var best = candidates
            .Where(c => c.Celsius is > 5 and < 130)
            .OrderByDescending(c => c.Celsius)
            .FirstOrDefault();

        return best.Celsius > 0
            ? new Reading(Math.Round(best.Celsius, 1), best.Zone, DateTime.Now)
            : null;
    }

    private static void TryQueryThermalZonePerf(List<(double, string)> candidates)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2",
                "SELECT Name, Temperature, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");

            foreach (ManagementBaseObject zone in searcher.Get())
            {
                using (zone)
                {
                    string name = zone["Name"] as string ?? "Thermal zone";
                    double? celsius = ConvertKelvin(zone["HighPrecisionTemperature"]) ?? ConvertKelvin(zone["Temperature"]);
                    if (celsius is not null) candidates.Add((celsius.Value, name));
                }
            }
        }
        catch (ManagementException)
        {
            // Expected on machines whose firmware exposes no thermal zone perf class — not an error.
        }
        catch (Exception ex)
        {
            Logger.Info("ThermalZone perf query: " + ex.Message);
        }
    }

    private static void TryQueryAcpiWmi(List<(double, string)> candidates)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            foreach (ManagementBaseObject zone in searcher.Get())
            {
                using (zone)
                {
                    string name = zone["InstanceName"] as string ?? "ACPI zone";
                    double? celsius = ConvertKelvin(zone["CurrentTemperature"]);
                    if (celsius is not null) candidates.Add((celsius.Value, ShortZoneName(name)));
                }
            }
        }
        catch (ManagementException)
        {
            // "Not supported" on many VMs/mainboards — the fallback simply yields nothing.
        }
        catch (Exception ex)
        {
            Logger.Info("ACPI thermal query: " + ex.Message);
        }
    }

    /// <summary>Accepts Kelvin values that are plain (320 K) or in tenths (3200).</summary>
    private static double? ConvertKelvin(object? raw)
    {
        if (raw is null) return null;
        try
        {
            double value = Convert.ToDouble(raw);
            if (value <= 0) return null;
            double celsius = value > 1000 ? value / 10.0 - 273.15 : value - 273.15;
            return celsius is > 5 and < 130 ? celsius : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ShortZoneName(string instanceName)
    {
        int idx = instanceName.LastIndexOf('\\');
        return idx >= 0 && idx < instanceName.Length - 1 ? instanceName[(idx + 1)..] : instanceName;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        Updated = null;
    }
}
