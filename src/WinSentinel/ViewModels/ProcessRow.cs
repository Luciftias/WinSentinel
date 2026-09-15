using WinSentinel.Models;

namespace WinSentinel.ViewModels;

/// <summary>
/// Mutable row view-model for the process table. Rows are created once per PID and updated in
/// place on every snapshot so selection, sorting and scroll position survive refreshes.
/// </summary>
public sealed class ProcessRow : ViewModelBase
{
    public int Pid { get; }

    private string _name;
    public string Name { get => _name; private set => SetField(ref _name, value); }

    /// <summary>Full image path (for tooltips / "open file location").</summary>
    public string? Path { get; private set; }

    /// <summary>Publisher from version info, when readable.</summary>
    public string? Company { get; private set; }

    private double _memoryMB;
    public double MemoryMB
    {
        get => _memoryMB;
        private set { if (SetField(ref _memoryMB, value)) OnPropertyChanged(nameof(MemoryDisplay)); }
    }
    public string MemoryDisplay => $"{MemoryMB:N1} MB";

    private double? _cpuPercent;
    public double? CpuPercent
    {
        get => _cpuPercent;
        private set { if (SetField(ref _cpuPercent, value)) OnPropertyChanged(nameof(CpuDisplay)); }
    }
    public string CpuDisplay => CpuPercent is double c ? $"{c:0.0}%" : "—";

    private double? _diskBps;
    public double? DiskBps
    {
        get => _diskBps;
        private set { if (SetField(ref _diskBps, value)) OnPropertyChanged(nameof(DiskDisplay)); }
    }
    public string DiskDisplay => DiskBps is double b && b > 0 ? FormatRate(b) : "—";

    private double? _gpuPercent;
    public double? GpuPercent
    {
        get => _gpuPercent;
        private set { if (SetField(ref _gpuPercent, value)) OnPropertyChanged(nameof(GpuDisplay)); }
    }
    public string GpuDisplay => GpuPercent is double g && g > 0 ? $"{g:0.0}%" : "—";

    private int _threads;
    public int Threads { get => _threads; private set => SetField(ref _threads, value); }

    private string _priority = "—";
    public string Priority { get => _priority; private set => SetField(ref _priority, value); }

    private bool? _ecoMode;
    public bool? EcoMode
    {
        get => _ecoMode;
        private set { if (SetField(ref _ecoMode, value)) OnPropertyChanged(nameof(IsEco)); }
    }
    public bool IsEco => EcoMode == true;

    private bool _isSuspended;
    public bool IsSuspended
    {
        get => _isSuspended;
        set
        {
            if (SetField(ref _isSuspended, value))
                OnPropertyChanged(nameof(StateDisplay));
        }
    }
    public string StateDisplay => IsSuspended ? "Suspended" : string.Empty;

    private bool _isProtected;
    public bool IsProtected { get => _isProtected; private set => SetField(ref _isProtected, value); }

    public ProcessRow(ProcessInfo info)
    {
        Pid = info.Pid;
        _name = info.Name;
        Apply(info);
    }

    /// <summary>Updates the row in place from a fresh snapshot entry.</summary>
    public void Apply(ProcessInfo info)
    {
        Name = info.Name;
        Path = info.Path;
        Company = info.Company;
        MemoryMB = info.WorkingSetMB;
        CpuPercent = info.CpuPercent;
        DiskBps = info.DiskReadBps is null && info.DiskWriteBps is null
            ? null
            : (info.DiskReadBps ?? 0) + (info.DiskWriteBps ?? 0);
        GpuPercent = info.GpuPercent;
        Threads = info.Threads;
        Priority = info.Priority;
        EcoMode = info.EcoMode;
        IsProtected = info.IsProtected;
    }

    /// <summary>Search match against name, PID or publisher.</summary>
    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        query = query.Trim();

        if (Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (Pid.ToString().Contains(query, StringComparison.Ordinal)) return true;
        if (Company is not null && Company.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Human-readable byte rate (used by the process table and the dashboard cards).</summary>
    public static string FormatRate(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1_073_741_824 => $"{bytesPerSecond / 1_073_741_824:0.0} GB/s",
        >= 1_048_576 => $"{bytesPerSecond / 1_048_576:0.0} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:0} KB/s",
        > 0 => $"{bytesPerSecond:0} B/s",
        _ => "0"
    };
}
