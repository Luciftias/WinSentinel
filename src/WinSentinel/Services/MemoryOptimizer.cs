using System.Diagnostics;

namespace WinSentinel.Services;

/// <summary>
/// Working-set trimming via <c>EmptyWorkingSet</c>. This does NOT free or "clean" RAM in
/// any permanent sense — it asks Windows to page a process's resident pages out so the
/// "in use" figure drops; the OS pages them back in on demand. It is genuinely useful for
/// measurement and tuning, and for reclaiming bloated working sets after heavy workloads,
/// but it is not a magic accelerator. WinSentinel surfaces it honestly.
/// </summary>
public sealed class MemoryOptimizer
{
    public readonly record struct TrimResult(int Trimmed, int Skipped, double FreedMB);

    /// <summary>Trim a single process by PID. Returns false on access-denied/exited.</summary>
    public bool TrimProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (ProtectedProcesses.IsProtected(p.ProcessName)) return false;
            return NativeMethods.EmptyWorkingSet(p.Handle);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Trim every accessible, non-protected process. Returns a best-effort estimate of the
    /// reclaimed working set (sum of before/after deltas for the processes we could read).
    /// </summary>
    public TrimResult TrimAll(Func<Process, bool>? predicate = null)
    {
        int trimmed = 0, skipped = 0;
        double beforeBytes = 0, afterBytes = 0;

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (ProtectedProcesses.IsProtected(p.ProcessName)) { skipped++; continue; }
                if (predicate != null && !predicate(p)) { skipped++; continue; }

                p.Refresh();
                long before = p.WorkingSet64;

                if (NativeMethods.EmptyWorkingSet(p.Handle))
                {
                    trimmed++;
                    p.Refresh();
                    long after = p.WorkingSet64;
                    beforeBytes += before;
                    afterBytes += after;
                }
                else
                {
                    skipped++;
                }
            }
            catch
            {
                skipped++;
            }
            finally
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }

        double freedMB = Math.Max(0, beforeBytes - afterBytes) / (1024.0 * 1024.0);
        return new TrimResult(trimmed, skipped, freedMB);
    }
}
