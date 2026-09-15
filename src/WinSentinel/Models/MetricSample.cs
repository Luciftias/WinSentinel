namespace WinSentinel.Models;

/// <summary>
/// One immutable snapshot of system-wide telemetry. Produced by
/// <see cref="WinSentinel.Services.SystemMonitorService"/> on every tick.
/// Availability flags stay false on machines without the corresponding hardware or counters
/// (e.g. no GPU counters, no battery) so the UI can hide those cards honestly.
/// </summary>
public sealed class MetricSample
{
    /// <summary>Total CPU utilisation across all logical processors, 0..100.</summary>
    public double CpuPercent { get; init; }

    /// <summary>Average current CPU clock in MHz, or 0 when the firmware does not report it.</summary>
    public double CpuMhz { get; init; }

    /// <summary>Physical memory load, 0..100 (matches Task Manager's "In use" percentage).</summary>
    public double RamPercent { get; init; }

    /// <summary>Physical memory currently in use, in megabytes.</summary>
    public double RamUsedMB { get; init; }

    /// <summary>Total installed physical memory, in megabytes.</summary>
    public double RamTotalMB { get; init; }

    /// <summary>Physical memory currently available, in megabytes.</summary>
    public double RamAvailableMB { get; init; }

    /// <summary>True once the disk rate counters produced a value.</summary>
    public bool DiskAvailable { get; init; }

    public double DiskReadBps { get; init; }
    public double DiskWriteBps { get; init; }

    public double NetDownBps { get; init; }
    public double NetUpBps { get; init; }

    /// <summary>True when the GPU Engine counters exist (Win10 1709+, WDDM 2.0+).</summary>
    public bool GpuAvailable { get; init; }

    public double GpuPercent { get; init; }

    /// <summary>True when a battery is present (laptops / tablets).</summary>
    public bool BatteryPresent { get; init; }

    /// <summary>Battery charge percentage, or -1 when unknown.</summary>
    public int BatteryPercent { get; init; } = -1;

    /// <summary>True when running on wall power.</summary>
    public bool OnAcPower { get; init; } = true;

    /// <summary>True when ACPI thermal zones are exposed by the firmware.</summary>
    public bool TempAvailable { get; init; }

    /// <summary>Hottest ACPI thermal zone in °C (0 when unavailable).</summary>
    public double TempCelsius { get; init; }

    /// <summary>Zone name reported by the firmware.</summary>
    public string TempZone { get; init; } = string.Empty;

    /// <summary>Local wall-clock time the sample was captured.</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;
}
