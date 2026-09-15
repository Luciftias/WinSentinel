namespace WinSentinel.Services;

/// <summary>
/// Guard-rail denylist. Terminating, suspending or re-prioritising any of these will destabilise
/// or bug-check Windows, so WinSentinel refuses such operations regardless of UI state.
/// Matching is case-insensitive and ignores a trailing ".exe".
///
/// The list mirrors the category of processes Microsoft's own Task Manager greys out for
/// "Efficiency mode" (core Windows processes) plus the classic critical list used by
/// Process Explorer's "cannot terminate" set. Expansion beyond this list is deliberately
/// conservative: everything else remains user-controllable, always behind a confirmation.
/// </summary>
public static class ProtectedProcesses
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // Kernel / session pseudo-processes
        "System", "Idle", "Secure System", "Registry", "Memory Compression",
        // Critical user-mode infrastructure
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "fontdrvhost", "dwm", "sihost", "ctfmon", "audiodg",
        // Security / platform services
        "MsMpEng", "SecurityHealthService", "SecurityHealthSystray",
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
