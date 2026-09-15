using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WinSentinel.Abstractions;

namespace WinSentinel.Plugin.PortScan;

/// <summary>
/// Nmap-lite for your own machine: a TCP connect scan of the common ports (1–1024 plus high-value
/// service ports), run concurrently with a short timeout. The default target is 127.0.0.1 — the
/// parameter prompt exists so you can point it at another host you own or are authorised to test.
/// Results are written to a text report and summarised in a metric.
/// </summary>
public sealed class PortScanPlugin : IWinSentinelPlugin
{
    private IPluginContext? _context;
    private volatile bool _scanning;
    private int _openPorts = -1; // -1 = no scan yet
    private int _completedScans;
    private string _lastTarget = string.Empty;
    private DateTime _lastScanTime;

    public string Id => "security.port-scan";
    public string Name => "Port Scanner (TCP)";
    public string Version => "1.0.0";
    public string Author => "WinSentinel";
    public string Description => "TCP connect scan of common ports on 127.0.0.1 (or an authorised host you type in), with a text report.";

    public void Initialize(IPluginContext context)
    {
        _context = context;

        context.RegisterMetric(new SimpleMetric("portscan.open", "Open ports (last scan)", string.Empty, () => _openPorts < 0 ? null : _openPorts));
        context.RegisterMetric(new SimpleMetric("portscan.scans", "Completed scans", string.Empty, () => _completedScans));

        context.RegisterCommand(new ScanCommand(this));
        context.RegisterCommand(new ActionCommand("portscan.open-report", "Open last scan report",
            "Opens the text report from the most recent scan (Notepad).", OpenReport));

        context.Log("Port scanner plugin ready. Only scan systems you are authorised to test.");
    }

    public void Shutdown() => _context?.Log("Port scanner plugin shut down.");

    // ══════════════════════════════════════════════════════════ scanning

    private void StartScan(string target)
    {
        if (_scanning)
            throw new InvalidOperationException("A scan is already running — wait for it to finish.");

        target = string.IsNullOrWhiteSpace(target) ? "127.0.0.1" : target.Trim();
        _scanning = true;
        _context?.Log($"Port scan started: {target} (TCP connect, {Ports.Length} ports).");

        _ = Task.Run(() => RunScanAsync(target));
    }

    private async Task RunScanAsync(string target)
    {
        try
        {
            IPAddress address;
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(target);
                if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
                address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
            }
            catch (Exception ex)
            {
                _context?.Log($"Port scan failed: could not resolve '{target}' ({ex.Message}).");
                return;
            }

            var started = DateTime.Now;
            var open = new List<int>();
            using var gate = new SemaphoreSlim(100);

            async Task ProbeAsync(int port)
            {
                await gate.WaitAsync();
                try
                {
                    using var client = new TcpClient(address.AddressFamily);
                    var connect = client.ConnectAsync(address, port);
                    var completed = await Task.WhenAny(connect, Task.Delay(250));
                    if (completed == connect && connect.IsCompletedSuccessfully && client.Connected)
                        lock (open) { open.Add(port); }
                }
                catch { /* closed / filtered / timeout — not an open port */ }
                finally { gate.Release(); }
            }

            await Task.WhenAll(Ports.Select(ProbeAsync));

            open.Sort();
            _openPorts = open.Count;
            _completedScans++;
            _lastTarget = target;
            _lastScanTime = started;

            WriteReport(target, address.ToString(), open, started);
            _context?.Log($"Port scan finished: {target} ({address}) — {open.Count} open port(s) of {Ports.Length} scanned. Report written.");
        }
        catch (Exception ex)
        {
            _context?.Log($"Port scan error: {ex.Message}");
        }
        finally
        {
            _scanning = false;
        }
    }

    private void WriteReport(string target, string address, List<int> open, DateTime started)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("WinSentinel — TCP connect port scan");
            sb.AppendLine("====================================");
            sb.AppendLine($"Target      : {target} ({address})");
            sb.AppendLine($"Started     : {started:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Duration    : {(DateTime.Now - started).TotalSeconds:0.0} s");
            sb.AppendLine($"Ports probed: {Ports.Length}");
            sb.AppendLine($"Open ports  : {open.Count}");
            sb.AppendLine();
            if (open.Count == 0)
            {
                sb.AppendLine("(no open TCP ports found in the scanned range)");
            }
            else
            {
                sb.AppendLine("PORT     COMMON SERVICE");
                foreach (int port in open)
                    sb.AppendLine($"{port,-8} {DescribePort(port)}");
            }
            sb.AppendLine();
            sb.AppendLine("Note: TCP connect scan only. Only scan systems you own or have explicit permission to test.");

            string path = Path.Combine(_context!.PluginDirectory, "last-scan.txt");
            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception ex)
        {
            _context?.Log($"Could not write the scan report: {ex.Message}");
        }
    }

    private void OpenReport()
    {
        string path = Path.Combine(_context!.PluginDirectory, "last-scan.txt");
        if (!File.Exists(path))
            throw new InvalidOperationException("No scan report yet — run a scan first.");

        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static readonly int[] Ports = BuildPortList();

    private static int[] BuildPortList()
    {
        var ports = new SortedSet<int>();
        for (int p = 1; p <= 1024; p++) ports.Add(p);
        foreach (int extra in new[]
                 {
                     1080, 1433, 1521, 2049, 2375, 2376, 3000, 3306, 3389, 5000, 5432, 5900,
                     5985, 5986, 6379, 8000, 8080, 8443, 8888, 9000, 9090, 9200, 11211, 27017
                 })
            ports.Add(extra);
        return ports.ToArray();
    }

    private static string DescribePort(int port) => port switch
    {
        21 => "FTP",
        22 => "SSH",
        23 => "Telnet",
        25 => "SMTP",
        53 => "DNS",
        80 => "HTTP",
        110 => "POP3",
        135 => "RPC",
        139 => "NetBIOS",
        143 => "IMAP",
        443 => "HTTPS",
        445 => "SMB",
        1433 => "MSSQL",
        1521 => "Oracle",
        3306 => "MySQL",
        3389 => "RDP",
        5432 => "PostgreSQL",
        5900 => "VNC",
        6379 => "Redis",
        8080 => "HTTP alt",
        8443 => "HTTPS alt",
        9200 => "Elasticsearch",
        11211 => "Memcached",
        27017 => "MongoDB",
        _ => string.Empty
    };

    // ══════════════════════════════════════════════════════════ helpers

    internal sealed class SimpleMetric : IPluginMetric
    {
        private readonly Func<double?> _sample;

        public SimpleMetric(string id, string name, string unit, Func<double?> sample)
        {
            Id = id;
            Name = name;
            Unit = unit;
            _sample = sample;
        }

        public string Id { get; }
        public string Name { get; }
        public string Unit { get; }
        public bool IsAvailable => true;
        public double? Sample() => _sample();
    }

    private sealed class ScanCommand : IPluginCommand
    {
        private readonly PortScanPlugin _plugin;

        public ScanCommand(PortScanPlugin plugin) => _plugin = plugin;

        public string Id => "portscan.scan";
        public string Title => "Scan host…";
        public string Description => "TCP connect scan of common ports. Defaults to this machine (127.0.0.1).";
        public bool RequiresParameter => true;
        public string ParameterLabel => "Host or IP — only scan systems you are authorised to test";
        public string ParameterPlaceholder => "127.0.0.1";

        public bool CanExecute(IPluginCommandContext context) => true;

        public void Execute(IPluginCommandContext context) => _plugin.StartScan(context.Parameter ?? "127.0.0.1");
    }

    private sealed class ActionCommand : IPluginCommand
    {
        private readonly Action _execute;

        public ActionCommand(string id, string title, string description, Action execute)
        {
            Id = id;
            Title = title;
            Description = description;
            _execute = execute;
        }

        public string Id { get; }
        public string Title { get; }
        public string Description { get; }
        public bool CanExecute(IPluginCommandContext context) => true;
        public void Execute(IPluginCommandContext context) => _execute();
    }
}
