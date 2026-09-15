namespace WinSentinel.Services;

/// <summary>
/// Keeps the previous per-PID CPU time and I/O counters so snapshots can be converted into
/// rates. The first observation of a process yields null rates (no delta yet); exited PIDs are
/// pruned so the map cannot grow without bound.
///
/// CPU normalisation follows the Task Manager convention: a process burning one full core on
/// an 8-core machine reads 12.5%.
/// </summary>
internal sealed class ProcessSampler
{
    private readonly record struct Previous(TimeSpan Cpu, ulong Read, ulong Write, long TickMs);

    private readonly Dictionary<int, Previous> _previous = new();
    private readonly int _cores = Math.Max(1, Environment.ProcessorCount);

    public (double? CpuPercent, double? ReadBps, double? WriteBps) Update(
        int pid, TimeSpan cpu, ulong readBytes, ulong writeBytes, long nowMs)
    {
        double? cpuPercent = null, readBps = null, writeBps = null;

        if (_previous.TryGetValue(pid, out var prev))
        {
            double elapsedMs = nowMs - prev.TickMs;
            if (elapsedMs > 50)
            {
                double deltaCpuMs = (cpu - prev.Cpu).TotalMilliseconds;
                if (deltaCpuMs >= 0)
                    cpuPercent = Math.Clamp(deltaCpuMs / (elapsedMs * _cores) * 100.0, 0, 100);

                double seconds = elapsedMs / 1000.0;
                // Guard against counter resets / PID reuse (delta would underflow): drop rather
                // than report garbage.
                if (readBytes >= prev.Read) readBps = (readBytes - prev.Read) / seconds;
                if (writeBytes >= prev.Write) writeBps = (writeBytes - prev.Write) / seconds;
            }
        }

        _previous[pid] = new Previous(cpu, readBytes, writeBytes, nowMs);
        return (cpuPercent, readBps, writeBps);
    }

    /// <summary>Drop state for PIDs that no longer exist.</summary>
    public void Prune(HashSet<int> alive)
    {
        if (_previous.Count <= alive.Count + 32) return; // nothing could be stale yet
        List<int>? dead = null;
        foreach (var pid in _previous.Keys)
        {
            if (!alive.Contains(pid)) (dead ??= new List<int>()).Add(pid);
        }
        if (dead is null) return;
        foreach (var pid in dead) _previous.Remove(pid);
    }
}
