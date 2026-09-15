namespace WinSentinel.Services;

/// <summary>
/// Guard-rail denylist. Terminating or re-prioritising any of these will destabilise or
/// bug-check Windows, so WinSentinel refuses such operations regardless of UI state.
/// Matching is case-insensitive and ignores a trailing ".exe".
/// </summary>
public static class ProtectedProcesses
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // Kernel / session pseudo-processes
        "System", "Idle", "Secure System", "Registry", "Memory Compression",
        // Critical user-mode infrastructure
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "fontdrvhost", "dwm", "sihost", "ctfmon",
        // Shell + self
        "explorer", "WinSentinel"
    };

    public static bool IsProtected(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return true;
        string normalized = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return Names.Contains(normalized);
    }
}
