namespace WinSentinel.Models;

/// <summary>
/// A lightweight, UI-friendly view of a running process. Snapshotted by
/// <see cref="WinSentinel.Services.ProcessService"/> so the UI never holds live
/// <see cref="System.Diagnostics.Process"/> handles.
/// </summary>
public sealed class ProcessInfo
{
    public int Pid { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>Working set (physical RAM held by the process) in megabytes.</summary>
    public double WorkingSetMB { get; init; }

    /// <summary>Friendly priority class name (e.g. "Normal", "High").</summary>
    public string Priority { get; init; } = "—";

    public int Threads { get; init; }

    /// <summary>True for OS-critical processes that must never be killed or re-prioritised.</summary>
    public bool IsProtected { get; init; }

    public string MemoryDisplay => $"{WorkingSetMB:N1} MB";
}
