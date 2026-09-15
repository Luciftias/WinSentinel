using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using WinSentinel.Abstractions;

namespace WinSentinel.Services;

/// <summary>
/// In-process plugin system. Discovers plugin assemblies under
/// <c>%AppData%\WinSentinel\plugins</c> (or <c>WINSENTINEL_PLUGINS</c> when set), loads each into
/// its own collectible <see cref="AssemblyLoadContext"/>, samples registered metrics on a
/// background cadence and exposes registered commands to the UI.
///
/// Design guarantees:
///  • <b>Error isolation</b> — a plugin that fails to load, initialize, sample or execute is
///    marked with its error and never takes the app down; metric-error logging is rate-limited.
///  • <b>Type unification</b> — the host shares its own <c>WinSentinel.Abstractions</c> instance
///    with every plugin, so contracts always match.
///  • <b>Best-effort reload</b> — unloads collectible contexts and rescans; a DLL that is still
///    file-locked is reported as failed instead of crashing.
///
/// <para><b>Security:</b> plugins run in-process with the same administrator privileges as the
/// app. That is the documented trade-off of this design — only install plugins you trust.</para>
/// </summary>
public sealed class PluginHost : IDisposable
{
    public const string StatusLoaded = "Loaded";
    public const string StatusFailed = "Failed";
    public const string StatusSkipped = "Skipped";

    private static readonly string[] HostAssemblyNames = { "WinSentinel", "WinSentinel.Abstractions" };

    public sealed record PluginMetricValue(
        string PluginId, string PluginName, string MetricId, string MetricName,
        string Unit, double? Value, string? Error, bool Available)
    {
        public string ValueDisplay => Value is double v
            ? $"{v:0.##} {Unit}".TrimEnd()
            : Error is null ? "—" : "error";

        public string NameDisplay => $"{MetricName}  ({PluginName})";
    }

    public sealed class PluginCommandEntry
    {
        public required string PluginId { get; init; }
        public required string PluginName { get; init; }
        public required string Title { get; init; }
        public required string Description { get; init; }
        public required IPluginCommand Command { get; init; }
        public string Display => $"{Title} ({PluginName})";

        /// <summary>True when the host must prompt the user for a parameter before executing.</summary>
        public bool RequiresParameter => Command.RequiresParameter;

        public string ParameterLabel => Command.ParameterLabel;

        public string ParameterPlaceholder => Command.ParameterPlaceholder;
    }

    public sealed class LoadedPlugin
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Version { get; init; }
        public required string Author { get; init; }
        public required string Description { get; init; }
        public required string Status { get; init; }
        public string? Error { get; init; }
        public required string SourcePath { get; init; }
        internal IWinSentinelPlugin? Instance { get; init; }
        internal PluginLoadContext? LoadContext { get; init; }
        public IReadOnlyList<IPluginMetric> Metrics { get; internal set; } = Array.Empty<IPluginMetric>();
        public IReadOnlyList<PluginCommandEntry> Commands { get; internal set; } = Array.Empty<PluginCommandEntry>();
    }

    private readonly object _gate = new();
    private readonly List<LoadedPlugin> _plugins = new();
    private readonly System.Timers.Timer _timer;
    private readonly Dictionary<string, DateTime> _lastMetricErrorLog = new();
    private IReadOnlyList<PluginMetricValue> _lastValues = Array.Empty<PluginMetricValue>();
    private bool _disposed;

    public PluginHost(string? pluginsDirectory = null)
    {
        PluginsDirectory = pluginsDirectory
            ?? Environment.GetEnvironmentVariable("WINSENTINEL_PLUGINS")
            ?? Path.Combine(Logger.LogDirectory, "plugins");

        _timer = new System.Timers.Timer(5000) { AutoReset = true };
        _timer.Elapsed += (_, _) => SampleNow();
    }

    public string PluginsDirectory { get; }

    public IReadOnlyList<LoadedPlugin> Plugins
    {
        get { lock (_gate) { return _plugins.ToArray(); } }
    }

    public IReadOnlyList<PluginMetricValue> LastValues => _lastValues;

    public IReadOnlyList<PluginCommandEntry> Commands
        => Plugins.SelectMany(p => p.Commands).ToList();

    /// <summary>Raised after every metric sampling pass (background thread).</summary>
    public event EventHandler? ValuesUpdated;

    /// <summary>Raised after a load/reload pass (background/UI thread).</summary>
    public event EventHandler? PluginsChanged;

    // ══════════════════════════════════════════════════════════ lifecycle

    public void Start()
    {
        SampleNow();
        _timer.Start();
    }

    /// <summary>Scans the plugins directory and loads every plugin assembly found.</summary>
    public void LoadAll()
    {
        lock (_gate) { _plugins.Clear(); }
        _lastValues = Array.Empty<PluginMetricValue>();

        var results = new List<LoadedPlugin>();
        try
        {
            Directory.CreateDirectory(PluginsDirectory);
        }
        catch (Exception ex)
        {
            Logger.Error("Plugins directory", ex);
        }

        foreach (var path in DiscoverPluginAssemblies())
        {
            if (_disposed) return;
            results.Add(LoadOne(path));
        }

        lock (_gate) { _plugins.AddRange(results); }

        Logger.Info($"Plugins: {results.Count(p => p.Status == StatusLoaded)} loaded, " +
                    $"{results.Count(p => p.Status != StatusLoaded)} skipped/failed.");
        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Best-effort reload: shuts plugins down, unloads contexts and rescans the folder.</summary>
    public void ReloadAll()
    {
        ShutdownAll();

        // Collectible contexts release their file locks once collected; a couple of passes is the
        // documented best effort. If a DLL stays locked it is reported as Failed, not crashed on.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        LoadAll();
        SampleNow();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        ShutdownAll();
    }

    private void ShutdownAll()
    {
        List<LoadedPlugin> plugins;
        lock (_gate)
        {
            plugins = _plugins.ToList();
            _plugins.Clear();
        }

        foreach (var plugin in plugins)
        {
            try { plugin.Instance?.Shutdown(); }
            catch (Exception ex) { Logger.Error($"[plugin:{plugin.Id}] shutdown", ex); }

            try { plugin.LoadContext?.Unload(); }
            catch (Exception ex) { Logger.Error($"[plugin:{plugin.Id}] unload", ex); }
        }

        _lastValues = Array.Empty<PluginMetricValue>();
    }

    // ══════════════════════════════════════════════════════════ discovery / loading

    private IEnumerable<string> DiscoverPluginAssemblies()
    {
        // Root *.dll (single-file plugins) plus one level of subfolders (plugins with dependencies).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();

        void AddRange(IEnumerable<string> files)
        {
            foreach (var file in files)
            {
                if (seen.Add(file)) results.Add(file);
            }
        }

        try { AddRange(Directory.EnumerateFiles(PluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly)); }
        catch (Exception ex) { Logger.Error("Plugin discovery", ex); }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(PluginsDirectory))
            {
                try { AddRange(Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly)); }
                catch (Exception ex) { Logger.Error($"Plugin discovery '{dir}'", ex); }
            }
        }
        catch (Exception ex) { Logger.Error("Plugin discovery (folders)", ex); }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    private LoadedPlugin LoadOne(string path)
    {
        string fileName = Path.GetFileName(path);

        // Never load the host itself or a stray copy of the contracts assembly as a plugin.
        if (HostAssemblyNames.Any(name => fileName.Equals(name + ".dll", StringComparison.OrdinalIgnoreCase)))
            return Skipped(path, "Host assembly — skipped");

        PluginLoadContext? context = null;
        try
        {
            context = new PluginLoadContext(path);
            var assembly = context.LoadFromAssemblyPath(path);

            var pluginTypes = SafeGetTypes(assembly)
                .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IWinSentinelPlugin).IsAssignableFrom(t))
                .ToList();

            if (pluginTypes.Count == 0)
            {
                context.Unload();
                return Skipped(path, "No IWinSentinelPlugin types found");
            }

            var instance = (IWinSentinelPlugin)Activator.CreateInstance(pluginTypes[0])!;
            var contextImpl = new PluginContextImpl(this, instance, Path.Combine(PluginsDirectory, SanitizeId(instance.Id)));

            instance.Initialize(contextImpl);

            var plugin = new LoadedPlugin
            {
                Id = instance.Id,
                Name = instance.Name,
                Version = instance.Version,
                Author = instance.Author,
                Description = instance.Description,
                Status = StatusLoaded,
                SourcePath = path,
                Instance = instance,
                LoadContext = context,
                Metrics = contextImpl.Metrics.ToArray(),
                Commands = contextImpl.Commands.ToArray()
            };

            Logger.Info($"Plugin loaded: {plugin.Name} {plugin.Version} ({plugin.Id}) — " +
                        $"{plugin.Metrics.Count} metrics, {plugin.Commands.Count} commands.");
            return plugin;
        }
        catch (Exception ex)
        {
            Logger.Error($"Plugin load '{fileName}'", ex);
            try { context?.Unload(); } catch { /* ignore */ }
            return Failed(path, ex.Message);
        }
    }

    private static LoadedPlugin Skipped(string path, string reason) => new()
    {
        Id = Path.GetFileNameWithoutExtension(path),
        Name = Path.GetFileName(path),
        Version = "—",
        Author = "—",
        Description = string.Empty,
        Status = StatusSkipped,
        Error = reason,
        SourcePath = path
    };

    private static LoadedPlugin Failed(string path, string message) => new()
    {
        Id = Path.GetFileNameWithoutExtension(path),
        Name = Path.GetFileName(path),
        Version = "—",
        Author = "—",
        Description = string.Empty,
        Status = StatusFailed,
        Error = message,
        SourcePath = path
    };

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static string SanitizeId(string id)
    {
        var chars = id.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }

    // ══════════════════════════════════════════════════════════ sampling

    /// <summary>Samples every registered metric once. Safe to call from any thread.</summary>
    public void SampleNow()
    {
        if (_disposed) return;

        List<LoadedPlugin> plugins;
        lock (_gate) { plugins = _plugins.ToList(); }

        var values = new List<PluginMetricValue>(16);
        foreach (var plugin in plugins)
        {
            if (plugin.Status != StatusLoaded) continue;

            foreach (var metric in plugin.Metrics)
            {
                double? value = null;
                string? error = null;
                bool available = false;

                try
                {
                    available = metric.IsAvailable;
                    if (available)
                    {
                        value = metric.Sample();
                        if (value is double d && (double.IsNaN(d) || double.IsInfinity(d)))
                        {
                            value = null;
                            error = "invalid value";
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    LogMetricError(plugin.Id, metric.Id, ex);
                }

                values.Add(new PluginMetricValue(
                    plugin.Id, plugin.Name, metric.Id, metric.Name, metric.Unit ?? string.Empty,
                    value, error, available));
            }
        }

        _lastValues = values;
        ValuesUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Metric exceptions are logged at most once per five minutes per metric.</summary>
    private void LogMetricError(string pluginId, string metricId, Exception ex)
    {
        string key = pluginId + "|" + metricId;
        lock (_gate)
        {
            if (_lastMetricErrorLog.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(5))
                return;
            _lastMetricErrorLog[key] = DateTime.UtcNow;
        }
        Logger.Error($"[plugin:{pluginId}] metric '{metricId}'", ex);
    }

    // ══════════════════════════════════════════════════════════ commands

    /// <summary>Executes a plugin command with error isolation; returns false + message on failure.</summary>
    public bool TryExecuteCommand(PluginCommandEntry entry, IPluginCommandContext context, out string message)
    {
        try
        {
            if (!entry.Command.CanExecute(context))
            {
                message = $"'{entry.Title}' is not available right now.";
                return false;
            }

            entry.Command.Execute(context);
            message = $"Plugin command '{entry.Title}' executed.";
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[plugin:{entry.PluginId}] command '{entry.Command.Id}'", ex);
            message = $"Plugin command failed: {ex.Message}";
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════ context implementation

    private sealed class PluginContextImpl : IPluginContext
    {
        private readonly IWinSentinelPlugin _plugin;

        public PluginContextImpl(PluginHost host, IWinSentinelPlugin plugin, string pluginDirectory)
        {
            _plugin = plugin;
            PluginDirectory = pluginDirectory;
            SettingsFilePath = Path.Combine(pluginDirectory, "settings.json");

            try { Directory.CreateDirectory(pluginDirectory); }
            catch { /* plugin may still work without a data directory */ }
        }

        public List<IPluginMetric> Metrics { get; } = new();

        public List<PluginCommandEntry> Commands { get; } = new();

        public string PluginDirectory { get; }

        public string SettingsFilePath { get; }

        public void Log(string message) => Logger.Info($"[plugin:{_plugin.Id}] {message}");

        public void RegisterMetric(IPluginMetric metric)
        {
            if (metric is null) throw new ArgumentNullException(nameof(metric));
            Metrics.Add(metric);
        }

        public void RegisterCommand(IPluginCommand command)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));
            Commands.Add(new PluginCommandEntry
            {
                PluginId = _plugin.Id,
                PluginName = _plugin.Name,
                Title = command.Title,
                Description = command.Description,
                Command = command
            });
        }
    }

    // ══════════════════════════════════════════════════════════ load context

    /// <summary>
    /// Per-plugin collectible load context. The host's own assemblies (WinSentinel and
    /// WinSentinel.Abstractions) deliberately fall back to the default context so plugin code
    /// and host code share one type identity for the contracts.
    /// </summary>
    internal sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath)
            : base($"WinSentinelPlugin:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is not null &&
                HostAssemblyNames.Contains(assemblyName.Name, StringComparer.OrdinalIgnoreCase))
                return null; // → default context (shared instance)

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
