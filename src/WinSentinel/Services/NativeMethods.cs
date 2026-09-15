using System.Runtime.InteropServices;

namespace WinSentinel.Services;

/// <summary>
/// Thin P/Invoke layer. All Win32 / NT surface area used by WinSentinel lives here so the
/// rest of the code base stays free of <c>DllImport</c> declarations.
///
/// Design notes (verified against Microsoft Learn, 2025-2026):
///  • CPU: <c>GetSystemTimes</c> deltas — no PerformanceCounter pitfalls (first-read 0,
///    counter-DB corruption 0x800007D5, locale issues).
///  • Memory: <c>GlobalMemoryStatusEx</c> — matches Task Manager's "In use" figure.
///  • Trim: <c>EmptyWorkingSet</c> (= SetProcessWorkingSetSizeEx(h,-1,-1,0)). A tuning aid,
///    not a permanent accelerator.
///  • Disk throughput: PDH "PhysicalDisk(_Total)" rate counters (see <see cref="PdhHelper"/>).
///  • Network throughput: <c>GetIfTable</c> octet counters (MIB-II), wrap-safe deltas.
///  • GPU: PDH "GPU Engine(*)\Utilization Percentage" (see <see cref="PdhHelper"/>).
///  • Efficiency Mode (EcoQoS): <c>SetProcessInformation(ProcessPowerThrottling)</c>,
///    Linux kernel semantics: ControlMask/StateMask = EXECUTION_SPEED (0x1).
///  • I/O priority: <c>NtSetInformationProcess(ProcessIoPriority = 33)</c> with
///    IO_PRIORITY_HINT (0..2 reliable; 3/4 reserved for the system). Requires
///    SeIncreaseBasePriorityPrivilege (present when running elevated as admin).
///  • Suspend/Resume: <c>NtSuspendProcess</c>/<c>NtResumeProcess</c> — the same calls used by
///    Resource Monitor / Process Explorer. Debugger-class operations; guarded in ProcessService.
/// </summary>
internal static class NativeMethods
{
    // ---------------------------------------------------------------- memory status

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ---------------------------------------------------------------- CPU times

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    public static ulong ToUInt64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    // ---------------------------------------------------------------- working set

    /// <summary>
    /// Removes as many pages as possible from the working set of the specified process.
    /// Equivalent to <c>SetProcessWorkingSetSizeEx(h, -1, -1, 0)</c>. The OS pages memory
    /// back in on demand, so this is primarily a measurement / tuning aid.
    /// </summary>
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);

    // ---------------------------------------------------------------- per-process I/O

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary>Aggregate per-process I/O accounting (file + network + device I/O).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

    // ---------------------------------------------------------------- process image path

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess, uint dwFlags, [Out] System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    // ---------------------------------------------------------------- power / battery

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0 offline, 1 online, 255 unknown
        public byte BatteryFlag;         // 1 high, 2 low, 4 critical, 8 charging, 128 no battery
        public byte BatteryLifePercent;  // 0..100, or 255 unknown
        public byte SystemStatusFlag;    // battery-saver on/off (Win10+)
        public uint BatteryLifeTime;     // seconds, unchecked(-1) unknown
        public uint BatteryFullLifeTime; // seconds, unchecked(-1) unknown
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    /// <summary>powrprof!CallNtPowerInformation — level 11 = ProcessorInformation (current MHz).</summary>
    [DllImport("powrprof.dll")]
    public static extern uint CallNtPowerInformation(
        int informationLevel, IntPtr lpInputBuffer, uint nInputBufferSize, IntPtr lpOutputBuffer, uint nOutputBufferSize);

    public const int PowerInformationProcessor = 11;

    // ---------------------------------------------------------------- network (MIB-II)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MIB_IFROW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string wszName;
        public uint dwIndex;
        public uint dwType;
        public uint dwMtu;
        public uint dwSpeed;
        public uint dwPhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] bPhysAddr;
        public uint dwAdminStatus;
        public uint dwOperStatus;
        public uint dwLastChange;
        public uint dwInOctets;
        public uint dwOutOctets;
        public uint dwInUcastPkts;
        public uint dwInNUcastPkts;
        public uint dwInDiscards;
        public uint dwInErrors;
        public uint dwInUnknownProtos;
        public uint dwOutUcastPkts;
        public uint dwOutNUcastPkts;
        public uint dwOutDiscards;
        public uint dwOutErrors;
        public uint dwOutQLen;
        public uint dwDescrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] bDescr;
    }

    public const uint IfTypeSoftwareLoopback = 24;
    public const uint IfOperStatusUp = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetIfTable(IntPtr pIfTable, ref uint pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder);

    // ---------------------------------------------------------------- process information classes

    /// <summary>
    /// PROCESS_INFORMATION_CLASS numeric values (winnt.h order). Verified against the
    /// documented enum: ProcessMemoryPriority = 0, ProcessPowerThrottling = 4.
    /// </summary>
    public static class ProcessInfoClass
    {
        public const int MemoryPriority = 0;
        public const int PowerThrottling = 4;
    }

    public static class PowerThrottling
    {
        public const uint CurrentVersion = 1;
        public const uint ExecutionSpeed = 0x1;           // EcoQoS: prefer efficient cores/frequency
        public const uint IgnoreTimerResolution = 0x4;    // keep timer resolution when backgrounded
    }

    /// <summary>MEMORY_PRIORITY values (winnt.h): lower priority pages are trimmed first.</summary>
    public static class MemoryPriorityLevel
    {
        public const uint VeryLow = 1;
        public const uint Low = 2;
        public const uint Medium = 3;
        public const uint BelowNormal = 4;
        public const uint Normal = 5;
    }

    public const int ProcessIoPriorityClass = 33; // PROCESSINFOCLASS, used by Process Explorer / psutil

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_PRIORITY_INFORMATION
    {
        public uint MemoryPriority;
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetProcessInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessInformationPowerThrottling(
        IntPtr hProcess, int processInformationClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetProcessInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessInformationMemoryPriority(
        IntPtr hProcess, int processInformationClass, ref MEMORY_PRIORITY_INFORMATION info, uint size);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetProcessInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessInformationPowerThrottling(
        IntPtr hProcess, int processInformationClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    // ---------------------------------------------------------------- NT calls (documented APIs only where possible)

    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    public static extern int NtSetInformationProcess(
        IntPtr processHandle, int processInformationClass, ref int processInformation, int processInformationLength);

    public const int SystemMemoryListInformation = 0x50; // SYSTEM_INFORMATION_CLASS
    public const int MemoryPurgeStandbyList = 4;

    [DllImport("ntdll.dll")]
    public static extern int NtSetSystemInformation(
        int systemInformationClass, ref int systemInformation, int systemInformationLength);

    // ---------------------------------------------------------------- TCP / UDP connection tables

    public const int AfInet = 2;
    public const int AfInet6 = 23;
    public const int TcpTableOwnerPidAll = 5;
    public const int UdpTableOwnerPid = 1;
    public const uint ErrorInsufficientBuffer = 122;
    public const uint ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] ucRemoteAddr;
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref uint pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref uint pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf, int tableClass, uint reserved);

    /// <summary>TCP connection states (MIB_TCP_STATE), used for the connections list.</summary>
    public static string TcpStateName(uint state) => state switch
    {
        1 => "Closed",
        2 => "Listen",
        3 => "SynSent",
        4 => "SynReceived",
        5 => "Established",
        6 => "FinWait1",
        7 => "FinWait2",
        8 => "CloseWait",
        9 => "Closing",
        10 => "LastAck",
        11 => "TimeWait",
        12 => "DeleteTcb",
        _ => state.ToString()
    };

    // ---------------------------------------------------------------- TCP extended statistics (EStats)
    // Enables the per-process TCP byte rates shown in the process table. Enabling collection
    // requires an elevated process; payload bytes only (no TCP headers), IPv4 + IPv6.

    public const int TcpConnectionEstatsData = 1;
    public const uint TcpEstatsStateEstablished = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCP6ROW
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] ucRemoteAddr;
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TCP_ESTATS_DATA_RW_V0
    {
        public byte EnableCollection;
    }

    /// <summary>Read-only dynamic data-transfer counters (tcpestats.h, field order verified).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TCP_ESTATS_DATA_ROD_V0
    {
        public ulong DataBytesOut;
        public ulong DataSegsOut;
        public ulong DataBytesIn;
        public ulong DataSegsIn;
        public ulong SegsOut;
        public ulong SegsIn;
        public uint SoftErrors;
        public uint SoftErrorReason;
        public uint SndUna;
        public uint SndNxt;
        public uint SndMax;
        public ulong ThruBytesAcked;
        public uint RcvNxt;
        public ulong ThruBytesReceived;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint SetPerTcpConnectionEStats(
        ref MIB_TCPROW row, int estatsType, ref TCP_ESTATS_DATA_RW_V0 rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint SetPerTcp6ConnectionEStats(
        ref MIB_TCP6ROW row, int estatsType, ref TCP_ESTATS_DATA_RW_V0 rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetPerTcpConnectionEStats(
        ref MIB_TCPROW row, int estatsType, ref byte rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize,
        ref TCP_ESTATS_DATA_ROD_V0 rod, uint rodVersion, uint rodSize);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetPerTcp6ConnectionEStats(
        ref MIB_TCP6ROW row, int estatsType, ref byte rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize,
        ref TCP_ESTATS_DATA_ROD_V0 rod, uint rodVersion, uint rodSize);
}
