using System.Windows.Threading;
using WinSentinel.Models;
using WinSentinel.Services;

namespace WinSentinel.ViewModels;

/// <summary>
/// View model behind the floating balloon: live CPU/RAM plus optional GPU, disk and network
/// rows, all driven by user settings. Settings changes are picked up live.
/// </summary>
public sealed class BalloonViewModel : ViewModelBase, IDisposable
{
    private readonly SystemMonitorService _monitor;
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;

    public BalloonViewModel(SystemMonitorService monitor, SettingsService settings)
    {
        _monitor = monitor;
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _monitor.SampleUpdated += OnSample;
        _settings.Changed += OnSettingsChanged;
        if (_monitor.Latest is not null) Apply(_monitor.Latest);
    }

    // ── Live values ───────────────────────────────────────────────────────
    private double _cpu;
    public double Cpu { get => _cpu; private set { if (SetField(ref _cpu, value)) OnPropertyChanged(nameof(CpuText)); } }

    private double _ram;
    public double Ram { get => _ram; private set { if (SetField(ref _ram, value)) OnPropertyChanged(nameof(RamText)); } }

    private double _gpu;
    public double Gpu { get => _gpu; private set { if (SetField(ref _gpu, value)) OnPropertyChanged(nameof(GpuText)); } }

    private double _diskBps;
    public double DiskBps { get => _diskBps; private set { if (SetField(ref _diskBps, value)) OnPropertyChanged(nameof(DiskText)); } }

    private double _netDownBps;
    public double NetDownBps { get => _netDownBps; private set { if (SetField(ref _netDownBps, value)) OnPropertyChanged(nameof(NetText)); } }

    private double _netUpBps;
    public double NetUpBps { get => _netUpBps; private set { if (SetField(ref _netUpBps, value)) OnPropertyChanged(nameof(NetText)); } }

    // ── Visibility from settings ──────────────────────────────────────────
    public bool ShowGpu => _settings.Current.BalloonShowGpu;
    public bool ShowDisk => _settings.Current.BalloonShowDisk;
    public bool ShowNet => _settings.Current.BalloonShowNet;

    /// <summary>Window opacity, from settings.</summary>
    public double Opacity => _settings.Current.BalloonOpacity;

    // ── Position persistence ──────────────────────────────────────────────
    public double PositionX => _settings.Current.BalloonX;
    public double PositionY => _settings.Current.BalloonY;

    public void SavePosition(double x, double y)
        => _settings.Update(s => { s.BalloonX = x; s.BalloonY = y; });

    // ── Display strings ───────────────────────────────────────────────────
    public string CpuText => $"{Cpu:0}%";
    public string RamText => $"{Ram:0}%";
    public string GpuText => $"{Gpu:0}%";
    public string DiskText => ProcessRow.FormatRate(DiskBps);
    public string NetText => $"↓{ProcessRow.FormatRate(NetDownBps)}  ↑{ProcessRow.FormatRate(NetUpBps)}";

    private void OnSettingsChanged(object? sender, AppSettings s)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() =>
            {
                OnPropertyChanged(nameof(ShowGpu));
                OnPropertyChanged(nameof(ShowDisk));
                OnPropertyChanged(nameof(ShowNet));
                OnPropertyChanged(nameof(Opacity));
            });
            return;
        }
        OnPropertyChanged(nameof(ShowGpu));
        OnPropertyChanged(nameof(ShowDisk));
        OnPropertyChanged(nameof(ShowNet));
        OnPropertyChanged(nameof(Opacity));
    }

    private void OnSample(object? sender, MetricSample sample)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => Apply(sample));
            return;
        }
        Apply(sample);
    }

    private void Apply(MetricSample s)
    {
        Cpu = s.CpuPercent;
        Ram = s.RamPercent;
        Gpu = s.GpuAvailable ? s.GpuPercent : 0;
        DiskBps = s.DiskReadBps + s.DiskWriteBps;
        NetDownBps = s.NetDownBps;
        NetUpBps = s.NetUpBps;
    }

    public void Dispose()
    {
        _monitor.SampleUpdated -= OnSample;
        _settings.Changed -= OnSettingsChanged;
    }
}
