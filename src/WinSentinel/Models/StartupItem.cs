namespace WinSentinel.Models;

/// <summary>
/// A startup entry. Covers the four places Windows actually runs things from at sign-in:
/// HKCU/HKLM <c>Run</c>, HKCU/HKLM <c>RunOnce</c> and the per-user / all-users Startup folders.
/// Enable state comes from Explorer's <c>StartupApproved</c> keys where present; HKLM entries
/// are surfaced for visibility but are never modified.
/// </summary>
public sealed class StartupItem
{
    public string Name { get; init; } = string.Empty;

    public string Command { get; init; } = string.Empty;

    /// <summary>HKCU | HKLM | Startup (user) | Startup (all users)</summary>
    public string Location { get; init; } = "HKCU";

    /// <summary>Run | RunOnce | Folder</summary>
    public string Source { get; init; } = "Run";

    /// <summary>Enabled state from StartupApproved, or null when Windows has no approval data.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Name used when writing the StartupApproved value (file name with extension for folders).</summary>
    public string ToggleKey { get; init; } = string.Empty;

    /// <summary>True when the entry lives in a per-user hive/folder and can be changed by WinSentinel.</summary>
    public bool CanToggle { get; init; }

    /// <summary>True when the entry can be removed (HKCU Run/RunOnce only — folders are disable-only).</summary>
    public bool CanRemove { get; init; }

    public string LocationDisplay => Source == "Folder" ? Location : $"{Location} · {Source}";
}
