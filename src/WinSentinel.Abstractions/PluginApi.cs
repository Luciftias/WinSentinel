namespace WinSentinel.Abstractions;

/// <summary>Host services handed to a plugin during <see cref="IWinSentinelPlugin.Initialize"/>.</summary>
public interface IPluginContext
{
    /// <summary>Per-plugin data directory (created on demand), e.g. for caches or databases.</summary>
    string PluginDirectory { get; }

    /// <summary>Convenience path: <c>{PluginDirectory}\settings.json</c> — plugins own its format.</summary>
    string SettingsFilePath { get; }

    /// <summary>Writes a line to the WinSentinel log (prefixed with the plugin id).</summary>
    void Log(string message);

    /// <summary>Registers a metric. Values are sampled on a background thread (~5 s cadence).</summary>
    void RegisterMetric(IPluginMetric metric);

    /// <summary>Registers a command. Commands surface in the tray "Plugins" menu and Settings.</summary>
    void RegisterCommand(IPluginCommand command);
}

/// <summary>
/// One plugin-provided metric. <see cref="Sample"/> is called on a background thread — it must be
/// thread-safe, fast (a few hundred ms at most) and must not throw for "no data" (return null).
/// </summary>
public interface IPluginMetric
{
    string Id { get; }

    /// <summary>Display name, e.g. "CPU fan".</summary>
    string Name { get; }

    /// <summary>Unit suffix, e.g. "RPM", "%", "°C", "ms". May be empty.</summary>
    string Unit { get; }

    /// <summary>False hides the metric (hardware absent, service stopped, …).</summary>
    bool IsAvailable { get; }

    /// <summary>Current value, or null when no value is available right now.</summary>
    double? Sample();
}

/// <summary>One plugin-provided command (a user-triggered action).</summary>
public interface IPluginCommand
{
    string Id { get; }

    /// <summary>Button/menu title, e.g. "Open in Process Explorer".</summary>
    string Title { get; }

    string Description { get; }

    /// <summary>Called before the command is offered/executed; keep it cheap.</summary>
    bool CanExecute(IPluginCommandContext context);

    /// <summary>Runs on the UI thread. Keep it short; use fire-and-forget for long work.</summary>
    void Execute(IPluginCommandContext context);
}

/// <summary>Context for command execution (what the user had selected when invoking it).</summary>
public interface IPluginCommandContext
{
    int? SelectedProcessId { get; }

    string? SelectedProcessName { get; }
}

/// <summary>Ready-made mutable implementation of <see cref="IPluginCommandContext"/>.</summary>
public sealed class PluginCommandContext : IPluginCommandContext
{
    public int? SelectedProcessId { get; init; }

    public string? SelectedProcessName { get; init; }

    /// <summary>Context with no process selection (used by the tray menu).</summary>
    public static PluginCommandContext Empty => new();
}
