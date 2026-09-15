using System.Windows.Threading;
using WinSentinel.Models;
using WinSentinel.Services;

namespace WinSentinel.ViewModels;

/// <summary>Minimal view model behind the floating balloon: just live CPU/RAM percentages.</summary>
public sealed class BalloonViewModel : ViewModelBase
{
    private readonly SystemMonitorService _monitor;
    private readonly Dispatcher _dispatcher;

    private double _cpu;
    public double Cpu
    {
        get => _cpu;
        private set { if (SetField(ref _cpu, value)) OnPropertyChanged(nameof(CpuText)); }
    }

    private double _ram;
    public double Ram
    {
        get => _ram;
        private set { if (SetField(ref _ram, value)) OnPropertyChanged(nameof(RamText)); }
    }

    public string CpuText => $"{Cpu:0}%";
    public string RamText => $"{Ram:0}%";

    public BalloonViewModel(SystemMonitorService monitor)
    {
        _monitor = monitor;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _monitor.SampleUpdated += OnSample;
        if (_monitor.Latest is not null) Apply(_monitor.Latest);
    }

    private void OnSample(object? sender, MetricSample sample)
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => Apply(sample)); return; }
        Apply(sample);
    }

    private void Apply(MetricSample s)
    {
        Cpu = s.CpuPercent;
        Ram = s.RamPercent;
    }
}
