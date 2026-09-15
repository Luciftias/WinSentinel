using System.Timers;
using WinSentinel.Models;
using Timer = System.Timers.Timer;

namespace WinSentinel.Services;

/// <summary>
/// Polls system-wide CPU and physical-memory usage on a background timer and raises
/// <see cref="SampleUpdated"/> for every tick.
///
/// CPU is computed from <c>GetSystemTimes</c> deltas (idle/kernel/user) rather than a
/// localised PerformanceCounter, which avoids the well-known "first read returns 0",
/// counter-corruption, and non-English-locale issues. Memory comes from
/// <c>GlobalMemoryStatusEx</c>, matching the percentage Task Manager shows.
/// </summary>
public sealed class SystemMonitorService : IDisposable
{
    private readonly Timer _timer;
    private readonly object _gate = new();

    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasPrevious;

    /// <summary>Fired on the timer's thread-pool thread for every captured sample.</summary>
    public event EventHandler<MetricSample>? SampleUpdated;

    /// <summary>The most recent sample, or null before the first tick completes.</summary>
    public MetricSample? Latest { get; private set; }

    public SystemMonitorService(double intervalMilliseconds = 1000)
    {
        _timer = new Timer(intervalMilliseconds) { AutoReset = true };
        _timer.Elapsed += (_, _) => Sample();
    }

    public void Start()
    {
        // Prime the CPU delta so the first user-visible tick is already meaningful.
        Sample();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Sample()
    {
        MetricSample sample;
        lock (_gate)
        {
            double cpu = ReadCpu();
            var (ramPct, used, total, avail) = ReadRam();
            sample = new MetricSample
            {
                CpuPercent = cpu,
                RamPercent = ramPct,
                RamUsedMB = used,
                RamTotalMB = total,
                RamAvailableMB = avail
            };
        }

        Latest = sample;
        SampleUpdated?.Invoke(this, sample);
    }

    private double ReadCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return Latest?.CpuPercent ?? 0;

        ulong i = NativeMethods.ToUInt64(idle);
        ulong k = NativeMethods.ToUInt64(kernel);  // kernel time INCLUDES idle time
        ulong u = NativeMethods.ToUInt64(user);

        if (!_hasPrevious)
        {
            _prevIdle = i; _prevKernel = k; _prevUser = u;
            _hasPrevious = true;
            return 0;
        }

        ulong idleDelta = i - _prevIdle;
        ulong kernelDelta = k - _prevKernel;
        ulong userDelta = u - _prevUser;

        _prevIdle = i; _prevKernel = k; _prevUser = u;

        ulong totalDelta = kernelDelta + userDelta;
        if (totalDelta == 0) return Latest?.CpuPercent ?? 0;

        double busy = (double)(totalDelta - idleDelta) / totalDelta * 100.0;
        return Math.Clamp(busy, 0, 100);
    }

    private static (double pct, double usedMB, double totalMB, double availMB) ReadRam()
    {
        var stat = new NativeMethods.MEMORYSTATUSEX();
        if (!NativeMethods.GlobalMemoryStatusEx(ref stat))
            return (0, 0, 0, 0);

        const double MB = 1024.0 * 1024.0;
        double total = stat.ullTotalPhys / MB;
        double avail = stat.ullAvailPhys / MB;
        double used = Math.Max(0, total - avail);
        return (stat.dwMemoryLoad, used, total, avail);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        SampleUpdated = null;
    }
}
