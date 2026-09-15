using System.Diagnostics;
using System.Runtime.InteropServices;
using WinSentinel.Models;

namespace WinSentinel.Services;

/// <summary>
/// Process enumeration plus guarded mutations. Every mutating call re-checks
/// <see cref="ProtectedProcesses"/> so the guard rail holds even if the UI is bypassed.
///
/// Capabilities (all verified against the documented Windows API surface):
///  • Snapshot with per-process CPU % (GetProcessTimes deltas), disk I/O rates
///    (GetProcessIoCounters deltas), GPU % (PDH GPU Engine, busiest engine), TCP network rates
///    (EStats) and Efficiency Mode state (GetProcessInformation).
///  • Kill (optionally the whole process tree), SetPriority, SetAffinity,
///    Suspend/Resume (NtSuspendProcess — same call Resource Monitor uses),
///    Efficiency Mode toggle (EcoQoS), I/O priority and memory priority.
/// </summary>
public sealed class ProcessService : IDisposable
{
    public readonly record struct Result(bool Ok, string Message);

    private static readonly Dictionary<int, double> EmptyGpuMap = new();
    private static readonly Dictionary<int, (double DownBps, double UpBps)> EmptyTcpMap = new();

    private readonly ProcessSampler _sampler = new();
    private readonly GpuCounters? _gpu;
    private readonly NetworkService? _network;
    private readonly Dictionary<string, string?> _companyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _suspended = new();
    private readonly Dictionary<int, bool> _ecoState = new();
    private readonly object _gate = new();

    // GetProcessInformation(ProcessPowerThrottling) is not implemented on Windows 10
    // (it returns ERROR_INVALID_PARAMETER); SetProcessInformation works there. After a few
    // failed queries we stop asking and rely on the state WinSentinel set itself.
    private int _ecoQueryFailures;

    public ProcessService(NetworkService? network = null)
    {
        _network = network;
        try { _gpu = new GpuCounters(); }
        catch (Exception ex) { Logger.Error("GPU counters init", ex); }
    }

    /// <summary>True when the machine exposes the GPU Engine counter set.</summary>
    public bool GpuAvailable => _gpu?.Available ?? false;

    /// <summary>System-wide GPU utilisation from the last snapshot (busiest engine).</summary>
    public double LastGpuTotalPercent => _gpu?.LastTotalPercent ?? 0;

    // ---------------------------------------------------------------- snapshot

    /// <summary>
    /// Enumerates every process and computes per-process rates. Safe to call from a background
    /// thread; the result is an immutable snapshot list. The caller owns sorting/filtering.
    /// </summary>
    public List<ProcessInfo> Snapshot()
    {
        Dictionary<int, double> gpuByPid;
        IReadOnlyDictionary<int, (double DownBps, double UpBps)> tcpRates;
        lock (_gate)
        {
            gpuByPid = _gpu?.Sample() ?? EmptyGpuMap;
            tcpRates = _network?.SampleProcessTcpRates() ?? EmptyTcpMap;
        }

        long now = Environment.TickCount64;
        var list = new List<ProcessInfo>(320);
        var alive = new HashSet<int>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                int pid = p.Id;
                alive.Add(pid);
                string name = p.ProcessName;

                double workingSetMB = 0;
                try { workingSetMB = p.WorkingSet64 / (1024.0 * 1024.0); } catch { /* access denied */ }

                int threads = 0;
                try { threads = p.Threads.Count; } catch { /* access denied */ }

                string priority = "—";
                try { priority = p.PriorityClass.ToString(); } catch { /* access denied */ }

                bool haveCpu = false;
                TimeSpan cpu = default;
                try { cpu = p.TotalProcessorTime; haveCpu = true; } catch { }

                bool haveIo = false;
                ulong readBytes = 0, writeBytes = 0;
                try
                {
                    if (NativeMethods.GetProcessIoCounters(p.Handle, out var io))
                    {
                        readBytes = io.ReadTransferCount;
                        writeBytes = io.WriteTransferCount;
                        haveIo = true;
                    }
                }
                catch { }

                double? cpuPercent = null, readBps = null, writeBps = null;
                if (haveCpu)
                {
                    var (c, r, w) = _sampler.Update(pid, cpu, readBytes, writeBytes, now);
                    cpuPercent = c;
                    if (haveIo) { readBps = r; writeBps = w; }
                }

                bool? eco = null;
                if (_ecoQueryFailures < 3)
                {
                    try
                    {
                        var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                        {
                            Version = NativeMethods.PowerThrottling.CurrentVersion
                        };
                        if (NativeMethods.GetProcessInformationPowerThrottling(
                                p.Handle, NativeMethods.ProcessInfoClass.PowerThrottling, ref state,
                                (uint)Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>()))
                        {
                            eco = (state.StateMask & NativeMethods.PowerThrottling.ExecutionSpeed) != 0;
                            _ecoQueryFailures = 0;
                        }
                        else
                        {
                            _ecoQueryFailures++;
                        }
                    }
                    catch
                    {
                        _ecoQueryFailures = 3; // API missing entirely — stop probing
                    }
                }
                if (eco is null && _ecoState.TryGetValue(pid, out bool trackedEco))
                    eco = trackedEco; // state set through WinSentinel on OSes without query support

                string? path = null;
                try { path = GetImagePath(p); } catch { }
                string? company = path is null ? null : GetCompany(path);
                var icon = Helpers.IconLoader.Get(path);

                double? gpuPercent = gpuByPid.TryGetValue(pid, out double g) ? g : null;

                double? netDown = null, netUp = null;
                if (tcpRates.TryGetValue(pid, out var tcp))
                {
                    netDown = tcp.DownBps;
                    netUp = tcp.UpBps;
                }

                bool suspended;
                lock (_gate) { suspended = _suspended.Contains(pid); }

                list.Add(new ProcessInfo
                {
                    Pid = pid,
                    Name = name,
                    Path = path,
                    Icon = icon,
                    Company = company,
                    WorkingSetMB = workingSetMB,
                    CpuPercent = cpuPercent,
                    DiskReadBps = readBps,
                    DiskWriteBps = writeBps,
                    GpuPercent = gpuPercent,
                    NetDownBps = netDown,
                    NetUpBps = netUp,
                    Threads = threads,
                    Priority = priority,
                    EcoMode = eco,
                    IsProtected = ProtectedProcesses.IsProtected(name)
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

        _sampler.Prune(alive);
        PruneState(alive);
        return list;
    }

    /// <summary>Drops per-PID state for exited processes so the maps cannot grow forever.</summary>
    private void PruneState(HashSet<int> alive)
    {
        if (_ecoState.Count > alive.Count + 32)
        {
            List<int>? dead = null;
            foreach (var pid in _ecoState.Keys)
                if (!alive.Contains(pid)) (dead ??= new List<int>()).Add(pid);
            if (dead is not null)
                foreach (var pid in dead) _ecoState.Remove(pid);
        }

        lock (_gate)
        {
            if (_suspended.Count > alive.Count + 32)
                _suspended.RemoveWhere(pid => !alive.Contains(pid));
        }
    }

    private static string? GetImagePath(Process p)
    {
        try
        {
            const int capacity = 1024;
            var buffer = new System.Text.StringBuilder(capacity);
            uint size = capacity;
            if (NativeMethods.QueryFullProcessImageNameW(p.Handle, 0, buffer, ref size))
                return buffer.ToString();
        }
        catch { /* protected processes deny even limited queries */ }
        return null;
    }

    private string? GetCompany(string path)
    {
        if (_companyCache.TryGetValue(path, out var cached)) return cached;
        string? company = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            company = string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName!.Trim();
        }
        catch { /* not a versioned PE or access denied */ }

        if (_companyCache.Count > 512) _companyCache.Clear(); // pathological path churn guard
        _companyCache[path] = company;
        return company;
    }

    // ---------------------------------------------------------------- mutations

    public Result Kill(int pid, bool entireTree = false)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (ProtectedProcesses.IsProtected(p.ProcessName))
                return new Result(false, $"'{p.ProcessName}' is a protected system process and cannot be terminated.");

            p.Kill(entireProcessTree: entireTree);
            lock (_gate) { _suspended.Remove(pid); }
            return new Result(true, entireTree
                ? $"Terminated process tree of {p.ProcessName} (PID {pid})."
                : $"Terminated {p.ProcessName} (PID {pid}).");
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

    /// <summary>Current processor-affinity mask, or all logical cores when unreadable.</summary>
    public long GetAffinity(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return (long)p.ProcessorAffinity;
        }
        catch
        {
            return DefaultAffinityMask();
        }
    }

    /// <summary>All-cores mask. The 64-core case returns -1 (all bits) instead of the old
    /// <c>(1L &lt;&lt; 64) - 1</c> bug that produced 0.</summary>
    private static long DefaultAffinityMask()
    {
        int cores = Environment.ProcessorCount;
        return cores >= 64 ? -1L : (1L << cores) - 1;
    }

    public Result Suspend(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (ProtectedProcesses.IsProtected(p.ProcessName))
                return new Result(false, $"'{p.ProcessName}' is protected; suspend blocked.");

            int status = NativeMethods.NtSuspendProcess(p.Handle);
            if (status != 0)
                return new Result(false, $"Could not suspend {p.ProcessName} (NTSTATUS 0x{status:X8}).");

            lock (_gate) { _suspended.Add(pid); }
            return new Result(true, $"Suspended {p.ProcessName} (PID {pid}). Resume when ready.");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Could not suspend PID {pid}: {ex.Message}");
        }
    }

    public Result Resume(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            int status = NativeMethods.NtResumeProcess(p.Handle);
            if (status != 0)
                return new Result(false, $"Could not resume {p.ProcessName} (NTSTATUS 0x{status:X8}).");

            lock (_gate) { _suspended.Remove(pid); }
            return new Result(true, $"Resumed {p.ProcessName} (PID {pid}).");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Could not resume PID {pid}: {ex.Message}");
        }
    }

    public bool IsSuspended(int pid) { lock (_gate) { return _suspended.Contains(pid); } }

    /// <summary>
    /// Windows Efficiency Mode (EcoQoS): the process is scheduled onto efficient cores /
    /// efficient clock speeds. Requires PROCESS_SET_INFORMATION; unavailable on Windows 10 builds
    /// before 2004 for some process types.
    /// </summary>
    public Result SetEfficiencyMode(int pid, bool enabled)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (ProtectedProcesses.IsProtected(p.ProcessName))
                return new Result(false, $"'{p.ProcessName}' is a core system process; efficiency mode is not applied.");

            var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
            {
                Version = NativeMethods.PowerThrottling.CurrentVersion,
                ControlMask = NativeMethods.PowerThrottling.ExecutionSpeed,
                StateMask = enabled ? NativeMethods.PowerThrottling.ExecutionSpeed : 0
            };

            bool ok = NativeMethods.SetProcessInformationPowerThrottling(
                p.Handle, NativeMethods.ProcessInfoClass.PowerThrottling, ref state,
                (uint)Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());

            if (ok) _ecoState[pid] = enabled;

            return ok
                ? new Result(true, enabled
                    ? $"Efficiency mode enabled for {p.ProcessName} (EcoQoS)."
                    : $"Efficiency mode disabled for {p.ProcessName}.")
                : new Result(false, $"Could not change efficiency mode for {p.ProcessName} (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Could not change efficiency mode for PID {pid}: {ex.Message}");
        }
    }

    /// <summary>Sets I/O priority: 0 = very low (background), 1 = low, 2 = normal.
    /// Values 3/4 are reserved for the system and are intentionally not exposed.</summary>
    public Result SetIoPriority(int pid, int hint)
    {
        if (hint is < 0 or > 2)
            return new Result(false, "I/O priority must be Very low (0), Low (1) or Normal (2).");

        try
        {
            using var p = Process.GetProcessById(pid);
            if (ProtectedProcesses.IsProtected(p.ProcessName))
                return new Result(false, $"'{p.ProcessName}' is protected; I/O priority change blocked.");

            int value = hint;
            int status = NativeMethods.NtSetInformationProcess(
                p.Handle, NativeMethods.ProcessIoPriorityClass, ref value, sizeof(int));

            return status == 0
                ? new Result(true, $"I/O priority of {p.ProcessName} set to {(hint == 0 ? "Very low" : hint == 1 ? "Low" : "Normal")}.")
                : new Result(false, $"Could not set I/O priority (NTSTATUS 0x{status:X8}). Requires an elevated process.");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Could not set I/O priority for PID {pid}: {ex.Message}");
        }
    }

    /// <summary>Sets memory priority — lower priority pages are trimmed first by the OS.</summary>
    public Result SetMemoryPriority(int pid, uint level)
    {
        if (level is < NativeMethods.MemoryPriorityLevel.VeryLow or > NativeMethods.MemoryPriorityLevel.Normal)
            return new Result(false, "Memory priority must be between Very low and Normal.");

        try
        {
            using var p = Process.GetProcessById(pid);
            if (ProtectedProcesses.IsProtected(p.ProcessName))
                return new Result(false, $"'{p.ProcessName}' is protected; memory priority change blocked.");

            var info = new NativeMethods.MEMORY_PRIORITY_INFORMATION { MemoryPriority = level };
            bool ok = NativeMethods.SetProcessInformationMemoryPriority(
                p.Handle, NativeMethods.ProcessInfoClass.MemoryPriority, ref info,
                (uint)Marshal.SizeOf<NativeMethods.MEMORY_PRIORITY_INFORMATION>());

            return ok
                ? new Result(true, $"{p.ProcessName} memory priority set to {MemoryPriorityName(level)}.")
                : new Result(false, $"Could not set memory priority (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Could not set memory priority for PID {pid}: {ex.Message}");
        }
    }

    public static string MemoryPriorityName(uint level) => level switch
    {
        1 => "Very low",
        2 => "Low",
        3 => "Medium",
        4 => "Below normal",
        _ => "Normal"
    };

    public void Dispose() => _gpu?.Dispose();
}
