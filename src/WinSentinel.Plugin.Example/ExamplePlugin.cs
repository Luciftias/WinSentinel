using System.Diagnostics;
using System.Net.NetworkInformation;
using WinSentinel.Abstractions;

namespace WinSentinel.Plugin.Example;

/// <summary>
/// Sample plugin demonstrating both extension points:
///  • metrics — "C: free space" (%) and "Ping 1.1.1.1" (ms, ICMP),
///  • commands — open Task Manager, open the plugins folder, and a log-only test command.
/// Copy <c>WinSentinel.Plugin.Example.dll</c> into %AppData%\WinSentinel\plugins and reload.
/// </summary>
public sealed class ExamplePlugin : IWinSentinelPlugin
{
    private IPluginContext? _context;

    public string Id => "example.diagnostics";

    public string Name => "Example Diagnostics";

    public string Version => "1.0.0";

    public string Author => "WinSentinel";

    public string Description => "Sample plugin: C: free space, ICMP latency to 1.1.1.1, and three demo commands.";

    public void Initialize(IPluginContext context)
    {
        _context = context;

        context.RegisterMetric(new FreeDiskMetric());
        context.RegisterMetric(new PingMetric());

        context.RegisterCommand(new DelegateCommand(
            id: "open-taskmgr",
            title: "Open Task Manager",
            description: "Launches taskmgr.exe — demonstrates a fire-and-forget plugin action.",
            canExecute: _ => true,
            execute: _ =>
            {
                Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
            }));

        context.RegisterCommand(new DelegateCommand(
            id: "open-plugins-folder",
            title: "Open plugins folder",
            description: "Opens the folder plugins are loaded from.",
            canExecute: _ => true,
            execute: _ =>
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{context.PluginDirectory}\"")
                {
                    UseShellExecute = true
                });
            }));

        context.RegisterCommand(new DelegateCommand(
            id: "log-status",
            title: "Log plugin status",
            description: "Writes a line to the WinSentinel log (useful to smoke-test commands).",
            canExecute: _ => true,
            execute: ctx => context.Log(
                $"status command run — selected process: {(ctx.SelectedProcessName ?? "none")} ({(ctx.SelectedProcessId?.ToString() ?? "—")})")));

        context.Log("Example plugin initialised.");
    }

    public void Shutdown() => _context?.Log("Example plugin shut down.");

    // ── metrics ───────────────────────────────────────────────────────────

    private sealed class FreeDiskMetric : IPluginMetric
    {
        public string Id => "example.c-free-percent";
        public string Name => "C: free space";
        public string Unit => "%";
        public bool IsAvailable => true;

        public double? Sample()
        {
            var drive = new DriveInfo("C");
            return drive.TotalSize == 0
                ? null
                : Math.Round(drive.AvailableFreeSpace * 100.0 / drive.TotalSize, 1);
        }
    }

    private sealed class PingMetric : IPluginMetric
    {
        private readonly Ping _ping = new();

        public string Id => "example.ping-1-1-1-1";
        public string Name => "Ping 1.1.1.1";
        public string Unit => "ms";
        public bool IsAvailable => true;

        public double? Sample()
        {
            try
            {
                var reply = _ping.Send("1.1.1.1", 1500);
                return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
            }
            catch
            {
                return null; // offline is not an error
            }
        }
    }

    // ── commands ──────────────────────────────────────────────────────────

    private sealed class DelegateCommand : IPluginCommand
    {
        private readonly Func<IPluginCommandContext, bool> _canExecute;
        private readonly Action<IPluginCommandContext> _execute;

        public DelegateCommand(string id, string title, string description,
            Func<IPluginCommandContext, bool> canExecute, Action<IPluginCommandContext> execute)
        {
            Id = id;
            Title = title;
            Description = description;
            _canExecute = canExecute;
            _execute = execute;
        }

        public string Id { get; }
        public string Title { get; }
        public string Description { get; }

        public bool CanExecute(IPluginCommandContext context) => _canExecute(context);

        public void Execute(IPluginCommandContext context) => _execute(context);
    }
}
