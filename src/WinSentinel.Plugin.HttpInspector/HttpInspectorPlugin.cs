using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinSentinel.Abstractions;

namespace WinSentinel.Plugin.HttpInspector;

/// <summary>
/// Burp-lite, honestly scoped: a loopback-only HTTP forward proxy. Plain-HTTP requests are
/// forwarded and logged (method, target, status, bytes); HTTPS is tunnelled via CONNECT and only
/// host:port plus byte counts are recorded — WinSentinel never decrypts TLS and never installs a
/// root certificate. Point a browser or tool at 127.0.0.1:8878 (configurable via settings.json)
/// to watch your own machine's web traffic.
/// </summary>
public sealed class HttpInspectorPlugin : IWinSentinelPlugin
{
    private IPluginContext? _context;
    private HttpProxy? _proxy;

    public string Id => "security.http-inspector";
    public string Name => "HTTP Inspector (local proxy)";
    public string Version => "1.0.0";
    public string Author => "WinSentinel";
    public string Description => "Loopback HTTP proxy: logs plain-HTTP requests and tunnels HTTPS (CONNECT) with byte counts. No TLS interception.";

    public void Initialize(IPluginContext context)
    {
        _context = context;
        _proxy = new HttpProxy(context);

        context.RegisterMetric(new SimpleMetric("proxy.requests", "Proxied requests", string.Empty, () => _proxy!.RequestCount));
        context.RegisterMetric(new SimpleMetric("proxy.tunnels", "Active tunnels", string.Empty, () => _proxy!.ActiveTunnels));
        context.RegisterMetric(new SimpleMetric("proxy.data", "Logged data", "MB", () => _proxy!.DataMegabytes));
        context.RegisterMetric(new SimpleMetric("proxy.port", "Proxy port", string.Empty, () => _proxy!.Port));

        context.RegisterCommand(new ActionCommand("proxy.start", "Start proxy",
            "Starts the loopback HTTP proxy (point clients at 127.0.0.1:port).", () => _proxy!.Start()));
        context.RegisterCommand(new ActionCommand("proxy.stop", "Stop proxy",
            "Stops the proxy and closes active tunnels.", () => _proxy!.Stop()));
        context.RegisterCommand(new ActionCommand("proxy.open-log", "Open request log",
            "Opens the request log (Notepad).", OpenLog));

        context.Log($"HTTP inspector ready (proxy will listen on 127.0.0.1:{_proxy.Port}).");
    }

    public void Shutdown()
    {
        _proxy?.Stop();
        _context?.Log("HTTP inspector shut down.");
    }

    private void OpenLog()
    {
        string path = Path.Combine(_context!.PluginDirectory, "requests.log");
        if (!File.Exists(path))
            throw new InvalidOperationException("No requests logged yet — start the proxy and send some traffic through it.");

        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    // ══════════════════════════════════════════════════════════ proxy

    private sealed class HttpProxy
    {
        private readonly IPluginContext _context;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;

        private long _requests;
        private long _tunnels;
        private long _bytes;

        public HttpProxy(IPluginContext context)
        {
            _context = context;
            Port = ReadPort(context.SettingsFilePath);
        }

        public int Port { get; }

        public double RequestCount => Interlocked.Read(ref _requests);
        public double ActiveTunnels => Interlocked.Read(ref _tunnels);
        public double DataMegabytes => Math.Round(Interlocked.Read(ref _bytes) / (1024.0 * 1024.0), 2);

        private static int ReadPort(string settingsPath)
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                    if (document.RootElement.TryGetProperty("port", out var portElement) &&
                        portElement.TryGetInt32(out int port) && port is > 1023 and < 65536)
                        return port;
                }
            }
            catch { /* default below */ }
            return 8878;
        }

        public void Start()
        {
            if (_listener is not null) return;

            var listener = new TcpListener(IPAddress.Loopback, Port);
            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException($"Could not start the proxy on 127.0.0.1:{Port} — {ex.Message}");
            }

            _listener = listener;
            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(listener, _cts.Token);
            _context.Log($"HTTP inspector proxy listening on 127.0.0.1:{Port}.");
        }

        public void Stop()
        {
            var listener = _listener;
            _listener = null;
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { listener?.Stop(); } catch { /* ignore */ }
            _cts = null;
            _context.Log("HTTP inspector proxy stopped.");
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { await Task.Delay(150).ConfigureAwait(false); continue; }

                _ = HandleClientAsync(client);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    var (header, leftover) = await ReadHeaderBlockAsync(stream, 32 * 1024).ConfigureAwait(false);
                    if (header is null) return;

                    var lines = header.Split("\r\n");
                    var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) return;

                    string method = parts[0].ToUpperInvariant();
                    string target = parts[1];

                    if (method == "CONNECT")
                    {
                        await TunnelAsync(stream, target).ConfigureAwait(false);
                        return;
                    }

                    await ForwardAsync(stream, lines, method, target, leftover).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _context.Log($"Proxy connection error: {ex.Message}");
                }
            }
        }

        // ── plain HTTP ────────────────────────────────────────────────────

        private async Task ForwardAsync(NetworkStream clientStream, string[] headerLines, string method, string target, byte[] leftover)
        {
            Uri? uri = Uri.TryCreate(target, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
                ? absolute
                : null;

            string host;
            int port;
            string path;

            if (uri is not null)
            {
                host = uri.Host;
                port = uri.Port;
                path = uri.PathAndQuery;
            }
            else
            {
                string hostHeader = ExtractHeader(headerLines, "Host") ?? string.Empty;
                string[] hostBits = hostHeader.Split(':', 2);
                host = hostBits[0];
                port = hostBits.Length == 2 && int.TryParse(hostBits[1], out int parsedPort) ? parsedPort : 80;
                path = target;
            }

            if (host.Length == 0) return;

            // Rebuild the request for the origin server: relative path, Connection: close, host-only Host header.
            var builder = new StringBuilder();
            builder.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            foreach (var line in headerLines.Skip(1))
            {
                if (line.Length == 0) continue;
                if (line.StartsWith("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) continue;
                builder.Append(line).Append("\r\n");
            }
            builder.Append("Host: ").Append(host).Append("\r\n");
            builder.Append("Connection: close\r\n\r\n");

            var requestBytes = Encoding.ASCII.GetBytes(builder.ToString());

            using var upstream = new TcpClient();
            try
            {
                await upstream.ConnectAsync(host, port).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteStringAsync(clientStream, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                LogLine($"http   {method} {target} -> 502 ({ex.Message})");
                return;
            }

            var upstreamStream = upstream.GetStream();
            await upstreamStream.WriteAsync(requestBytes).ConfigureAwait(false);
            if (leftover.Length > 0) await upstreamStream.WriteAsync(leftover).ConfigureAwait(false);

            long bytes = requestBytes.Length + leftover.Length;

            // Forward any remaining request body per Content-Length.
            int contentLength = ExtractContentLength(headerLines);
            int remaining = contentLength - leftover.Length;
            if (remaining > 0)
            {
                var body = new byte[64 * 1024];
                while (remaining > 0)
                {
                    int read = await clientStream.ReadAsync(body.AsMemory(0, Math.Min(body.Length, remaining))).ConfigureAwait(false);
                    if (read <= 0) break;
                    await upstreamStream.WriteAsync(body.AsMemory(0, read)).ConfigureAwait(false);
                    remaining -= read;
                    bytes += read;
                }
            }

            // Response: header block (so we can log the status), then pump until close.
            var (responseHeader, responseLeftover) = await ReadHeaderBlockAsync(upstreamStream, 64 * 1024).ConfigureAwait(false);
            string status = "?";
            if (responseHeader is not null)
            {
                var statusLine = responseHeader.Split("\r\n")[0].Split(' ');
                if (statusLine.Length >= 2) status = statusLine[1];
                await WriteStringAsync(clientStream, responseHeader).ConfigureAwait(false);
            }
            if (responseLeftover.Length > 0) await clientStream.WriteAsync(responseLeftover).ConfigureAwait(false);
            bytes += responseLeftover.Length + await PumpAsync(upstreamStream, clientStream).ConfigureAwait(false);

            Interlocked.Increment(ref _requests);
            Interlocked.Add(ref _bytes, bytes);
            LogLine($"http   {method,-6} {target} -> {status}  {bytes / 1024.0:0.0} KB");
        }

        // ── HTTPS tunnel ──────────────────────────────────────────────────

        private async Task TunnelAsync(NetworkStream clientStream, string target)
        {
            string[] bits = target.Split(':', 2);
            if (bits.Length != 2 || !int.TryParse(bits[1], out int port))
            {
                await WriteStringAsync(clientStream, "HTTP/1.1 400 Bad Request\r\n\r\n").ConfigureAwait(false);
                return;
            }

            using var upstream = new TcpClient();
            try
            {
                await upstream.ConnectAsync(bits[0], port).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteStringAsync(clientStream, "HTTP/1.1 502 Bad Gateway\r\n\r\n").ConfigureAwait(false);
                LogLine($"https  CONNECT {target} -> 502 ({ex.Message})");
                return;
            }

            await WriteStringAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n").ConfigureAwait(false);
            Interlocked.Increment(ref _tunnels);

            var upstreamStream = upstream.GetStream();
            var up = PumpAsync(clientStream, upstreamStream);
            var down = PumpAsync(upstreamStream, clientStream);
            await Task.WhenAll(up, down).ConfigureAwait(false);

            Interlocked.Decrement(ref _tunnels);
            long bytes = up.Result + down.Result;
            Interlocked.Increment(ref _requests);
            Interlocked.Add(ref _bytes, bytes);
            LogLine($"https  CONNECT {target} (tunnelled, not decrypted)  {bytes / 1024.0:0.0} KB");
        }

        // ── helpers ───────────────────────────────────────────────────────

        private static async Task<long> PumpAsync(NetworkStream from, NetworkStream to)
        {
            var buffer = new byte[64 * 1024];
            long total = 0;
            try
            {
                while (true)
                {
                    int read = await from.ReadAsync(buffer).ConfigureAwait(false);
                    if (read <= 0) break;
                    await to.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    total += read;
                }
            }
            catch { /* either side closed */ }
            return total;
        }

        /// <summary>Reads until the blank line after the headers; returns header text + any body bytes already read.</summary>
        private static async Task<(string? Header, byte[] Leftover)> ReadHeaderBlockAsync(NetworkStream stream, int maxBytes)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int headerEnd = -1;

            while (buffer.Length < maxBytes)
            {
                int read = await stream.ReadAsync(chunk).ConfigureAwait(false);
                if (read <= 0) break;
                buffer.Write(chunk, 0, read);
                headerEnd = FindDoubleCrlf(buffer.GetBuffer(), (int)buffer.Length);
                if (headerEnd >= 0) break;
            }

            if (headerEnd < 0) return (null, Array.Empty<byte>());

            var all = buffer.ToArray();
            return (Encoding.ASCII.GetString(all, 0, headerEnd), all[headerEnd..]);
        }

        private static int FindDoubleCrlf(byte[] data, int length)
        {
            for (int i = 0; i + 3 < length; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i + 4;
            }
            return -1;
        }

        private static string? ExtractHeader(string[] headerLines, string name)
        {
            foreach (var line in headerLines)
            {
                int idx = line.IndexOf(':');
                if (idx <= 0) continue;
                if (line[..idx].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return line[(idx + 1)..].Trim();
            }
            return null;
        }

        private static int ExtractContentLength(string[] headerLines)
        {
            string? value = ExtractHeader(headerLines, "Content-Length");
            return value is not null && int.TryParse(value, out int length) ? length : 0;
        }

        private static async Task WriteStringAsync(NetworkStream stream, string text)
            => await stream.WriteAsync(Encoding.ASCII.GetBytes(text)).ConfigureAwait(false);

        private void LogLine(string line)
        {
            try
            {
                string path = Path.Combine(_context.PluginDirectory, "requests.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 5 * 1024 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
            }
            catch { /* logging must never break the proxy */ }
        }
    }

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
