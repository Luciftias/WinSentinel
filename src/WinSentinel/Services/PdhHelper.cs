using System.Runtime.InteropServices;

namespace WinSentinel.Services;

/// <summary>
/// Minimal PDH (Performance Data Helper) wrapper — the supported, dependency-free way to read
///  • GPU utilisation: <c>\GPU Engine(*)\Utilization Percentage</c> (per-process + system total),
///  • physical disk throughput: <c>\PhysicalDisk(_Total)\Disk Read/Write Bytes/sec</c>.
///
/// Notes verified against Microsoft documentation (2025-2026):
///  • GPU counters exist on Windows 10 1709+ with WDDM 2.0+ drivers; the counter set is simply
///    absent on machines without a supported GPU, so everything degrades to "unavailable".
///  • GPU instance names look like
///    <c>pid_1234_luid_0x00000000_0x00011ABC_phys_0_eng_8_engtype_Compute_1</c> and must be
///    enumerated, not guessed. Per-process utilisation follows Task Manager's convention: the
///    busiest engine wins (summing engines can exceed 100%).
///  • Rate counters need two collections at least 1 second apart; a priming collection is done
///    at construction and the first returned value is discarded.
///  • <c>PdhAddEnglishCounterW</c> resolves the counter names on any OS language.
/// </summary>
internal sealed class PdhHelper : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;

    // ---------------------------------------------------------------- counter values

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double Value; // union member we read (doubleValue)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr Name; // LPWSTR
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    // ---------------------------------------------------------------- pdh.dll
    // NOTE: PDH exports carry no A/W suffixes except where the name itself ends in W
    // (PdhOpenQueryW / PdhAddEnglishCounterW / PdhGetFormattedCounterArrayW). ExactSpelling=true
    // is therefore mandatory — with CharSet.Unicode the runtime would probe for
    // "PdhGetFormattedCounterValueW", which does not exist.

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW", ExactSpelling = true)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW", ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);

    // ---------------------------------------------------------------- low-level helpers

    private static bool OpenQuery(out IntPtr query)
    {
        query = IntPtr.Zero;
        try { return PdhOpenQuery(null, IntPtr.Zero, out query) == 0 && query != IntPtr.Zero; }
        catch (DllNotFoundException) { return false; }
    }

    private static bool AddCounter(IntPtr query, string path, out IntPtr counter)
    {
        counter = IntPtr.Zero;
        try { return PdhAddEnglishCounter(query, path, IntPtr.Zero, out counter) == 0 && counter != IntPtr.Zero; }
        catch { return false; }
    }

    private static bool Collect(IntPtr query)
    {
        try { return PdhCollectQueryData(query) == 0; }
        catch { return false; }
    }

    private static bool ReadSingle(IntPtr counter, out double value)
    {
        value = 0;
        try
        {
            // 3rd parameter receives the counter TYPE (e.g. 0x10410500); the data status lives in
            // FmtValue.CStatus (0 = valid, 1 = new data, higher = stale/error).
            if (PdhGetFormattedCounterValue(counter, PdhFmtDouble, out _, out var v) != 0) return false;
            if (v.CStatus > 1) return false;
            value = v.Value;
            return true;
        }
        catch { return false; }
    }

    private static List<(string Name, double Value)> ReadArray(IntPtr counter)
    {
        var items = new List<(string, double)>(64);
        try
        {
            uint size = 0;
            uint count = 0;
            uint result = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out count, IntPtr.Zero);
            if (result != PdhMoreData || size == 0) return items;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                result = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out count, buffer);
                if (result != 0) return items;

                int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                for (uint i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + (int)(i * (uint)itemSize));
                    if (item.FmtValue.CStatus > 1) continue; // skip stale/invalid instances
                    string name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                    items.Add((name, item.FmtValue.Value));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch { /* PDH unavailable — return what we have */ }
        return items;
    }

    // ---------------------------------------------------------------- lifecycle

    private IntPtr _query = IntPtr.Zero;
    private readonly object _gate = new();

    /// <summary>True when the pdh.dll query could be opened.</summary>
    public bool Available { get; }

    public PdhHelper()
    {
        Available = OpenQuery(out _query);
    }

    /// <summary>Adds a counter to the shared query. Returns false when the counter set is absent.</summary>
    public bool Add(string path, out IntPtr counter)
    {
        counter = IntPtr.Zero;
        return Available && AddCounter(_query, path, out counter);
    }

    /// <summary>Collects one sample for every counter in the query (safe to call on any thread).</summary>
    public bool Sample()
    {
        if (!Available) return false;
        lock (_gate) { return Collect(_query); }
    }

    public bool TryReadSingle(IntPtr counter, out double value) => ReadSingle(counter, out value);

    public List<(string Name, double Value)> ReadAll(IntPtr counter) => ReadArray(counter);

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            try { PdhCloseQuery(_query); } catch { /* ignore */ }
            _query = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Per-process and system-wide GPU utilisation from the "GPU Engine" counter set.
/// </summary>
internal sealed class GpuCounters : IDisposable
{
    private const string GpuEnginePath = @"\GPU Engine(*)\Utilization Percentage";

    private readonly PdhHelper _pdh;
    private readonly IntPtr _counter;
    private int _samples;

    public bool Available { get; private set; }

    /// <summary>Busiest GPU engine utilisation (%), matching the Task Manager convention.</summary>
    public double LastTotalPercent { get; private set; }

    public GpuCounters()
    {
        _pdh = new PdhHelper();
        Available = _pdh.Available && _pdh.Add(GpuEnginePath, out _counter);
        if (Available)
        {
            _pdh.Sample(); // prime the rate counter
        }
    }

    /// <summary>
    /// Per-PID busiest-engine utilisation (0..100). Returns an empty map until the counter is
    /// primed (two collections >= 1s apart) or when GPU counters are unavailable.
    /// </summary>
    public Dictionary<int, double> Sample()
    {
        var result = new Dictionary<int, double>(64);
        LastTotalPercent = 0;
        if (!Available) return result;
        if (!_pdh.Sample()) return result;
        if (++_samples < 2) return result;

        var byPid = new Dictionary<int, double>(64);
        var totalByEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in _pdh.ReadAll(_counter))
        {
            if (value <= 0) continue;
            int pid = ParsePid(name);
            if (pid > 0 && (!byPid.TryGetValue(pid, out double best) || value > best))
                byPid[pid] = value;

            string engine = ParseEngineType(name);
            totalByEngine[engine] = totalByEngine.TryGetValue(engine, out double sum) ? sum + value : value;
        }

        // System total = the busiest engine type across adapters (see class docs).
        foreach (var sum in totalByEngine.Values)
            if (sum > LastTotalPercent) LastTotalPercent = sum;

        return byPid;
    }

    /// <summary>Extracts the PID from an instance name like "pid_5312_luid_0x..." .</summary>
    private static int ParsePid(string instance)
    {
        const string prefix = "pid_";
        if (!instance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return -1;
        int i = prefix.Length, start = i;
        while (i < instance.Length && char.IsDigit(instance[i])) i++;
        if (i == start) return -1;
        return int.TryParse(instance.AsSpan(start, i - start), out int pid) ? pid : -1;
    }

    private static string ParseEngineType(string instance)
    {
        const string marker = "_engtype_";
        int idx = instance.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 && idx + marker.Length < instance.Length ? instance[(idx + marker.Length)..] : "other";
    }

    public void Dispose() => _pdh.Dispose();
}

/// <summary>System disk read/write throughput from \PhysicalDisk(_Total).</summary>
internal sealed class DiskCounters : IDisposable
{
    private readonly PdhHelper _pdh;
    private readonly IntPtr _read;
    private readonly IntPtr _write;
    private int _samples;

    public bool Available { get; private set; }

    public DiskCounters()
    {
        _pdh = new PdhHelper();
        bool read = _pdh.Available && _pdh.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec", out _read);
        bool write = _pdh.Available && _pdh.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec", out _write);
        Available = read && write;
        if (Available)
        {
            _pdh.Sample(); // prime
        }
    }

    /// <summary>Reads the current disk throughput in bytes/second. False until primed.</summary>
    public bool TrySample(out double readBps, out double writeBps)
    {
        readBps = writeBps = 0;
        if (!Available) return false;
        if (!_pdh.Sample()) return false;
        if (++_samples < 2) return false;

        bool okR = _pdh.TryReadSingle(_read, out readBps);
        bool okW = _pdh.TryReadSingle(_write, out writeBps);
        return okR || okW;
    }

    public void Dispose() => _pdh.Dispose();
}
