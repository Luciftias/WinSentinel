using System.Diagnostics;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using WinSentinel.Models;

namespace WinSentinel.Services;

/// <summary>
/// Network telemetry:
///  • adapter list + system totals from <c>GetIfTable</c> (with Windows alias-row de-duplication
///    and friendly names resolved from the registry Connection map),
///  • active TCP/UDP connections (IPv4 + IPv6) from GetExtendedTcpTable/GetExtendedUdpTable,
///  • per-process TCP byte rates from TCP extended statistics (EStats). Collection is enabled
///    per connection (requires an elevated process) and counts TCP payload bytes only.
/// All sources degrade gracefully: missing data = empty results, never an exception.
/// </summary>
public sealed class NetworkService : IDisposable
{
    private readonly Dictionary<uint, (uint In, uint Out)> _adapterPrevious = new();
    private readonly Dictionary<string, string> _adapterNameCache = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<uint, string>? _wmiFriendlyNames; // interface index → "Wi-Fi" etc.
    private bool _wmiNamesRequested;

    private readonly Dictionary<string, (ulong In, ulong Out)> _tcpPrevious = new();
    private readonly Dictionary<string, NativeMethods.MIB_TCPROW> _enabledV4 = new();
    private readonly Dictionary<string, NativeMethods.MIB_TCP6ROW> _enabledV6 = new();
    private bool _estatsUnavailable;
    private long _lastTcpSampleMs;

    /// <summary>Latest adapter snapshot; refreshed by <see cref="Sample"/> (monitor cadence).</summary>
    public IReadOnlyList<AdapterInfo> LastAdapters { get; private set; } = Array.Empty<AdapterInfo>();

    /// <summary>System-wide down/up rates (bytes/second) from the last <see cref="Sample"/>.</summary>
    public (double DownBps, double UpBps) LastTotals { get; private set; }

    // ══════════════════════════════════════════════════════════ adapters

    /// <summary>Samples per-adapter deltas + system totals. Called on the monitor tick (~1 s).</summary>
    public void Sample()
    {
        try
        {
            uint size = 0;
            _ = NativeMethods.GetIfTable(IntPtr.Zero, ref size, false);
            if (size == 0 || size > 8 * 1024 * 1024) return;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (NativeMethods.GetIfTable(buffer, ref size, false) != 0) return;

                uint count = (uint)Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf<NativeMethods.MIB_IFROW>();
                if (rowSize != 860 || count > 8192) return; // layout guard

                var seen = new HashSet<uint>();
                var uniqueProfiles = new HashSet<(uint In, uint Out)>();
                var adapters = new List<AdapterInfo>(8);
                double totalDown = 0, totalUp = 0;

                for (uint i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_IFROW>(buffer + 4 + (int)i * rowSize);
                    if (row.dwType == NativeMethods.IfTypeSoftwareLoopback) continue;
                    seen.Add(row.dwIndex);

                    double down = 0, up = 0;
                    if (_adapterPrevious.TryGetValue(row.dwIndex, out var prev))
                    {
                        down = unchecked(row.dwInOctets - prev.In);
                        up = unchecked(row.dwOutOctets - prev.Out);
                    }
                    _adapterPrevious[row.dwIndex] = (row.dwInOctets, row.dwOutOctets);

                    // Windows exposes the same physical traffic through alias rows with identical
                    // counters — count each unique profile once per sample.
                    if (!uniqueProfiles.Add((row.dwInOctets, row.dwOutOctets))) continue;

                    totalDown += down;
                    totalUp += up;

                    adapters.Add(new AdapterInfo
                    {
                        Index = row.dwIndex,
                        Name = ResolveAdapterName(row),
                        Type = AdapterTypeName(row.dwType),
                        Status = row.dwOperStatus == 1 ? "Up" : "—",
                        LinkSpeedBps = row.dwSpeed,
                        DownBps = down,
                        UpBps = up,
                        TotalInBytes = row.dwInOctets,
                        TotalOutBytes = row.dwOutOctets
                    });
                }

                PruneAdapterState(seen);
                LastAdapters = adapters
                    .OrderByDescending(a => a.DownBps + a.UpBps)
                    .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                LastTotals = (totalDown, totalUp);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Network adapter sample", ex);
        }
    }

    private void PruneAdapterState(HashSet<uint> seen)
    {
        if (_adapterPrevious.Count <= seen.Count + 16) return;
        List<uint>? dead = null;
        foreach (var index in _adapterPrevious.Keys)
            if (!seen.Contains(index)) (dead ??= new List<uint>()).Add(index);
        if (dead is not null)
            foreach (var index in dead) _adapterPrevious.Remove(index);
    }

    private string ResolveAdapterName(NativeMethods.MIB_IFROW row)
    {
        // 1. Friendly name from the registry map: \DEVICE\TCPIP_{GUID} → HKLM\...\Network\{GUID}\Connection → "Name"
        string device = row.wszName ?? string.Empty;
        int open = device.LastIndexOf('{');
        int close = device.LastIndexOf('}');
        if (open >= 0 && close > open)
        {
            string guid = device[open..(close + 1)];
            if (_adapterNameCache.TryGetValue(guid, out var cached)) return cached;

            string? friendly = null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\" + guid + @"\Connection");
                friendly = key?.GetValue("Name") as string;
            }
            catch { /* registry unavailable — fall through */ }

            if (!string.IsNullOrWhiteSpace(friendly))
            {
                _adapterNameCache[guid] = friendly!;
                return friendly!;
            }
        }

        // 2. WMI NetConnectionID map ("Wi-Fi", "Ethernet") — queried once in the background.
        if (_wmiFriendlyNames is { } map && map.TryGetValue(row.dwIndex, out var wmiName))
            return wmiName;
        EnsureWmiNames();

        // 3. NDIS description (cleaned of "-WFP …" filter suffixes), 4. raw device path.
        try
        {
            if (row.dwDescrLen > 0 && row.bDescr is { Length: > 0 })
            {
                int len = Math.Min((int)row.dwDescrLen, row.bDescr.Length);
                string descr = Encoding.ASCII.GetString(row.bDescr, 0, len).TrimEnd('\0', ' ');
                descr = CleanDescription(descr);
                if (descr.Length > 0) return descr;
            }
        }
        catch { /* ignore */ }

        return device.Replace(@"\DEVICE\TCPIP_", string.Empty).Trim('\\');
    }

    /// <summary>GetIfTable descriptions often carry the bound NDIS filter name; strip it.</summary>
    private static string CleanDescription(string description)
    {
        int idx = description.IndexOf("-WFP", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) description = description[..idx];
        idx = description.IndexOf("-Native MAC", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) description = description[..idx];
        return description.Trim();
    }

    /// <summary>Loads interface-index → friendly-name once, in the background, and caches it.</summary>
    private void EnsureWmiNames()
    {
        if (_wmiNamesRequested) return;
        _wmiNamesRequested = true;

        _ = Task.Run(() =>
        {
            try
            {
                var map = new Dictionary<uint, string>();
                using var searcher = new ManagementObjectSearcher(
                    @"root\cimv2",
                    "SELECT InterfaceIndex, NetConnectionID FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL");

                foreach (ManagementBaseObject adapter in searcher.Get())
                {
                    using (adapter)
                    {
                        if (adapter["InterfaceIndex"] is null ||
                            adapter["NetConnectionID"] is not string name || name.Length == 0)
                            continue;
                        map[Convert.ToUInt32(adapter["InterfaceIndex"])] = name;
                    }
                }
                _wmiFriendlyNames = map;
            }
            catch (Exception ex)
            {
                Logger.Error("Adapter friendly-name query", ex);
                _wmiFriendlyNames = new Dictionary<uint, string>();
            }
        });
    }

    private static string AdapterTypeName(uint type) => type switch
    {
        6 => "Ethernet",
        23 => "PPP",
        24 => "Loopback",
        53 => "Virtual",
        71 => "Wi-Fi",
        131 => "Tunnel",
        237 => "IEEE 1394",
        243 or 244 => "Mobile",
        _ => $"Type {type}"
    };

    // ══════════════════════════════════════════════════════════ connections

    /// <summary>Active TCP + UDP endpoints (IPv4 and IPv6), with process names resolved by the caller.</summary>
    public List<ConnectionInfo> GetConnections(IReadOnlyDictionary<int, string> processNames)
    {
        var list = new List<ConnectionInfo>(256);
        ReadTcp4(processNames, list);
        ReadTcp6(processNames, list);
        ReadUdp4(processNames, list);
        ReadUdp6(processNames, list);
        return list;
    }

    private static string PidName(IReadOnlyDictionary<int, string> names, int pid)
        => names.TryGetValue(pid, out var name) ? name : pid > 0 ? $"PID {pid}" : "—";

    private static void ReadTcp4(IReadOnlyDictionary<int, string> names, List<ConnectionInfo> sink)
    {
        uint size = 0;
        if (NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, true, NativeMethods.AfInet, NativeMethods.TcpTableOwnerPidAll, 0)
            != NativeMethods.ErrorInsufficientBuffer || size == 0 || size > 8 * 1024 * 1024) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetExtendedTcpTable(buffer, ref size, true, NativeMethods.AfInet, NativeMethods.TcpTableOwnerPidAll, 0) != 0) return;

            uint count = (uint)Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
            for (uint i = 0; i < count && i < 100_000; i++)
            {
                var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(buffer + 4 + (int)i * rowSize);
                int pid = (int)row.dwOwningPid;
                sink.Add(new ConnectionInfo
                {
                    Pid = pid,
                    ProcessName = PidName(names, pid),
                    Protocol = "TCP4",
                    State = NativeMethods.TcpStateName(row.dwState),
                    Local = FormatEndpoint(row.dwLocalAddr, row.dwLocalPort),
                    Remote = row.dwRemoteAddr == 0 && row.dwRemotePort == 0
                        ? "—"
                        : FormatEndpoint(row.dwRemoteAddr, row.dwRemotePort)
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error("TCP4 table read", ex);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ReadTcp6(IReadOnlyDictionary<int, string> names, List<ConnectionInfo> sink)
    {
        uint size = 0;
        if (NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, true, NativeMethods.AfInet6, NativeMethods.TcpTableOwnerPidAll, 0)
            != NativeMethods.ErrorInsufficientBuffer || size == 0 || size > 8 * 1024 * 1024) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetExtendedTcpTable(buffer, ref size, true, NativeMethods.AfInet6, NativeMethods.TcpTableOwnerPidAll, 0) != 0) return;

            uint count = (uint)Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCP6ROW_OWNER_PID>();
            for (uint i = 0; i < count && i < 100_000; i++)
            {
                var row = Marshal.PtrToStructure<NativeMethods.MIB_TCP6ROW_OWNER_PID>(buffer + 4 + (int)i * rowSize);
                int pid = (int)row.dwOwningPid;
                bool noRemote = row.ucRemoteAddr.All(b => b == 0) && row.dwRemotePort == 0;
                sink.Add(new ConnectionInfo
                {
                    Pid = pid,
                    ProcessName = PidName(names, pid),
                    Protocol = "TCP6",
                    State = NativeMethods.TcpStateName(row.dwState),
                    Local = FormatEndpoint6(row.ucLocalAddr, row.dwLocalScopeId, row.dwLocalPort),
                    Remote = noRemote ? "—" : FormatEndpoint6(row.ucRemoteAddr, row.dwRemoteScopeId, row.dwRemotePort)
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error("TCP6 table read", ex);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ReadUdp4(IReadOnlyDictionary<int, string> names, List<ConnectionInfo> sink)
    {
        uint size = 0;
        if (NativeMethods.GetExtendedUdpTable(IntPtr.Zero, ref size, true, NativeMethods.AfInet, NativeMethods.UdpTableOwnerPid, 0)
            != NativeMethods.ErrorInsufficientBuffer || size == 0 || size > 8 * 1024 * 1024) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetExtendedUdpTable(buffer, ref size, true, NativeMethods.AfInet, NativeMethods.UdpTableOwnerPid, 0) != 0) return;

            uint count = (uint)Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<NativeMethods.MIB_UDPROW_OWNER_PID>();
            for (uint i = 0; i < count && i < 100_000; i++)
            {
                var row = Marshal.PtrToStructure<NativeMethods.MIB_UDPROW_OWNER_PID>(buffer + 4 + (int)i * rowSize);
                int pid = (int)row.dwOwningPid;
                sink.Add(new ConnectionInfo
                {
                    Pid = pid,
                    ProcessName = PidName(names, pid),
                    Protocol = "UDP4",
                    State = "—",
                    Local = FormatEndpoint(row.dwLocalAddr, row.dwLocalPort),
                    Remote = "—"
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error("UDP4 table read", ex);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ReadUdp6(IReadOnlyDictionary<int, string> names, List<ConnectionInfo> sink)
    {
        uint size = 0;
        if (NativeMethods.GetExtendedUdpTable(IntPtr.Zero, ref size, true, NativeMethods.AfInet6, NativeMethods.UdpTableOwnerPid, 0)
            != NativeMethods.ErrorInsufficientBuffer || size == 0 || size > 8 * 1024 * 1024) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetExtendedUdpTable(buffer, ref size, true, NativeMethods.AfInet6, NativeMethods.UdpTableOwnerPid, 0) != 0) return;

            uint count = (uint)Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<NativeMethods.MIB_UDP6ROW_OWNER_PID>();
            for (uint i = 0; i < count && i < 100_000; i++)
            {
                var row = Marshal.PtrToStructure<NativeMethods.MIB_UDP6ROW_OWNER_PID>(buffer + 4 + (int)i * rowSize);
                int pid = (int)row.dwOwningPid;
                sink.Add(new ConnectionInfo
                {
                    Pid = pid,
                    ProcessName = PidName(names, pid),
                    Protocol = "UDP6",
                    State = "—",
                    Local = FormatEndpoint6(row.ucLocalAddr, row.dwLocalScopeId, row.dwLocalPort),
                    Remote = "—"
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error("UDP6 table read", ex);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ushort NetToHostPort(uint port) => (ushort)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    private static string FormatEndpoint(uint address, uint port)
    {
        try
        {
            return $"{new IPAddress(address)}:{NetToHostPort(port)}";
        }
        catch
        {
            return "—";
        }
    }

    private static string FormatEndpoint6(byte[] address, uint scopeId, uint port)
    {
        try
        {
            var ip = new IPAddress(address, scopeId);
            string host = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal
                ? $"{ip}%{scopeId}"
                : ip.ToString();
            return $"[{host}]:{NetToHostPort(port)}";
        }
        catch
        {
            return "—";
        }
    }

    // ══════════════════════════════════════════════════════════ per-process TCP rates (EStats)

    /// <summary>
    /// Per-PID TCP payload rates (IPv4 + IPv6) from TCP extended statistics. Returns an empty map
    /// when EStats cannot be enabled (non-elevated process) or before the first delta.
    /// </summary>
    public IReadOnlyDictionary<int, (double DownBps, double UpBps)> SampleProcessTcpRates()
    {
        var result = new Dictionary<int, (double, double)>();
        if (_estatsUnavailable) return result;

        try
        {
            long now = Environment.TickCount64;
            double elapsed = _lastTcpSampleMs == 0 ? 0 : (now - _lastTcpSampleMs) / 1000.0;
            _lastTcpSampleMs = now;
            bool canComputeRates = elapsed > 0.2;

            var seen = new HashSet<string>();
            SampleTcp4(result, seen, canComputeRates, elapsed);
            if (_estatsUnavailable) return result;
            SampleTcp6(result, seen, canComputeRates, elapsed);

            PruneTcpState(seen);
        }
        catch (Exception ex)
        {
            Logger.Error("TCP EStats sample", ex);
        }
        return result;
    }

    private void SampleTcp4(Dictionary<int, (double, double)> result, HashSet<string> seen, bool canComputeRates, double elapsed)
    {
        foreach (var row in EnumerateTcp4()) 
        {
            if (row.dwState != NativeMethods.TcpEstatsStateEstablished || row.dwOwningPid == 0) continue;

            string key = $"4|{row.dwLocalAddr}:{row.dwLocalPort}|{row.dwRemoteAddr}:{row.dwRemotePort}";
            seen.Add(key);

            if (!_enabledV4.TryGetValue(key, out var mibRow))
            {
                mibRow = new NativeMethods.MIB_TCPROW
                {
                    dwState = row.dwState,
                    dwLocalAddr = row.dwLocalAddr,
                    dwLocalPort = row.dwLocalPort,
                    dwRemoteAddr = row.dwRemoteAddr,
                    dwRemotePort = row.dwRemotePort
                };

                var rw = new NativeMethods.TCP_ESTATS_DATA_RW_V0 { EnableCollection = 1 };
                uint rc = NativeMethods.SetPerTcpConnectionEStats(ref mibRow, NativeMethods.TcpConnectionEstatsData, ref rw, 0, 1, 0);
                if (rc == NativeMethods.ErrorAccessDenied)
                {
                    _estatsUnavailable = true; // needs an elevated process
                    Logger.Info("Per-process TCP stats (EStats) require elevation; per-process network rates disabled.");
                    return;
                }
                if (rc != 0) continue;
                _enabledV4[key] = mibRow;
            }

            byte enabled = 0;
            var rod = default(NativeMethods.TCP_ESTATS_DATA_ROD_V0);
            var readRow = mibRow;
            uint readRc = NativeMethods.GetPerTcpConnectionEStats(
                ref readRow, NativeMethods.TcpConnectionEstatsData, ref enabled, 0, 1,
                IntPtr.Zero, 0, 0, ref rod, 0, (uint)Marshal.SizeOf<NativeMethods.TCP_ESTATS_DATA_ROD_V0>());
            if (readRc != 0 || enabled == 0) continue;

            if (canComputeRates && _tcpPrevious.TryGetValue(key, out var prev))
            {
                double down = rod.DataBytesIn >= prev.In ? (rod.DataBytesIn - prev.In) / elapsed : 0;
                double up = rod.DataBytesOut >= prev.Out ? (rod.DataBytesOut - prev.Out) / elapsed : 0;
                if (down > 0 || up > 0)
                {
                    var current = result.TryGetValue((int)row.dwOwningPid, out var v) ? v : (0, 0);
                    result[(int)row.dwOwningPid] = (current.Item1 + down, current.Item2 + up);
                }
            }
            _tcpPrevious[key] = (rod.DataBytesIn, rod.DataBytesOut);
        }
    }

    private void SampleTcp6(Dictionary<int, (double, double)> result, HashSet<string> seen, bool canComputeRates, double elapsed)
    {
        foreach (var row in EnumerateTcp6())
        {
            if (row.dwState != NativeMethods.TcpEstatsStateEstablished || row.dwOwningPid == 0) continue;

            string key = $"6|{Convert.ToHexString(row.ucLocalAddr)}:{row.dwLocalPort}|{Convert.ToHexString(row.ucRemoteAddr)}:{row.dwRemotePort}";
            seen.Add(key);

            if (!_enabledV6.TryGetValue(key, out var mibRow))
            {
                mibRow = new NativeMethods.MIB_TCP6ROW
                {
                    ucLocalAddr = row.ucLocalAddr,
                    dwLocalScopeId = row.dwLocalScopeId,
                    dwLocalPort = row.dwLocalPort,
                    ucRemoteAddr = row.ucRemoteAddr,
                    dwRemoteScopeId = row.dwRemoteScopeId,
                    dwRemotePort = row.dwRemotePort,
                    dwState = row.dwState
                };

                var rw = new NativeMethods.TCP_ESTATS_DATA_RW_V0 { EnableCollection = 1 };
                uint rc = NativeMethods.SetPerTcp6ConnectionEStats(ref mibRow, NativeMethods.TcpConnectionEstatsData, ref rw, 0, 1, 0);
                if (rc == NativeMethods.ErrorAccessDenied)
                {
                    _estatsUnavailable = true;
                    Logger.Info("Per-process TCP stats (EStats) require elevation; per-process network rates disabled.");
                    return;
                }
                if (rc != 0) continue;
                _enabledV6[key] = mibRow;
            }

            byte enabled = 0;
            var rod = default(NativeMethods.TCP_ESTATS_DATA_ROD_V0);
            var readRow = mibRow;
            uint readRc = NativeMethods.GetPerTcp6ConnectionEStats(
                ref readRow, NativeMethods.TcpConnectionEstatsData, ref enabled, 0, 1,
                IntPtr.Zero, 0, 0, ref rod, 0, (uint)Marshal.SizeOf<NativeMethods.TCP_ESTATS_DATA_ROD_V0>());
            if (readRc != 0 || enabled == 0) continue;

            if (canComputeRates && _tcpPrevious.TryGetValue(key, out var prev))
            {
                double down = rod.DataBytesIn >= prev.In ? (rod.DataBytesIn - prev.In) / elapsed : 0;
                double up = rod.DataBytesOut >= prev.Out ? (rod.DataBytesOut - prev.Out) / elapsed : 0;
                if (down > 0 || up > 0)
                {
                    var current = result.TryGetValue((int)row.dwOwningPid, out var v) ? v : (0, 0);
                    result[(int)row.dwOwningPid] = (current.Item1 + down, current.Item2 + up);
                }
            }
            _tcpPrevious[key] = (rod.DataBytesIn, rod.DataBytesOut);
        }
    }

    private void PruneTcpState(HashSet<string> seen)
    {
        if (_tcpPrevious.Count > seen.Count * 2 + 64)
        {
            List<string>? dead = null;
            foreach (var key in _tcpPrevious.Keys)
                if (!seen.Contains(key)) (dead ??= new List<string>()).Add(key);
            if (dead is not null)
                foreach (var key in dead) _tcpPrevious.Remove(key);
        }
        if (_enabledV4.Count > 4096) _enabledV4.Clear(); // connections long gone; keep the map bounded
        if (_enabledV6.Count > 4096) _enabledV6.Clear();
    }

    private static IEnumerable<NativeMethods.MIB_TCPROW_OWNER_PID> EnumerateTcp4()
    {
        uint size = 0;
        if (NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, true, NativeMethods.AfInet, NativeMethods.TcpTableOwnerPidAll, 0)
            != NativeMethods.ErrorInsufficientBuffer || size == 0 || size > 8 * 1024 * 1024) yield break;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetExtendedTcpTable(buffer, ref size, true, NativeMethods.AfInet, NativeMethods.TcpTableOwnerPidAll, 0) != 0) yield break;

            uint count = (uint)Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
            for (uint i = 0; i < count && i < 200_000; i++)
                yield return Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(buffer + 4 + (int)i * rowSize);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IEnumerable<NativeMethods.MIB_TCP6ROW_OWNER_PID> EnumerateTcp6()
    {
        uint size = 0;
        if (NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, true, NativeMethods.AfInet6, NativeMethods.TcpTableOwnerPidAll, 0)
            != NativeMethods.ErrorInsufficientBuffer || size == 0 || size > 8 * 1024 * 1024) yield break;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetExtendedTcpTable(buffer, ref size, true, NativeMethods.AfInet6, NativeMethods.TcpTableOwnerPidAll, 0) != 0) yield break;

            uint count = (uint)Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCP6ROW_OWNER_PID>();
            for (uint i = 0; i < count && i < 200_000; i++)
                yield return Marshal.PtrToStructure<NativeMethods.MIB_TCP6ROW_OWNER_PID>(buffer + 4 + (int)i * rowSize);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>True when per-process TCP statistics could be enabled.</summary>
    public bool ProcessTcpStatsAvailable => !_estatsUnavailable;

    // ══════════════════════════════════════════════════════════ lifecycle

    public void Dispose()
    {
        // Politely disable the EStats collection we enabled, best effort.
        var off = new NativeMethods.TCP_ESTATS_DATA_RW_V0 { EnableCollection = 0 };
        foreach (var row in _enabledV4.Values)
        {
            try
            {
                var copy = row;
                NativeMethods.SetPerTcpConnectionEStats(ref copy, NativeMethods.TcpConnectionEstatsData, ref off, 0, 1, 0);
            }
            catch { /* ignore */ }
        }
        foreach (var row in _enabledV6.Values)
        {
            try
            {
                var copy = row;
                NativeMethods.SetPerTcp6ConnectionEStats(ref copy, NativeMethods.TcpConnectionEstatsData, ref off, 0, 1, 0);
            }
            catch { /* ignore */ }
        }
        _enabledV4.Clear();
        _enabledV6.Clear();
    }
}
