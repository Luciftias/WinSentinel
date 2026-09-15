using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using WinSentinel.Abstractions;
using WinSentinel.Helpers;
using WinSentinel.Models;
using WinSentinel.Services;

namespace WinSentinel.ViewModels;

/// <summary>
/// View model for the dashboard: live gauges (CPU/RAM/GPU/disk/network/battery), the
/// auto-refreshing process table with per-process actions, the startup manager and the
/// settings surface. All destructive operations route through services that enforce the
/// protected-process guard rail; the UI additionally asks for confirmation.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly SystemMonitorService _monitor;
    private readonly ProcessService _processes;
    private readonly MemoryOptimizer _memory;
    private readonly StartupManager _startup;
    private readonly NetworkService _network;
    private readonly PluginHost _plugins;
    private readonly SettingsService _settings;
    private readonly ThemeManager _theme;
    private readonly AlertService _alerts;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;

    private bool _refreshBusy;
    private bool _connectionsBusy;
    private bool _disposed;

    private const int NetworkTabIndex = 2;
    private const int SettingsTabIndex = 5;

    public DashboardViewModel(
        SystemMonitorService monitor,
        ProcessService processes,
        MemoryOptimizer memory,
        StartupManager startup,
        NetworkService network,
        PluginHost plugins,
        SettingsService settings,
        ThemeManager theme,
        AlertService alerts)
    {
        _monitor = monitor;
        _processes = processes;
        _memory = memory;
        _startup = startup;
        _network = network;
        _plugins = plugins;
        _settings = settings;
        _theme = theme;
        _alerts = alerts;
        _dispatcher = Dispatcher.CurrentDispatcher;

        HistoryLength = Math.Clamp(settings.Current.HistorySeconds, 30, 300);

        // Filtered, sorted view over the process rows --------------------------------
        ProcessesView = CollectionViewSource.GetDefaultView(Processes);
        ProcessesView.Filter = o => o is ProcessRow row && row.Matches(_searchText);
        ProcessesView.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.MemoryMB), ListSortDirection.Descending));

        // Filtered view over the network connections --------------------------------
        ConnectionView = CollectionViewSource.GetDefaultView(Connections);
        ConnectionView.Filter = o => o is ConnectionInfo connection && connection.Matches(_connectionSearch);

        // Alert history ---------------------------------------------------------------
        _alerts.AlertRaised += OnAlertRaised;
        foreach (var alert in _alerts.History) AlertsHistory.Add(alert);

        // Plugins -----------------------------------------------------------------------
        _plugins.ValuesUpdated += OnPluginValues;
        _plugins.PluginsChanged += OnPluginsChanged;

        // Seed histories so the sparklines draw a full-width baseline right away.
        for (int i = 0; i < HistoryLength; i++)
        {
            CpuHistory.Add(0);
            RamHistory.Add(0);
            GpuHistory.Add(0);
            DiskHistory.Add(0);
            NetDownHistory.Add(0);
            NetUpHistory.Add(0);
        }

        _monitor.SampleUpdated += OnSample;

        // Commands -------------------------------------------------------------------
        RefreshProcessesCommand = new RelayCommand(_ => _ = RefreshProcessesAsync());
        TrimAllCommand = new RelayCommand(_ => _ = TrimAllAsync());
        PurgeStandbyCommand = new RelayCommand(_ => _ = PurgeStandbyAsync());
        TrimSelectedCommand = new RelayCommand(_ => TrimSelected(), _ => SelectedProcess is not null);
        KillSelectedCommand = new RelayCommand(_ => KillSelected(entireTree: false), _ => SelectedProcess is not null);
        KillTreeSelectedCommand = new RelayCommand(_ => KillSelected(entireTree: true), _ => SelectedProcess is not null);
        SuspendResumeSelectedCommand = new RelayCommand(_ => SuspendResumeSelected(), _ => SelectedProcess is not null);
        ToggleEcoSelectedCommand = new RelayCommand(_ => ToggleEcoSelected(), _ => SelectedProcess is not null);
        ApplyPriorityCommand = new RelayCommand(_ => ApplySelectedPriority(), _ => SelectedProcess is not null);
        ApplyIoPriorityCommand = new RelayCommand(_ => ApplySelectedIoPriority(), _ => SelectedProcess is not null);
        ApplyMemoryPriorityCommand = new RelayCommand(_ => ApplySelectedMemoryPriority(), _ => SelectedProcess is not null);
        OpenFileLocationCommand = new RelayCommand(_ => OpenFileLocation(), _ => SelectedProcess?.Path is not null);
        CopyDetailsCommand = new RelayCommand(_ => CopyDetails(), _ => SelectedProcess is not null);
        RefreshStartupCommand = new RelayCommand(_ => RefreshStartup());
        RemoveStartupCommand = new RelayCommand(_ => RemoveStartup(), _ => SelectedStartup?.CanRemove == true);
        ToggleStartupCommand = new RelayCommand(_ => ToggleStartup(), _ => SelectedStartup?.CanToggle == true);
        ResetBalloonPositionCommand = new RelayCommand(_ => ResetBalloonPosition());
        TestAlertCommand = new RelayCommand(_ => _alerts.RaiseTest());
        ResetSettingsCommand = new RelayCommand(_ => ResetSettings());
        RefreshConnectionsCommand = new RelayCommand(_ => _ = RefreshConnectionsAsync());
        ClearAlertsCommand = new RelayCommand(_ => ClearAlerts());
        ReloadPluginsCommand = new RelayCommand(_ => ReloadPlugins());
        OpenPluginsFolderCommand = new RelayCommand(_ => OpenPluginsFolder());

        // Process auto-refresh timer -------------------------------------------------
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SelectedRefreshSeconds) };
        _refreshTimer.Tick += (_, _) =>
        {
            if (!AutoRefresh || Paused) return;
            _ = RefreshProcessesAsync();
            UpdateAdapters();
            if (SelectedTabIndex == NetworkTabIndex) _ = RefreshConnectionsAsync();
        };
        _refreshTimer.Start();

        if (_monitor.Latest is not null) Apply(_monitor.Latest);
        _ = RefreshProcessesAsync();
        RefreshStartup();
        UpdateAdapters();
        RebuildPluginRows();
        UpdatePluginMetrics();
        SetStatus("Monitoring system…");
    }

    // ═══════════════════════════════════════════════════════════════════ Header

    public string MachineName => Environment.MachineName;

    public string VersionText => $"WinSentinel {typeof(DashboardViewModel).Assembly.GetName().Version?.ToString(3) ?? "2.0.0"}";

    private string _uptime = "—";
    public string Uptime { get => _uptime; private set => SetField(ref _uptime, value); }

    private int _processCount;
    public int ProcessCount { get => _processCount; private set => SetField(ref _processCount, value); }

    private int _threadCount;
    public int ThreadCount { get => _threadCount; private set => SetField(ref _threadCount, value); }

    private string _status = string.Empty;
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private int _selectedTabIndex;
    public int SelectedTabIndex { get => _selectedTabIndex; set => SetField(ref _selectedTabIndex, value); }

    /// <summary>Navigates the dashboard to the Settings page (used by tray "Settings…").</summary>
    public void ShowSettings() => SelectedTabIndex = SettingsTabIndex;

    // ═══════════════════════════════════════════════════════════════════ Live metrics

    public int HistoryLength { get; }
    public int LogicalProcessors { get; } = Environment.ProcessorCount;

    public ObservableCollection<double> CpuHistory { get; } = new();
    public ObservableCollection<double> RamHistory { get; } = new();
    public ObservableCollection<double> GpuHistory { get; } = new();
    public ObservableCollection<double> DiskHistory { get; } = new();
    public ObservableCollection<double> NetDownHistory { get; } = new();
    public ObservableCollection<double> NetUpHistory { get; } = new();

    private double _cpu;
    public double Cpu { get => _cpu; private set { if (SetField(ref _cpu, value)) OnPropertyChanged(nameof(CpuText)); } }
    public string CpuText => $"{Cpu:0}%";

    private double _ram;
    public double Ram { get => _ram; private set { if (SetField(ref _ram, value)) OnPropertyChanged(nameof(RamText)); } }
    public string RamText => $"{Ram:0}%";

    private double _gpu;
    public double Gpu { get => _gpu; private set { if (SetField(ref _gpu, value)) OnPropertyChanged(nameof(GpuText)); } }
    public string GpuText => $"{Gpu:0}%";

    private bool _gpuAvailable;
    public bool GpuAvailable { get => _gpuAvailable; private set => SetField(ref _gpuAvailable, value); }

    private bool _diskAvailable;
    public bool DiskAvailable { get => _diskAvailable; private set => SetField(ref _diskAvailable, value); }

    private string _cpuDetail = "—";
    public string CpuDetail { get => _cpuDetail; private set => SetField(ref _cpuDetail, value); }

    private string _ramDetail = "—";
    public string RamDetail { get => _ramDetail; private set => SetField(ref _ramDetail, value); }

    private string _diskDetail = "—";
    public string DiskDetail { get => _diskDetail; private set => SetField(ref _diskDetail, value); }

    private string _netDetail = "—";
    public string NetDetail { get => _netDetail; private set => SetField(ref _netDetail, value); }

    private bool _batteryPresent;
    public bool BatteryPresent { get => _batteryPresent; private set => SetField(ref _batteryPresent, value); }

    private string _batteryDetail = string.Empty;
    public string BatteryDetail { get => _batteryDetail; private set => SetField(ref _batteryDetail, value); }

    private bool _tempAvailable;
    public bool TempAvailable { get => _tempAvailable; private set => SetField(ref _tempAvailable, value); }

    private string _tempDetail = "—";
    public string TempDetail { get => _tempDetail; private set => SetField(ref _tempDetail, value); }

    private double _diskMax = 10;
    public double DiskMax { get => _diskMax; private set => SetField(ref _diskMax, value); }

    private double _netMax = 5;
    public double NetMax { get => _netMax; private set => SetField(ref _netMax, value); }

    // ═══════════════════════════════════════════════════════════════════ Processes

    public ObservableCollection<ProcessRow> Processes { get; } = new();
    public ICollectionView ProcessesView { get; }

    // ── Network page ──────────────────────────────────────────────────────
    public ObservableCollection<AdapterInfo> Adapters { get; } = new();
    public ObservableCollection<ConnectionInfo> Connections { get; } = new();
    public ICollectionView ConnectionView { get; }

    private string _connectionSearch = string.Empty;
    public string ConnectionSearch
    {
        get => _connectionSearch;
        set { if (SetField(ref _connectionSearch, value)) ConnectionView.Refresh(); }
    }

    private int _connectionCount;
    public int ConnectionCount { get => _connectionCount; private set => SetField(ref _connectionCount, value); }

    /// <summary>True when per-process TCP statistics could be enabled (needs elevation).</summary>
    public bool ProcessTcpStatsAvailable => _network.ProcessTcpStatsAvailable;

        // ── Alerts page ───────────────────────────────────────────────────────
    public ObservableCollection<Alert> AlertsHistory { get; } = new();

    // ── Plugins ───────────────────────────────────────────────────────────
    public ObservableCollection<PluginHost.PluginMetricValue> PluginMetrics { get; } = new();

    public ObservableCollection<PluginRow> Plugins { get; } = new();

    public bool HasPluginMetrics => PluginMetrics.Count > 0;

    public bool HasPlugins => Plugins.Count > 0;

    public string PluginsDirectory => _plugins.PluginsDirectory;

    public string PluginSummary
    {
        get
        {
            if (Plugins.Count == 0) return "No plugins loaded.";
            int loaded = Plugins.Count(p => !p.IsError);
            return $"{loaded}/{Plugins.Count} plugins loaded • {PluginMetrics.Count} metrics";
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
                ProcessesView.Refresh();
        }
    }

    private ProcessRow? _selectedProcess;
    public ProcessRow? SelectedProcess { get => _selectedProcess; set => SetField(ref _selectedProcess, value); }

    public int[] RefreshSecondsOptions { get; } = { 1, 2, 5, 10 };

    public int SelectedRefreshSeconds
    {
        get => Math.Max(1, _settings.Current.ProcessRefreshMs / 1000);
        set
        {
            int ms = Math.Clamp(value, 1, 30) * 1000;
            if (ms == _settings.Current.ProcessRefreshMs) return;
            _settings.Update(s => s.ProcessRefreshMs = ms);
            _refreshTimer.Interval = TimeSpan.FromSeconds(value);
            OnPropertyChanged();
        }
    }

    public bool AutoRefresh
    {
        get => _settings.Current.AutoRefreshProcesses;
        set
        {
            if (value == _settings.Current.AutoRefreshProcesses) return;
            _settings.Update(s => s.AutoRefreshProcesses = value);
            OnPropertyChanged();
        }
    }

    private bool _paused;
    public bool Paused { get => _paused; set => SetField(ref _paused, value); }

    public static string[] PriorityOptions { get; } =
        { "Idle", "BelowNormal", "Normal", "AboveNormal", "High", "RealTime" };

    public static string[] IoPriorityOptions { get; } = { "Very low", "Low", "Normal" };

    public static string[] MemoryPriorityOptions { get; } = { "Very low", "Low", "Medium", "Below normal", "Normal" };

    private string _selectedPriority = "Normal";
    public string SelectedPriority { get => _selectedPriority; set => SetField(ref _selectedPriority, value); }

    private string _selectedIoPriority = "Normal";
    public string SelectedIoPriority { get => _selectedIoPriority; set => SetField(ref _selectedIoPriority, value); }

    private string _selectedMemoryPriority = "Normal";
    public string SelectedMemoryPriority { get => _selectedMemoryPriority; set => SetField(ref _selectedMemoryPriority, value); }

    // ═══════════════════════════════════════════════════════════════════ Startup

    public ObservableCollection<StartupItem> StartupItems { get; } = new();

    private StartupItem? _selectedStartup;
    public StartupItem? SelectedStartup { get => _selectedStartup; set => SetField(ref _selectedStartup, value); }

    // ═══════════════════════════════════════════════════════════════════ Settings surface

    public static string[] ThemeOptions => ThemeManager.ThemeOptions;
    public static string[] AccentOptions => ThemeManager.AccentOptions;
    public static int[] SampleIntervalOptions { get; } = { 500, 1000, 2000 };

    public string ThemeChoice
    {
        get => _settings.Current.Theme;
        set
        {
            if (value == _settings.Current.Theme) return;
            _settings.Update(s => s.Theme = value);
            OnPropertyChanged();
        }
    }

    public string AccentChoice
    {
        get => _settings.Current.Accent;
        set
        {
            if (value == _settings.Current.Accent) return;
            _settings.Update(s => s.Accent = value);
            OnPropertyChanged();
        }
    }

    public int SelectedSampleIntervalMs
    {
        get => _settings.Current.SampleIntervalMs;
        set
        {
            int v = Math.Clamp(value, 500, 2000);
            if (v == _settings.Current.SampleIntervalMs) return;
            _settings.Update(s => s.SampleIntervalMs = v);
            OnPropertyChanged();
        }
    }

    public bool ConfirmKill
    {
        get => _settings.Current.ConfirmKill;
        set
        {
            if (value == _settings.Current.ConfirmKill) return;
            _settings.Update(s => s.ConfirmKill = value);
            OnPropertyChanged();
        }
    }

    public bool ConfirmStartup
    {
        get => _settings.Current.ConfirmStartup;
        set
        {
            if (value == _settings.Current.ConfirmStartup) return;
            _settings.Update(s => s.ConfirmStartup = value);
            OnPropertyChanged();
        }
    }

    public bool BalloonVisible
    {
        get => _settings.Current.BalloonVisible;
        set
        {
            if (value == _settings.Current.BalloonVisible) return;
            _settings.Update(s => s.BalloonVisible = value);
            OnPropertyChanged();
        }
    }

    public double BalloonOpacity
    {
        get => _settings.Current.BalloonOpacity;
        set
        {
            double v = Math.Round(Math.Clamp(value, 0.3, 1.0), 2);
            if (Math.Abs(v - _settings.Current.BalloonOpacity) < 0.001) return;
            _settings.Update(s => s.BalloonOpacity = v);
            OnPropertyChanged();
        }
    }

    public bool BalloonShowGpu
    {
        get => _settings.Current.BalloonShowGpu;
        set
        {
            if (value == _settings.Current.BalloonShowGpu) return;
            _settings.Update(s => s.BalloonShowGpu = value);
            OnPropertyChanged();
        }
    }

    public bool BalloonShowDisk
    {
        get => _settings.Current.BalloonShowDisk;
        set
        {
            if (value == _settings.Current.BalloonShowDisk) return;
            _settings.Update(s => s.BalloonShowDisk = value);
            OnPropertyChanged();
        }
    }

    public bool BalloonShowNet
    {
        get => _settings.Current.BalloonShowNet;
        set
        {
            if (value == _settings.Current.BalloonShowNet) return;
            _settings.Update(s => s.BalloonShowNet = value);
            OnPropertyChanged();
        }
    }

    public bool AlertsEnabled
    {
        get => _settings.Current.AlertsEnabled;
        set
        {
            if (value == _settings.Current.AlertsEnabled) return;
            _settings.Update(s => s.AlertsEnabled = value);
            OnPropertyChanged();
        }
    }

    public double AlertCpuPercent
    {
        get => _settings.Current.AlertCpuPercent;
        set
        {
            int v = (int)Math.Round(value);
            if (v == _settings.Current.AlertCpuPercent) return;
            _settings.Update(s => s.AlertCpuPercent = v);
            OnPropertyChanged();
        }
    }

    public double AlertRamPercent
    {
        get => _settings.Current.AlertRamPercent;
        set
        {
            int v = (int)Math.Round(value);
            if (v == _settings.Current.AlertRamPercent) return;
            _settings.Update(s => s.AlertRamPercent = v);
            OnPropertyChanged();
        }
    }

    public double AlertSustainSeconds
    {
        get => _settings.Current.AlertSustainSeconds;
        set
        {
            int v = (int)Math.Round(value);
            if (v == _settings.Current.AlertSustainSeconds) return;
            _settings.Update(s => s.AlertSustainSeconds = v);
            OnPropertyChanged();
        }
    }

    public double AlertCooldownMinutes
    {
        get => _settings.Current.AlertCooldownMinutes;
        set
        {
            int v = (int)Math.Round(value);
            if (v == _settings.Current.AlertCooldownMinutes) return;
            _settings.Update(s => s.AlertCooldownMinutes = v);
            OnPropertyChanged();
        }
    }

    public double AlertTempCelsius
    {
        get => _settings.Current.AlertTempCelsius;
        set
        {
            int v = (int)Math.Round(value);
            if (v == _settings.Current.AlertTempCelsius) return;
            _settings.Update(s => s.AlertTempCelsius = v);
            OnPropertyChanged();
        }
    }

    public bool SpikeAlertsEnabled
    {
        get => _settings.Current.SpikeAlertsEnabled;
        set
        {
            if (value == _settings.Current.SpikeAlertsEnabled) return;
            _settings.Update(s => s.SpikeAlertsEnabled = value);
            OnPropertyChanged();
        }
    }

    public double SpikeSensitivity
    {
        get => _settings.Current.SpikeSensitivity;
        set
        {
            double v = Math.Round(Math.Clamp(value, 1.5, 6.0), 1);
            if (Math.Abs(v - _settings.Current.SpikeSensitivity) < 0.05) return;
            _settings.Update(s => s.SpikeSensitivity = v);
            OnPropertyChanged();
        }
    }

    public bool RunawayAlertsEnabled
    {
        get => _settings.Current.RunawayAlertsEnabled;
        set
        {
            if (value == _settings.Current.RunawayAlertsEnabled) return;
            _settings.Update(s => s.RunawayAlertsEnabled = value);
            OnPropertyChanged();
        }
    }

    public double RunawayCpuPercent
    {
        get => _settings.Current.RunawayCpuPercent;
        set
        {
            int v = (int)Math.Round(value);
            if (v == _settings.Current.RunawayCpuPercent) return;
            _settings.Update(s => s.RunawayCpuPercent = v);
            OnPropertyChanged();
        }
    }

    public double RunawaySustainSeconds
    {
        get => _settings.Current.RunawaySustainSeconds;
        set
        {
            int v = (int)Math.Round(value);
            if (v == _settings.Current.RunawaySustainSeconds) return;
            _settings.Update(s => s.RunawaySustainSeconds = v);
            OnPropertyChanged();
        }
    }

    public string SettingsPath => _settings.SettingsPath;

    // ═══════════════════════════════════════════════════════════════════ Commands

    public RelayCommand RefreshProcessesCommand { get; }
    public RelayCommand TrimAllCommand { get; }
    public RelayCommand PurgeStandbyCommand { get; }
    public RelayCommand TrimSelectedCommand { get; }
    public RelayCommand KillSelectedCommand { get; }
    public RelayCommand KillTreeSelectedCommand { get; }
    public RelayCommand SuspendResumeSelectedCommand { get; }
    public RelayCommand ToggleEcoSelectedCommand { get; }
    public RelayCommand ApplyPriorityCommand { get; }
    public RelayCommand ApplyIoPriorityCommand { get; }
    public RelayCommand ApplyMemoryPriorityCommand { get; }
    public RelayCommand OpenFileLocationCommand { get; }
    public RelayCommand CopyDetailsCommand { get; }
    public RelayCommand RefreshStartupCommand { get; }
    public RelayCommand RemoveStartupCommand { get; }
    public RelayCommand ToggleStartupCommand { get; }
    public RelayCommand ResetBalloonPositionCommand { get; }
    public RelayCommand TestAlertCommand { get; }
    public RelayCommand ResetSettingsCommand { get; }
    public RelayCommand RefreshConnectionsCommand { get; }
    public RelayCommand ClearAlertsCommand { get; }
    public RelayCommand ReloadPluginsCommand { get; }
    public RelayCommand OpenPluginsFolderCommand { get; }

    // ═══════════════════════════════════════════════════════════════════ Sampling

    private void OnSample(object? sender, MetricSample s)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => Apply(s));
            return;
        }
        Apply(s);
    }

    private void Apply(MetricSample s)
    {
        Cpu = s.CpuPercent;
        Ram = s.RamPercent;
        Gpu = s.GpuPercent;
        GpuAvailable = s.GpuAvailable;
        DiskAvailable = s.DiskAvailable;

        CpuDetail = s.CpuMhz > 0
            ? $"{s.CpuMhz:0} MHz average • {LogicalProcessors} logical processors"
            : $"{LogicalProcessors} logical processors";

        RamDetail = $"{s.RamUsedMB / 1024.0:0.0} GB of {s.RamTotalMB / 1024.0:0.0} GB  •  {s.RamAvailableMB / 1024.0:0.0} GB free";

        if (s.DiskAvailable)
            DiskDetail = $"Read {ProcessRow.FormatRate(s.DiskReadBps)}  •  Write {ProcessRow.FormatRate(s.DiskWriteBps)}";

        NetDetail = $"Down {ProcessRow.FormatRate(s.NetDownBps)}  •  Up {ProcessRow.FormatRate(s.NetUpBps)}";

        BatteryPresent = s.BatteryPresent;
        if (s.BatteryPresent)
        {
            string state = s.OnAcPower ? "AC power" : "On battery";
            BatteryDetail = s.BatteryPercent >= 0 ? $"{s.BatteryPercent}%  •  {state}" : state;
        }

        TempAvailable = s.TempAvailable;
        if (s.TempAvailable)
            TempDetail = $"{s.TempCelsius:0.0} °C  •  {s.TempZone}";

        Uptime = FormatUptime();

        Push(CpuHistory, s.CpuPercent, 100);
        Push(RamHistory, s.RamPercent, 100);
        Push(GpuHistory, s.GpuAvailable ? s.GpuPercent : 0, 100);

        const double MB = 1024.0 * 1024.0;
        double diskMBs = (s.DiskReadBps + s.DiskWriteBps) / MB;
        double downMBs = s.NetDownBps / MB;
        double upMBs = s.NetUpBps / MB;
        Push(DiskHistory, diskMBs, ref _diskMax, 10);
        Push(NetDownHistory, downMBs, ref _netMax, 5);
        Push(NetUpHistory, upMBs, ref _netMax, 5, updateMax: false);
    }

    private void Push(ObservableCollection<double> series, double value, double ceiling)
    {
        series.Add(Math.Clamp(value, 0, ceiling));
        while (series.Count > HistoryLength) series.RemoveAt(0);
    }

    /// <summary>Pushes a rate value and auto-scales the sparkline ceiling to keep it visible.</summary>
    private void Push(ObservableCollection<double> series, double value, ref double maxField, double minimum, bool updateMax = true)
    {
        series.Add(Math.Max(0, value));
        while (series.Count > HistoryLength) series.RemoveAt(0);

        if (!updateMax) return;
        double peak = minimum;
        foreach (var v in series) if (v > peak) peak = v;
        double scaled = Math.Max(minimum, peak * 1.2);
        if (Math.Abs(scaled - maxField) > 0.01)
        {
            maxField = scaled;
            OnPropertyChanged(series == DiskHistory ? nameof(DiskMax) : nameof(NetMax));
        }
    }

    private static string FormatUptime()
    {
        var ts = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }

    private void SetStatus(string message)
        => Status = $"{DateTime.Now:HH:mm:ss}   {message}";

    // ═══════════════════════════════════════════════════════════════════ Process refresh

    private async Task RefreshProcessesAsync()
    {
        if (_refreshBusy || _disposed) return;
        _refreshBusy = true;
        try
        {
            var snapshot = await Task.Run(() => _processes.Snapshot());
            if (_disposed) return;
            MergeSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            Logger.Error("Process refresh", ex);
        }
        finally
        {
            _refreshBusy = false;
        }
    }

    /// <summary>
    /// Merges a snapshot into the existing rows (diff by PID) so selection, sorting and scroll
    /// position survive refreshes; dead PIDs are removed, new PIDs appended.
    /// </summary>
    private void MergeSnapshot(List<ProcessInfo> snapshot)
    {
        var byPid = new Dictionary<int, ProcessInfo>(snapshot.Count);
        foreach (var info in snapshot) byPid[info.Pid] = info;

        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            if (!byPid.ContainsKey(Processes[i].Pid)) Processes.RemoveAt(i);
        }

        var rows = new Dictionary<int, ProcessRow>(Processes.Count);
        foreach (var row in Processes) rows[row.Pid] = row;

        int threads = 0;
        foreach (var info in snapshot)
        {
            threads += info.Threads;
            if (rows.TryGetValue(info.Pid, out var row))
            {
                row.Apply(info);
            }
            else
            {
                row = new ProcessRow(info);
                Processes.Add(row);
                rows[info.Pid] = row;
            }
            row.IsSuspended = _processes.IsSuspended(info.Pid);
        }

        ProcessCount = snapshot.Count;
        ThreadCount = threads;

        // Feed the runaway-process detector with the current top CPU consumer.
        ProcessInfo? top = null;
        foreach (var info in snapshot)
        {
            if (info.CpuPercent is not > 0) continue;
            if (top is null || (info.CpuPercent ?? 0) > (top.CpuPercent ?? 0)) top = info;
        }

        if (top is not null)
            _alerts.ReportTopProcess(top.Pid, top.Name, top.CpuPercent ?? 0, _settings.Current.ProcessRefreshMs / 1000.0);
        else
            _alerts.ReportTopProcess(0, string.Empty, 0, 0);
    }

    // ═══════════════════════════════════════════════════════════════════ Process actions

    private void TrimSelected()
    {
        if (SelectedProcess is not { } target) return;
        bool ok = _memory.TrimProcess(target.Pid);
        SetStatus(ok
            ? $"Trimmed working set of {target.Name}."
            : $"Could not trim {target.Name} (protected or access denied).");
        _ = RefreshProcessesAsync();
    }

    private async Task TrimAllAsync()
    {
        SetStatus("Trimming working sets…");
        var r = await Task.Run(() => _memory.TrimAll());
        SetStatus($"Trimmed {r.Trimmed} processes • ~{r.FreedMB:0} MB reclaimed • {r.Skipped} skipped.");
        await RefreshProcessesAsync();
    }

    private async Task PurgeStandbyAsync()
    {
        var confirm = MessageBox.Show(
            "Purge the system standby (cached) memory list?\n\n" +
            "This is an advanced action. Windows immediately rebuilds the cache from disk afterwards, " +
            "so expect a short I/O spike rather than a lasting win. Use it only when tools report high cached memory.",
            "WinSentinel — Advanced memory action",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        SetStatus("Purging standby list…");
        var r = await Task.Run(() => _memory.PurgeStandby());
        SetStatus(r.Message);
    }

    private void KillSelected(bool entireTree)
    {
        if (SelectedProcess is not { } target) return;

        if (target.IsProtected)
        {
            SetStatus($"'{target.Name}' is a protected system process and cannot be terminated.");
            return;
        }

        if (_settings.Current.ConfirmKill)
        {
            string body = entireTree
                ? $"End process tree for '{target.Name}' (PID {target.Pid})?\n\nThis terminates the process and all of its child processes."
                : $"End process '{target.Name}' (PID {target.Pid})?\n\nUnsaved work in that program will be lost.";
            var confirm = MessageBox.Show(body, "WinSentinel — Confirm End Task",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var r = _processes.Kill(target.Pid, entireTree);
        SetStatus(r.Message);
        _ = RefreshProcessesAsync();
    }

    private void SuspendResumeSelected()
    {
        if (SelectedProcess is not { } target) return;

        if (target.IsProtected)
        {
            SetStatus($"'{target.Name}' is protected; suspend/resume blocked.");
            return;
        }

        var r = target.IsSuspended ? _processes.Resume(target.Pid) : _processes.Suspend(target.Pid);
        SetStatus(r.Message);
        target.IsSuspended = _processes.IsSuspended(target.Pid);
        _ = RefreshProcessesAsync();
    }

    private void ToggleEcoSelected()
    {
        if (SelectedProcess is not { } target) return;

        bool enable = target.EcoMode != true;
        var r = _processes.SetEfficiencyMode(target.Pid, enable);
        SetStatus(r.Message);
        _ = RefreshProcessesAsync();
    }

    private void ApplySelectedPriority()
    {
        if (SelectedProcess is not { } target) return;
        if (!Enum.TryParse<ProcessPriorityClass>(SelectedPriority, out var priority))
            return;

        if (priority == ProcessPriorityClass.RealTime)
        {
            var confirm = MessageBox.Show(
                $"Set '{target.Name}' to REAL-TIME priority?\n\n" +
                "Real-time can starve the rest of the system, including input and the OS. Use only briefly.",
                "WinSentinel — Confirm Real-time Priority",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var r = _processes.SetPriority(target.Pid, priority);
        SetStatus(r.Message);
        _ = RefreshProcessesAsync();
    }

    private void ApplySelectedIoPriority()
    {
        if (SelectedProcess is not { } target) return;
        int hint = SelectedIoPriority switch { "Very low" => 0, "Low" => 1, _ => 2 };
        var r = _processes.SetIoPriority(target.Pid, hint);
        SetStatus(r.Message);
    }

    private void ApplySelectedMemoryPriority()
    {
        if (SelectedProcess is not { } target) return;
        uint level = SelectedMemoryPriority switch
        {
            "Very low" => 1,
            "Low" => 2,
            "Medium" => 3,
            "Below normal" => 4,
            _ => 5
        };
        var r = _processes.SetMemoryPriority(target.Pid, level);
        SetStatus(r.Message);
    }

    private void OpenFileLocation()
    {
        if (SelectedProcess?.Path is not { } path) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open file location: {ex.Message}");
        }
    }

    private void CopyDetails()
    {
        if (SelectedProcess is not { } target) return;
        string text =
            $"Name: {target.Name}\nPID: {target.Pid}\n" +
            $"CPU: {target.CpuDisplay}\nMemory: {target.MemoryDisplay}\nDisk: {target.DiskDisplay}\nGPU: {target.GpuDisplay}\n" +
            $"Net (TCP): ↓ {ByteFormat.Rate(target.NetDownBps ?? 0)}  ↑ {ByteFormat.Rate(target.NetUpBps ?? 0)}\n" +
            $"Threads: {target.Threads}\nPriority: {target.Priority}\n" +
            $"Efficiency mode: {(target.EcoMode == true ? "on" : "off")}\nProtected: {target.IsProtected}\n" +
            (target.Company is null ? string.Empty : $"Publisher: {target.Company}\n") +
            (target.Path is null ? string.Empty : $"Path: {target.Path}\n");
        try
        {
            Clipboard.SetText(text);
            SetStatus($"Copied details for {target.Name} to the clipboard.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not copy details: {ex.Message}");
        }
    }

    /// <summary>Current processor-affinity mask of the selected process (for the dialog).</summary>
    public long GetSelectedAffinity()
        => SelectedProcess is null ? 0 : _processes.GetAffinity(SelectedProcess.Pid);

    /// <summary>Applies the affinity mask chosen in the dialog.</summary>
    public void ApplyAffinity(long mask)
    {
        if (SelectedProcess is not { } target) return;
        var r = _processes.SetAffinity(target.Pid, mask);
        SetStatus(r.Message);
        _ = RefreshProcessesAsync();
    }

    // ═══════════════════════════════════════════════════════════════════ Startup

    private void RefreshStartup()
    {
        try
        {
            var keep = SelectedStartup;
            StartupItems.Clear();
            foreach (var item in _startup.GetStartupItems()) StartupItems.Add(item);
            if (keep is not null)
                SelectedStartup = StartupItems.FirstOrDefault(i => i.Name == keep.Name && i.Location == keep.Location);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not read startup entries: {ex.Message}");
        }
    }

    private void ToggleStartup()
    {
        if (SelectedStartup is not { } item) return;
        bool enable = item.Enabled != true;
        var r = _startup.SetEnabled(item, enable);
        SetStatus(r.Message);
        RefreshStartup();
    }

    private void RemoveStartup()
    {
        if (SelectedStartup is not { } item) return;

        if (_settings.Current.ConfirmStartup)
        {
            var confirm = MessageBox.Show(
                $"Remove startup entry '{item.Name}'?\n\n" +
                $"This deletes the value from {item.Location}\\…\\{item.Source}. " +
                "The program itself is not uninstalled; it simply won't auto-start.",
                "WinSentinel — Confirm Remove",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var r = _startup.Remove(item);
        SetStatus(r.Message);
        RefreshStartup();
    }

    public void AddStartup(string name, string command)
    {
        var r = _startup.Add(name, command);
        SetStatus(r.Message);
        RefreshStartup();
    }

    // ═══════════════════════════════════════════════════════════════════ Settings actions

    private void ResetBalloonPosition()
    {
        _settings.Update(s => { s.BalloonX = 48; s.BalloonY = 96; });
        SetStatus("Floating balloon position reset. Drag it anywhere to move it.");
    }

    private void ResetSettings()
    {
        var confirm = MessageBox.Show(
            "Reset all WinSentinel settings to defaults?\n\nThe app keeps running with default behaviour immediately.",
            "WinSentinel — Reset Settings",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        _settings.ResetToDefaults();
        OnPropertyChanged(string.Empty); // re-read every bound property
        SetStatus("Settings were reset to defaults.");
    }

    // ═══════════════════════════════════════════════════════════════════ Network page

    private void UpdateAdapters()
    {
        try
        {
            var latest = _network.LastAdapters;
            Adapters.Clear();
            foreach (var adapter in latest) Adapters.Add(adapter);
        }
        catch (Exception ex)
        {
            Logger.Error("Adapter update", ex);
        }
    }

    private async Task RefreshConnectionsAsync()
    {
        if (_connectionsBusy || _disposed) return;
        _connectionsBusy = true;
        try
        {
            var names = new Dictionary<int, string>(Processes.Count);
            foreach (var row in Processes) names[row.Pid] = row.Name;

            var list = await Task.Run(() => _network.GetConnections(names));
            if (_disposed) return;

            Connections.Clear();
            foreach (var connection in list) Connections.Add(connection);
            ConnectionCount = list.Count;
            OnPropertyChanged(nameof(ProcessTcpStatsAvailable));
        }
        catch (Exception ex)
        {
            Logger.Error("Connection refresh", ex);
        }
        finally
        {
            _connectionsBusy = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════ Alerts page

    private void OnAlertRaised(object? sender, Alert alert)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => AddAlert(alert));
            return;
        }
        AddAlert(alert);
    }

    private void AddAlert(Alert alert)
    {
        AlertsHistory.Insert(0, alert);
        while (AlertsHistory.Count > 100) AlertsHistory.RemoveAt(AlertsHistory.Count - 1);
    }

    private void ClearAlerts()
    {
        _alerts.ClearHistory();
        AlertsHistory.Clear();
        SetStatus("Alert history cleared.");
    }

    // ═══════════════════════════════════════════════════════════════════ Plugins

    private void OnPluginValues(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(UpdatePluginMetrics);
            return;
        }
        UpdatePluginMetrics();
    }

    private void OnPluginsChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => { RebuildPluginRows(); UpdatePluginMetrics(); });
            return;
        }
        RebuildPluginRows();
        UpdatePluginMetrics();
    }

    private void RebuildPluginRows()
    {
        try
        {
            Plugins.Clear();
            foreach (var plugin in _plugins.Plugins)
            {
                var row = new PluginRow
                {
                    Name = string.IsNullOrWhiteSpace(plugin.Name) ? plugin.Id : plugin.Name,
                    Version = plugin.Version,
                    Author = plugin.Author,
                    Description = plugin.Description,
                    Status = plugin.Status,
                    Error = plugin.Error,
                    SourcePath = plugin.SourcePath
                };

                foreach (var entry in plugin.Commands)
                {
                    var captured = entry;
                    row.Commands.Add(new PluginCommandRow
                    {
                        Title = captured.Title,
                        Description = captured.Description,
                        RunCommand = new RelayCommand(_ => RunPluginCommand(captured))
                    });
                }

                Plugins.Add(row);
            }

            OnPropertyChanged(nameof(HasPlugins));
            OnPropertyChanged(nameof(PluginSummary));
        }
        catch (Exception ex)
        {
            Logger.Error("Plugin rows", ex);
        }
    }

    private void UpdatePluginMetrics()
    {
        PluginMetrics.Clear();
        foreach (var value in _plugins.LastValues) PluginMetrics.Add(value);
        OnPropertyChanged(nameof(HasPluginMetrics));
        OnPropertyChanged(nameof(PluginSummary));
    }

    private void RunPluginCommand(PluginHost.PluginCommandEntry entry)
    {
        var selection = SelectedProcess;
        var context = new PluginCommandContext
        {
            SelectedProcessId = selection?.Pid,
            SelectedProcessName = selection?.Name
        };

        bool ok = _plugins.TryExecuteCommand(entry, context, out string message);
        SetStatus(message);
        if (!ok) Logger.Info($"Plugin command '{entry.Title}' was not executed.");
    }

    private void OpenPluginsFolder()
    {
        try
        {
            Directory.CreateDirectory(_plugins.PluginsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_plugins.PluginsDirectory}\"")
            {
                UseShellExecute = true
            });
            SetStatus($"Opened {_plugins.PluginsDirectory}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open the plugins folder: {ex.Message}");
        }
    }

    private void ReloadPlugins()
    {
        SetStatus("Reloading plugins…");
        try
        {
            _plugins.ReloadAll();
        }
        catch (Exception ex)
        {
            Logger.Error("Plugin reload", ex);
        }
        RebuildPluginRows();
        UpdatePluginMetrics();
        SetStatus($"Plugins reloaded — {Plugins.Count(p => !p.IsError)} loaded, {PluginMetrics.Count} metrics.");
    }

    // ═══════════════════════════════════════════════════════════════════ Lifecycle

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        _monitor.SampleUpdated -= OnSample;
        _alerts.AlertRaised -= OnAlertRaised;
        _plugins.ValuesUpdated -= OnPluginValues;
        _plugins.PluginsChanged -= OnPluginsChanged;
    }
}
