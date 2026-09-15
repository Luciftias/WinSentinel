using System.Diagnostics;
using WinSentinel.Models;

namespace WinSentinel.Services;

/// <summary>
/// Read-only enumeration plus guarded mutations (kill / priority / affinity / trim) over
/// running processes. Every mutating call re-checks <see cref="ProtectedProcesses"/> so the
/// guard rail holds even if the UI is bypassed.
/// </summary>
public sealed class ProcessService
{
    public readonly record struct Result(bool Ok, string Message);

    public List<ProcessInfo> GetProcesses()
    {
        var list = new List<ProcessInfo>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string priority;
                try { priority = p.PriorityClass.ToString(); }
                catch { priority = "—"; } // protected/elevated processes deny PriorityClass reads

                int threads;
                try { threads = p.Threads.Count; }
                catch { threads = 0; }

                list.Add(new ProcessInfo
                {
                    Pid = p.Id,
                    Name = p.ProcessName,
                    WorkingSetMB = p.WorkingSet64 / (1024.0 * 1024.0),
                    Priority = priority,
                    Threads = threads,
                    IsProtected = ProtectedProcesses.IsProtected(p.ProcessName)
                });
            }
            catch
            {
                // Process exited between enumeration and inspection — ignore.
            }
            finally
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }

        return list.OrderByDescending(x => x.WorkingSetMB).ToList();
    }

    public Result Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (ProtectedProcesses.IsProtected(p.ProcessName))
                return new Result(false, $"'{p.ProcessName}' is a protected system process and cannot be terminated.");

            p.Kill();
            return new Result(true, $"Terminated {p.ProcessName} (PID {pid}).");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Could not terminate PID {pid}: {ex.Message}");
        }
    }

    public Result SetPriority(int pid, ProcessPriorityClass priorityClass)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (ProtectedProcesses.IsProtected(p.ProcessName))
                return new Result(false, $"'{p.ProcessName}' is protected; priority change blocked.");

            p.PriorityClass = priorityClass;
            return new Result(true, $"Priority of {p.ProcessName} set to {priorityClass}.");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Could not change priority for PID {pid}: {ex.Message}");
        }
    }

    public Result SetAffinity(int pid, long mask)
    {
        try
        {
            if (mask == 0)
                return new Result(false, "Select at least one CPU core.");

            using var p = Process.GetProcessById(pid);
            if (ProtectedProcesses.IsProtected(p.ProcessName))
                return new Result(false, $"'{p.ProcessName}' is protected; affinity change blocked.");

            p.ProcessorAffinity = (IntPtr)mask;
            return new Result(true, $"CPU affinity of {p.ProcessName} updated.");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Could not set affinity for PID {pid}: {ex.Message}");
        }
    }

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

    /// <summary>Current processor-affinity mask for a process, or all-cores if unreadable.</summary>
    public long GetAffinity(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return (long)p.ProcessorAffinity;
        }
        catch
        {
            return (1L << Environment.ProcessorCount) - 1;
        }
    }
}
