using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using WinSentinel.Models;
using WinSentinel.Services;

namespace WinSentinel.ViewModels;

/// <summary>
/// View model for the main dashboard: live gauges + history sparklines, the process table
/// (kill / priority / affinity / trim) and the startup-program manager. All destructive
/// operations route through services that enforce the protected-process guard rail; the UI
/// additionally asks for confirmation.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly SystemMonitorService _monitor;
    private readonly MemoryOptimizer _memory = new();
    private readonly ProcessService _processes = new();
    private readonly StartupManager _startup = new();
    private readonly Dispatcher _dispatcher;

    public int HistoryLength => 60;
    public int LogicalProcessors => Environment.ProcessorCount;

    public ObservableCollection<double> CpuHistory { get; } = new();
    public ObservableCollection<double> RamHistory { get; } = new();
    public ObservableCollection<ProcessInfo> Processes { get; } = new();
    public ObservableCollection<StartupItem> StartupItems { get; } = new();

    private double _cpu;
    public double Cpu { get => _cpu; private set { if (SetField(ref _cpu, value)) OnPropertyChanged(nameof(CpuText)); } }

    private double _ram;
    public double Ram { get => _ram; private set { if (SetField(ref _ram, value)) OnPropertyChanged(nameof(RamText)); } }

    public string CpuText => $"{Cpu:0}%";
    public string RamText => $"{Ram:0}%";

    private string _ramDetail = "—";
    public string RamDetail { get => _ramDetail; private set => SetField(ref _ramDetail, value); }

    private string _status = "Monitoring system…";
    public string Status { get => _status; set => SetField(ref _status, value); }

    private ProcessInfo? _selectedProcess;
    public ProcessInfo? SelectedProcess { get => _selectedProcess; set => SetField(ref _selectedProcess, value); }

    private StartupItem? _selectedStartup;
    public StartupItem? SelectedStartup { get => _selectedStartup; set => SetField(ref _selectedStartup, value); }

    public RelayCommand TrimAllCommand { get; }
    public RelayCommand TrimSelectedCommand { get; }
    public RelayCommand RefreshProcessesCommand { get; }
    public RelayCommand KillSelectedCommand { get; }
    public RelayCommand RefreshStartupCommand { get; }
    public RelayCommand RemoveStartupCommand { get; }

    public DashboardViewModel(SystemMonitorService monitor)
    {
        _monitor = monitor;
        _dispatcher = Dispatcher.CurrentDispatcher;

        for (int i = 0; i < HistoryLength; i++) { CpuHistory.Add(0); RamHistory.Add(0); }

        _monitor.SampleUpdated += OnSample;

        TrimAllCommand          = new RelayCommand(_ => TrimAll());
        TrimSelectedCommand     = new RelayCommand(_ => TrimSelected(), _ => SelectedProcess is not null);
        RefreshProcessesCommand = new RelayCommand(_ => RefreshProcesses());
        KillSelectedCommand     = new RelayCommand(_ => KillSelected(), _ => SelectedProcess is not null);
        RefreshStartupCommand   = new RelayCommand(_ => RefreshStartup());
        RemoveStartupCommand    = new RelayCommand(_ => RemoveStartup(), _ => SelectedStartup?.Removable == true);

        RefreshProcesses();
        RefreshStartup();
        if (_monitor.Latest is not null) Apply(_monitor.Latest);
    }

    // ---- Live sampling -----------------------------------------------------

    private void OnSample(object? sender, MetricSample sample)
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => Apply(sample)); return; }
        Apply(sample);
    }

    private void Apply(MetricSample s)
    {
        Cpu = s.CpuPercent;
        Ram = s.RamPercent;
        RamDetail = $"{s.RamUsedMB / 1024.0:0.0} GB / {s.RamTotalMB / 1024.0:0.0} GB  •  {s.RamAvailableMB / 1024.0:0.0} GB free";
        Push(CpuHistory, s.CpuPercent);
        Push(RamHistory, s.RamPercent);
    }

    private void Push(ObservableCollection<double> series, double value)
    {
        series.Add(value);
        while (series.Count > HistoryLength) series.RemoveAt(0);
    }

    // ---- Memory ------------------------------------------------------------

    private void TrimAll()
    {
        Status = "Trimming working sets…";
        var r = _memory.TrimAll();
        Status = $"Trimmed {r.Trimmed} processes • ~{r.FreedMB:0} MB reclaimed • {r.Skipped} skipped.";
        RefreshProcesses();
    }

    private void TrimSelected()
    {
        if (SelectedProcess is null) return;
        bool ok = _processes.TrimProcess(SelectedProcess.Pid);
        Status = ok ? $"Trimmed working set of {SelectedProcess.Name}." : $"Could not trim {SelectedProcess.Name} (protected or access denied).";
        RefreshProcesses();
    }

    // ---- Processes ---------------------------------------------------------

    private void RefreshProcesses()
    {
        int? keepPid = SelectedProcess?.Pid;
        Processes.Clear();
        foreach (var p in _processes.GetProcesses()) Processes.Add(p);
        if (keepPid is int pid) SelectedProcess = Processes.FirstOrDefault(x => x.Pid == pid);
    }

    private void KillSelected()
    {
        if (SelectedProcess is null) return;
        var target = SelectedProcess;

        if (target.IsProtected)
        {
            Status = $"'{target.Name}' is a protected system process and cannot be terminated.";
            return;
        }

        var confirm = MessageBox.Show(
            $"End process '{target.Name}' (PID {target.Pid})?\n\nUnsaved work in that program will be lost.",
            "WinSentinel — Confirm End Task",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        var r = _processes.Kill(target.Pid);
        Status = r.Message;
        RefreshProcesses();
    }

    /// <summary>Called by the dashboard code-behind after the user picks a priority class.</summary>
    public void ApplyPriority(ProcessPriorityClass priorityClass)
    {
        if (SelectedProcess is null) return;
        var target = SelectedProcess;

        if (priorityClass == ProcessPriorityClass.RealTime)
        {
            var confirm = MessageBox.Show(
                $"Set '{target.Name}' to REAL-TIME priority?\n\nReal-time can starve the rest of the system, including input and the OS. Use only briefly.",
                "WinSentinel — Confirm Real-time Priority",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var r = _processes.SetPriority(target.Pid, priorityClass);
        Status = r.Message;
        RefreshProcesses();
    }

    public long GetSelectedAffinity()
        => SelectedProcess is null ? 0 : _processes.GetAffinity(SelectedProcess.Pid);

    public void ApplyAffinity(long mask)
    {
        if (SelectedProcess is null) return;
        var r = _processes.SetAffinity(SelectedProcess.Pid, mask);
        Status = r.Message;
    }

    // ---- Startup -----------------------------------------------------------

    private void RefreshStartup()
    {
        StartupItems.Clear();
        foreach (var i in _startup.GetStartupItems()) StartupItems.Add(i);
    }

    private void RemoveStartup()
    {
        if (SelectedStartup is null) return;
        if (!SelectedStartup.Removable)
        {
            Status = $"'{SelectedStartup.Name}' is a machine-wide (HKLM) entry; WinSentinel only edits per-user (HKCU) entries.";
            return;
        }

        var confirm = MessageBox.Show(
            $"Remove startup entry '{SelectedStartup.Name}'?\n\nThis deletes the value from HKCU\\…\\Run. The program itself is not uninstalled; it simply won't auto-start.",
            "WinSentinel — Confirm Remove",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        var r = _startup.Remove(SelectedStartup.Name);
        Status = r.Message;
        RefreshStartup();
    }

    public void AddStartup(string name, string command)
    {
        var r = _startup.Add(name, command);
        Status = r.Message;
        RefreshStartup();
    }
}
