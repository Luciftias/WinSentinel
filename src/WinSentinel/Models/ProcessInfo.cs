namespace WinSentinel.Models;

/// <summary>
/// A lightweight, UI-friendly snapshot of a running process, captured by
/// <see cref="WinSentinel.Services.ProcessService"/> so the UI never holds live
/// <see cref="System.Diagnostics.Process"/> handles. Rate values (CPU %, disk B/s) are null
/// on the first observation of a process, before a delta can be computed.
/// </summary>
public sealed class ProcessInfo
{
    public int Pid { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>Full image path, when readable.</summary>
    public string? Path { get; init; }

    /// <summary>Version-info company name (cached per path), when readable.</summary>
    public string? Company { get; init; }

    /// <summary>Working set (physical RAM held by the process) in megabytes.</summary>
    public double WorkingSetMB { get; init; }

    /// <summary>CPU utilisation normalised to total system capacity (0..100), or null before first delta.</summary>
    public double? CpuPercent { get; init; }

    /// <summary>Disk read rate in bytes/second (aggregate I/O counter delta), or null.</summary>
    public double? DiskReadBps { get; init; }

    /// <summary>Disk write rate in bytes/second, or null.</summary>
    public double? DiskWriteBps { get; init; }

    /// <summary>GPU utilisation (busiest engine) 0..100, or null when unavailable.</summary>
    public double? GpuPercent { get; init; }

    public int Threads { get; init; }

    /// <summary>Friendly priority class name (e.g. "Normal", "High").</summary>
    public string Priority { get; init; } = "—";

    /// <summary>Windows Efficiency Mode (EcoQoS) state, or null when unreadable.</summary>
    public bool? EcoMode { get; init; }

    /// <summary>True for OS-critical processes that must never be killed or re-prioritised.</summary>
    public bool IsProtected { get; init; }
}
