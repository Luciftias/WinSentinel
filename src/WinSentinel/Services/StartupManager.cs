using Microsoft.Win32;

namespace WinSentinel.Services;

public sealed class StartupItem
{
    public string Name { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;

    /// <summary>"HKCU" or "HKLM".</summary>
    public string Location { get; init; } = "HKCU";

    /// <summary>Only HKCU entries are writable without altering the machine-wide hive.</summary>
    public bool Removable => Location == "HKCU";
}

/// <summary>
/// Reads/writes the classic Run keys. Reads from both HKCU and HKLM so the user sees the
/// full startup picture; writes/removes only touch HKCU to stay least-privilege and
/// reversible per-user. Every mutation is initiated from a confirmed UI action.
/// </summary>
public sealed class StartupManager
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public readonly record struct Result(bool Ok, string Message);

    public List<StartupItem> GetStartupItems()
    {
        var items = new List<StartupItem>();
        ReadHive(Registry.CurrentUser, "HKCU", items);
        ReadHive(Registry.LocalMachine, "HKLM", items);
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ReadHive(RegistryKey root, string label, List<StartupItem> sink)
    {
        try
        {
            using var key = root.OpenSubKey(RunPath, writable: false);
            if (key is null) return;

            foreach (var name in key.GetValueNames())
            {
                sink.Add(new StartupItem
                {
                    Name = name,
                    Command = key.GetValue(name)?.ToString() ?? string.Empty,
                    Location = label
                });
            }
        }
        catch
        {
            // Access denied on HKLM without elevation, etc. — silently skip.
        }
    }

    public Result Remove(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
            if (key is null) return new Result(false, "HKCU Run key not found.");
            if (key.GetValue(name) is null) return new Result(false, $"'{name}' is not an HKCU startup entry.");

            key.DeleteValue(name, throwOnMissingValue: false);
            return new Result(true, $"Removed startup entry '{name}'.");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Could not remove '{name}': {ex.Message}");
        }
    }

    public Result Add(string name, string command)
    {
        if (string.IsNullOrWhiteSpace(name)) return new Result(false, "Name is required.");
        if (string.IsNullOrWhiteSpace(command)) return new Result(false, "Command/path is required.");

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunPath);
            key.SetValue(name, command, RegistryValueKind.String);
            return new Result(true, $"Added startup entry '{name}'.");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Could not add '{name}': {ex.Message}");
        }
    }
}
