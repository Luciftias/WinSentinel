using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinSentinel.Abstractions;

namespace WinSentinel.Plugin.PacketCapture;

/// <summary>
/// Wireshark-lite: captures the traffic visible to this machine's adapter through a raw socket
/// (SIO_RCVALL, administrator required), keeps a bounded ring buffer, reports live stats and
/// exports standard .pcap files that Wireshark can open directly (DLT_RAW / link type 101).
/// Capture is intentionally scoped to the local interface — this is a diagnostics tool, not a
/// network surveillance tool.
/// </summary>
public sealed class PacketCapturePlugin : IWinSentinelPlugin
{
    private IPluginContext? _context;
    private CaptureSession? _session;

    public string Id => "diagnostics.packet-capture";
    public string Name => "Packet Capture (pcap)";
    public string Version => "1.0.0";
    public string Author => "WinSentinel";
    public string Description => "Captures traffic visible to this adapter (raw socket, admin), shows stats and exports .pcap files for Wireshark.";

    public void Initialize(IPluginContext context)
    {
        _context = context;
        _session = new CaptureSession(context);

        context.RegisterMetric(_session.PacketsMetric);
        context.RegisterMetric(_session.RateMetric);
        context.RegisterMetric(_session.TcpMetric);
        context.RegisterMetric(_session.UdpMetric);
        context.RegisterMetric(_session.SizeMetric);

        context.RegisterCommand(new ActionCommand("capture.start", "Start capture",
            "Begins raw-socket capture on the primary IPv4 interface (administrator required).",
            () => _session!.Start()));

        context.RegisterCommand(new ActionCommand("capture.stop", "Stop capture",
            "Stops the capture loop.", () => _session!.Stop()));

        context.RegisterCommand(new ActionCommand("capture.save", "Save capture (.pcap)…",
            "Writes the buffer to a Wireshark-compatible .pcap and reveals it in Explorer.",
            () => _session!.Save()));

        context.RegisterCommand(new ActionCommand("capture.clear", "Clear buffer",
            "Discards all buffered packets.", () => _session!.Clear()));

        context.Log("Packet capture plugin ready.");
    }

    public void Shutdown()
    {
        _session?.Stop();
        _context?.Log("Packet capture plugin shut down.");
    }

    // ══════════════════════════════════════════════════════════ capture session

    private sealed class CapturedPacket
    {
        public required DateTime Time { get; init; }
        public required byte[] Data { get; init; }
    }

    private sealed class CaptureSession
    {
        private const int MaxPackets = 200_000;
        private const long MaxBytes = 256L * 1024 * 1024;

        private readonly IPluginContext _context;
        private readonly object _gate = new();
        private readonly Queue<CapturedPacket> _packets = new();
        private long _totalBytes;
        private long _tcpCount;
        private long _udpCount;
        private Socket? _socket;
        private Thread? _thread;
        private volatile bool _running;

        private long _lastSampleCount;
        private DateTime _lastSampleTime = DateTime.UtcNow;
        private double _rate;

        public CaptureSession(IPluginContext context)
        {
            _context = context;
            PacketsMetric = new SimpleMetric("capture.packets", "Captured packets", string.Empty, () => CountPackets());
            RateMetric = new SimpleMetric("capture.rate", "Packet rate", "pps", () => SampleRate());
            TcpMetric = new SimpleMetric("capture.tcp", "TCP packets", string.Empty, () => Interlocked.Read(ref _tcpCount));
            UdpMetric = new SimpleMetric("capture.udp", "UDP packets", string.Empty, () => Interlocked.Read(ref _udpCount));
            SizeMetric = new SimpleMetric("capture.size", "Buffer size", "MB", () => Math.Round(Interlocked.Read(ref _totalBytes) / (1024.0 * 1024.0), 2));
        }

        public IPluginMetric PacketsMetric { get; }
        public IPluginMetric RateMetric { get; }
        public IPluginMetric TcpMetric { get; }
        public IPluginMetric UdpMetric { get; }
        public IPluginMetric SizeMetric { get; }

        private double CountPackets() { lock (_gate) { return _packets.Count; } }

        private double SampleRate()
        {
            long count;
            lock (_gate) { count = _packets.Count; }
            var now = DateTime.UtcNow;
            double seconds = Math.Max(0.1, (now - _lastSampleTime).TotalSeconds);
            long delta = count - _lastSampleCount;
            _lastSampleCount = count;
            _lastSampleTime = now;
            if (delta >= 0) _rate = delta / seconds;
            return Math.Round(_rate, 1);
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_running) return;

                var address = FindLocalAddress();
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
                try
                {
                    socket.Bind(new IPEndPoint(address, 0));
                    socket.IOControl(unchecked((int)0x98000001), new byte[] { 1, 0, 0, 0 }, null); // SIO_RCVALL
                }
                catch (Exception ex)
                {
                    socket.Dispose();
                    throw new InvalidOperationException(
                        $"Could not start capture on {address}. Raw capture requires administrator and a wired/Wi-Fi adapter ({ex.Message}).", ex);
                }

                socket.ReceiveBufferSize = 1024 * 1024;
                _socket = socket;
                _running = true;
                _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "WinSentinel.PacketCapture" };
                _thread.Start();
                _context.Log($"Capture started on {address}.");
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (!_running) return;
                _running = false;
                try { _socket?.Close(); } catch { /* ignore */ }
                _socket = null;
                _context.Log("Capture stopped.");
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _packets.Clear();
                _totalBytes = 0;
                _tcpCount = 0;
                _udpCount = 0;
            }
            _context.Log("Capture buffer cleared.");
        }

        public void Save()
        {
            List<CapturedPacket> snapshot;
            lock (_gate) { snapshot = _packets.ToList(); }
            if (snapshot.Count == 0)
                throw new InvalidOperationException("The capture buffer is empty — start a capture first.");

            string folder = Path.Combine(_context.PluginDirectory, "captures");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, $"winsentinel-{DateTime.Now:yyyyMMdd-HHmmss}.pcap");

            using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var writer = new BinaryWriter(stream))
            {
                // libpcap global header (little-endian), link type 101 = LINKTYPE_RAW (raw IP).
                writer.Write(0xa1b2c3d4u);
                writer.Write((ushort)2);
                writer.Write((ushort)4);
                writer.Write(0);
                writer.Write(0u);
                writer.Write(65535u);
                writer.Write(101u);

                foreach (var packet in snapshot)
                {
                    long ticks = packet.Time.Ticks;
                    writer.Write((uint)(ticks / TimeSpan.TicksPerSecond));
                    writer.Write((uint)(ticks % TimeSpan.TicksPerSecond / 10));
                    writer.Write((uint)packet.Data.Length);
                    writer.Write((uint)packet.Data.Length);
                    writer.Write(packet.Data);
                }
            }

            var info = new FileInfo(file);
            _context.Log($"Capture saved: {file} ({snapshot.Count} packets, {info.Length / 1024} KB).");
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }

        private void ReceiveLoop()
        {
            var buffer = new byte[65536];
            while (_running)
            {
                var socket = _socket;
                if (socket is null) break;

                int length;
                try
                {
                    length = socket.Receive(buffer);
                }
                catch (SocketException)
                {
                    Thread.Sleep(50);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (length <= 0) continue;

                var data = new byte[length];
                Buffer.BlockCopy(buffer, 0, data, 0, length);

                byte protocol = length >= 20 ? data[9] : (byte)0;
                lock (_gate)
                {
                    _packets.Enqueue(new CapturedPacket { Time = DateTime.Now, Data = data });
                    Interlocked.Add(ref _totalBytes, length);
                    if (protocol == 6) Interlocked.Increment(ref _tcpCount);
                    else if (protocol == 17) Interlocked.Increment(ref _udpCount);

                    while (_packets.Count > MaxPackets || Interlocked.Read(ref _totalBytes) > MaxBytes)
                    {
                        var dropped = _packets.Dequeue();
                        Interlocked.Add(ref _totalBytes, -dropped.Data.Length);
                    }
                }
            }
        }

        private static IPAddress FindLocalAddress()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                            return unicast.Address;
                    }
                }
            }
            catch { /* fall through */ }
            return IPAddress.Loopback;
        }
    }

    // ══════════════════════════════════════════════════════════ small helpers

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
