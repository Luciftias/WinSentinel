using System.Diagnostics;

namespace WinSentinel.Services;

/// <summary>
/// Memory tuning that is honest about what it does:
///  • <see cref="TrimAll"/> asks Windows to page out working sets (EmptyWorkingSet). Useful
///    after heavy workloads; the OS pages memory back in on demand — a tuning aid, not a
///    permanent accelerator.
///  • <see cref="PurgeStandby"/> clears the standby (cached) list system-wide. Requires
///    administrator rights (SeProfileSingleProcessPrivilege). It is safe but counter-productive
///    if used often: Windows immediately refills the cache from disk, causing a brief I/O spike.
/// </summary>
public sealed class MemoryOptimizer
{
    public readonly record struct TrimResult(int Trimmed, int Skipped, double FreedMB);

    public readonly record struct Result(bool Ok, string Message);

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

    /// <summary>
    /// System-wide standby-list purge via NtSetSystemInformation(SystemMemoryListInformation,
    /// MemoryPurgeStandbyList). Administrator only. Presented in the UI as an advanced action
    /// with its real trade-off (cache is rebuilt from disk afterwards).
    /// </summary>
    public Result PurgeStandby()
    {
        try
        {
            int command = NativeMethods.MemoryPurgeStandbyList;
            int status = NativeMethods.NtSetSystemInformation(
                NativeMethods.SystemMemoryListInformation, ref command, sizeof(int));

            return status == 0
                ? new Result(true, "Standby list purged. Cache rebuilds on demand — expect a brief disk-I/O spike.")
                : new Result(false, $"Standby purge failed (NTSTATUS 0x{status:X8}). It requires administrator rights.");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Standby purge failed: {ex.Message}");
        }
    }
}
