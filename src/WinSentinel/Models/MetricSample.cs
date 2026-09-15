namespace WinSentinel.Models;

/// <summary>
/// One immutable snapshot of system-wide CPU and physical memory usage.
/// Produced by <see cref="WinSentinel.Services.SystemMonitorService"/> on every tick.
/// </summary>
public sealed class MetricSample
{
    /// <summary>Total CPU utilisation across all logical processors, 0..100.</summary>
    public double CpuPercent { get; init; }

    /// <summary>Physical memory load, 0..100 (matches Task Manager's "In use" percentage).</summary>
    public double RamPercent { get; init; }

    /// <summary>Physical memory currently in use, in megabytes.</summary>
    public double RamUsedMB { get; init; }

    /// <summary>Total installed physical memory, in megabytes.</summary>
    public double RamTotalMB { get; init; }

    /// <summary>Physical memory currently available, in megabytes.</summary>
    public double RamAvailableMB { get; init; }

    /// <summary>Local wall-clock time the sample was captured.</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;
}
