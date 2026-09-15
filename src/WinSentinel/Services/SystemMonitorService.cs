using System.Runtime.InteropServices;
using System.Timers;
using WinSentinel.Models;
using Timer = System.Timers.Timer;

namespace WinSentinel.Services;

/// <summary>
/// Polls system-wide telemetry on a background timer and raises <see cref="SampleUpdated"/> for
/// every tick.
///
/// Sources (all verified against Microsoft docs / measured behaviour):
///  • CPU — <c>GetSystemTimes</c> deltas (idle/kernel/user). Kernel time already includes idle.
///  • RAM — <c>GlobalMemoryStatusEx</c>, matching Task Manager's "In use" figure.
///  • Disk — PDH <c>\PhysicalDisk(_Total)</c> read/write bytes/sec (≥1 s cadence, rates need deltas).
///  • Network — <c>GetIfTable</c> octet counters, per-interface wrap-safe deltas, loopback excluded.
///  • GPU — PDH <c>\GPU Engine(*)</c> busiest-engine utilisation.
///  • Battery / AC — <c>GetSystemPowerStatus</c>.
///  • CPU clock — <c>CallNtPowerInformation(ProcessorInformation)</c> average current MHz.
///
/// The service never blocks on slow sources: every read is best-effort and failures degrade to
/// the previous value rather than throwing.
/// </summary>
public sealed class SystemMonitorService : IDisposable
{
    private readonly Timer _timer;
    private readonly object _gate = new();

    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasCpuPrevious;

    private readonly DiskCounters _disk = new();
    private readonly GpuCounters _gpu = new();
    private readonly NetworkService? _network;
    private readonly TemperatureService? _temperature;

    private long _lastPdhSampleMs;
    private double _lastDiskReadBps;
    private double _lastDiskWriteBps;

    /// <summary>Fired on the timer's thread-pool thread for every captured sample.</summary>
    public event EventHandler<MetricSample>? SampleUpdated;

    /// <summary>The most recent sample, or null before the first tick completes.</summary>
    public MetricSample? Latest
    {
        get { lock (_gate) { return _latest; } }
    }
    private MetricSample? _latest;

    /// <summary>True when the machine exposes GPU Engine counters.</summary>
    public bool GpuAvailable => _gpu.Available;

    /// <summary>True when the disk rate counters were successfully primed.</summary>
    public bool DiskAvailable => _disk.Available;

    public SystemMonitorService(double intervalMilliseconds = 1000, NetworkService? network = null, TemperatureService? temperature = null)
    {
        _network = network;
        _temperature = temperature;
        _timer = new Timer(Math.Max(250, intervalMilliseconds)) { AutoReset = true };
        _timer.Elapsed += (_, _) => Sample();
    }

    public void Start()
    {
        // Prime CPU deltas and PDH rate counters so the first user-visible tick is meaningful.
        Sample();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>Changes the sampling cadence at runtime (clamped to a sane 250 ms .. 10 s).</summary>
    public void SetInterval(double milliseconds) => _timer.Interval = Math.Clamp(milliseconds, 250, 10_000);

    private void Sample()
    {
        try
        {
            long nowMs = Environment.TickCount64;

            double cpu = ReadCpu();
            var (ramPct, usedMB, totalMB, availMB) = ReadRam();
            double cpuMhz = ReadCpuMhz();
            var (batteryPresent, batteryPercent, onAc) = ReadPower();

            // PDH rate counters must be >= 1 s apart; sample them at our own fixed cadence even
            // when the UI interval is faster.
            if (nowMs - _lastPdhSampleMs >= 1000)
            {
                _lastPdhSampleMs = nowMs;
                if (_disk.TrySample(out double readBps, out double writeBps))
                {
                    _lastDiskReadBps = readBps;
                    _lastDiskWriteBps = writeBps;
                }
                _gpu.Sample(); // updates LastTotalPercent
            }

            var (netDown, netUp) = _network is null ? (0.0, 0.0) : SampleNetwork();
            var temp = _temperature?.Latest;

            var sample = new MetricSample
            {
                CpuPercent = cpu,
                CpuMhz = cpuMhz,
                RamPercent = ramPct,
                RamUsedMB = usedMB,
                RamTotalMB = totalMB,
                RamAvailableMB = availMB,
                DiskAvailable = _disk.Available && _lastPdhSampleMs > 0,
                DiskReadBps = _lastDiskReadBps,
                DiskWriteBps = _lastDiskWriteBps,
                NetDownBps = netDown,
                NetUpBps = netUp,
                GpuAvailable = _gpu.Available,
                GpuPercent = _gpu.LastTotalPercent,
                BatteryPresent = batteryPresent,
                BatteryPercent = batteryPercent,
                OnAcPower = onAc,
                TempAvailable = temp is not null,
                TempCelsius = temp?.Celsius ?? 0,
                TempZone = temp?.Zone ?? string.Empty
            };

            lock (_gate)
            {
                _latest = sample;
                SampleUpdated?.Invoke(this, sample);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Monitor sample", ex);
        }
    }

    // ---------------------------------------------------------------- CPU

    private double ReadCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return Latest?.CpuPercent ?? 0;

        ulong i = NativeMethods.ToUInt64(idle);
        ulong k = NativeMethods.ToUInt64(kernel); // kernel time INCLUDES idle time
        ulong u = NativeMethods.ToUInt64(user);

        lock (_gate)
        {
            if (!_hasCpuPrevious)
            {
                _prevIdle = i; _prevKernel = k; _prevUser = u;
                _hasCpuPrevious = true;
                return 0;
            }

            ulong idleDelta = i - _prevIdle;
            ulong kernelDelta = k - _prevKernel;
            ulong userDelta = u - _prevUser;

            _prevIdle = i; _prevKernel = k; _prevUser = u;

            ulong totalDelta = kernelDelta + userDelta;
            if (totalDelta == 0) return _latest?.CpuPercent ?? 0;

            double busy = (double)(totalDelta - idleDelta) / totalDelta * 100.0;
            return Math.Clamp(busy, 0, 100);
        }
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

    /// <summary>Average current clock across logical processors, or 0 when not reported.</summary>
    private static double ReadCpuMhz()
    {
        try
        {
            int cores = Environment.ProcessorCount;
            int size = Marshal.SizeOf<NativeMethods.PROCESSOR_POWER_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(size * cores);
            try
            {
                if (NativeMethods.CallNtPowerInformation(
                        NativeMethods.PowerInformationProcessor, IntPtr.Zero, 0, buffer, (uint)(size * cores)) != 0)
                    return 0;

                double sum = 0;
                int counted = 0;
                for (int i = 0; i < cores; i++)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.PROCESSOR_POWER_INFORMATION>(buffer + i * size);
                    if (info.CurrentMhz > 0 && info.CurrentMhz < 100_000)
                    {
                        sum += info.CurrentMhz;
                        counted++;
                    }
                }
                return counted == 0 ? 0 : sum / counted;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return 0; // firmware/virtual machines often do not report clock speeds
        }
    }

    // ---------------------------------------------------------------- network (delegated)

    /// <summary>Delegates adapter sampling to the shared NetworkService (one sample per tick).</summary>
    private (double downBps, double upBps) SampleNetwork()
    {
        try
        {
            _network!.Sample();
            return _network.LastTotals;
        }
        catch (Exception ex)
        {
            Logger.Error("Network sample", ex);
            return (0, 0);
        }
    }

    // ---------------------------------------------------------------- power

    private static (bool present, int percent, bool onAc) ReadPower()
    {
        try
        {
            if (!NativeMethods.GetSystemPowerStatus(out var status)) return (false, -1, true);

            bool unknown = status.ACLineStatus == 255 || status.BatteryFlag == 255;
            bool noBattery = (status.BatteryFlag & 128) != 0;
            bool present = !unknown && !noBattery;
            int percent = present && status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : -1;
            bool onAc = status.ACLineStatus == 1;
            return (present, percent, onAc);
        }
        catch
        {
            return (false, -1, true);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _disk.Dispose();
        _gpu.Dispose();
        SampleUpdated = null;
    }
}
