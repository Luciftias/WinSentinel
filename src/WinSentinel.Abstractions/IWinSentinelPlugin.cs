namespace WinSentinel.Abstractions;

/// <summary>
/// Entry point of a WinSentinel plugin. Exactly one public class per plugin DLL implements this
/// interface; the host discovers it automatically, calls <see cref="Initialize"/> once, samples
/// registered metrics on a background cadence and exposes registered commands to the UI.
///
/// <para><b>Security:</b> plugins run in-process with the same privileges as WinSentinel
/// (administrator). Only install plugins you trust.</para>
/// </summary>
public interface IWinSentinelPlugin
{
    /// <summary>Unique, reverse-dns style identifier, e.g. <c>contoso.fanspeed</c>.</summary>
    string Id { get; }

    /// <summary>Human-readable display name.</summary>
    string Name { get; }

    /// <summary>Plugin version (informational).</summary>
    string Version { get; }

    string Author { get; }

    string Description { get; }

    /// <summary>Called once when the plugin is loaded. Register metrics/commands here.</summary>
    void Initialize(IPluginContext context);

    /// <summary>Called when the host shuts down or reloads plugins. Release resources here.</summary>
    void Shutdown();
}
