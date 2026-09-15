using System.IO;
using Microsoft.Win32;
using WinSentinel.Models;

namespace WinSentinel.Services;

/// <summary>
/// Reads the four places Windows starts programs from:
///   HKCU/HKLM Run, HKCU/HKLM RunOnce, and the per-user / all-users Startup folders.
/// Enable state is read from Explorer's StartupApproved keys (the same data Task Manager's
/// Startup tab uses). Mutations are confined to per-user locations:
///   • Enable/disable: HKCU Run entries and per-user Startup-folder files (non-destructive).
///   • Remove: HKCU Run / RunOnce entries only.
///   • Add: HKCU Run.
/// HKLM entries are shown for visibility and are never modified.
/// </summary>
public sealed class StartupManager
{
    public readonly record struct Result(bool Ok, string Message);

    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ApprovedRunPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedFolderPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public List<StartupItem> GetStartupItems()
    {
        var items = new List<StartupItem>();
        var userApproved = ReadApproved(Registry.CurrentUser, ApprovedRunPath);
        var machineApproved = ReadApproved(Registry.LocalMachine, ApprovedRunPath);
        var userFolderApproved = ReadApproved(Registry.CurrentUser, ApprovedFolderPath);

        ReadRunKey(Registry.CurrentUser, "HKCU", RunPath, "Run", userApproved, items);
        ReadRunKey(Registry.CurrentUser, "HKCU", RunOncePath, "RunOnce", userApproved, items);
        ReadRunKey(Registry.LocalMachine, "HKLM", RunPath, "Run", machineApproved, items);
        ReadRunKey(Registry.LocalMachine, "HKLM", RunOncePath, "RunOnce", machineApproved, items);

        ReadStartupFolder(Environment.SpecialFolder.Startup, "Startup (user)", userFolderApproved, items);
        ReadStartupFolder(Environment.SpecialFolder.CommonStartup, "Startup (all users)", null, items);

        return items
            .OrderBy(i => i.Location, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------------------------------------------------------- read

    private static void ReadRunKey(
        RegistryKey root, string hive, string path, string source,
        Dictionary<string, bool>? approved, List<StartupItem> sink)
    {
        try
        {
            using var key = root.OpenSubKey(path, writable: false);
            if (key is null) return;

            foreach (var name in key.GetValueNames())
            {
                bool? enabled = approved is not null && approved.TryGetValue(name, out bool e) ? e : null;

                sink.Add(new StartupItem
                {
                    Name = name,
                    Command = key.GetValue(name)?.ToString() ?? string.Empty,
                    Location = hive,
                    Source = source,
                    Enabled = enabled ?? true,
                    ToggleKey = name,
                    CanToggle = hive == "HKCU" && source == "Run",
                    CanRemove = hive == "HKCU"
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Startup read {hive}\\{path}", ex);
        }
    }

    private void ReadStartupFolder(
        Environment.SpecialFolder folder, string label,
        Dictionary<string, bool>? approved, List<StartupItem> sink)
    {
        try
        {
            string path = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            foreach (var file in Directory.EnumerateFiles(path))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.StartsWith('.')) continue; // hidden / desktop.ini style artifacts
                string ext = Path.GetExtension(file);
                if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".url", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool? enabled = approved is not null && approved.TryGetValue(fileName, out bool e) ? e : null;

                sink.Add(new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = file,
                    Location = label,
                    Source = "Folder",
                    Enabled = enabled ?? true,
                    ToggleKey = fileName,
                    CanToggle = label == "Startup (user)",
                    CanRemove = false
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Startup folder read {label}", ex);
        }
    }

    /// <summary>Reads StartupApproved values. Disabled entries have an odd first byte (0x03/0x07…).</summary>
    private static Dictionary<string, bool> ReadApproved(RegistryKey root, string path)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = root.OpenSubKey(path, writable: false);
            if (key is null) return map;

            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is byte[] { Length: > 0 } data)
                    map[name] = (data[0] & 1) == 0;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"StartupApproved read {path}", ex);
        }
        return map;
    }

    // ---------------------------------------------------------------- mutate

    /// <summary>Enable/disable a per-user entry without deleting anything.</summary>
    public Result SetEnabled(StartupItem item, bool enable)
    {
        if (!item.CanToggle)
            return new Result(false, $"'{item.Name}' cannot be toggled (machine-wide or RunOnce entries are read-only here).");

        try
        {
            string approvedPath = item.Source == "Folder" ? ApprovedFolderPath : ApprovedRunPath;
            using var key = Registry.CurrentUser.CreateSubKey(approvedPath);
            key.SetValue(item.ToggleKey, ApprovalBlob(enable), RegistryValueKind.Binary);
            return new Result(true, $"{(enable ? "Enabled" : "Disabled")} startup entry '{item.Name}'.");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Could not update '{item.Name}': {ex.Message}");
        }
    }

    /// <summary>0x02 = enabled, 0x03 = disabled, remaining bytes are a (zeroed) timestamp.</summary>
    private static byte[] ApprovalBlob(bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        return data;
    }

    public Result Remove(StartupItem item)
    {
        if (!item.CanRemove)
            return new Result(false, $"'{item.Name}' is not a removable HKCU entry.");

        try
        {
            string path = item.Source == "RunOnce" ? RunOncePath : RunPath;
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            if (key is null) return new Result(false, "HKCU startup key not found.");
            if (key.GetValue(item.Name) is null) return new Result(false, $"'{item.Name}' is no longer present.");

            key.DeleteValue(item.Name, throwOnMissingValue: false);
            return new Result(true, $"Removed startup entry '{item.Name}'. The program itself is not uninstalled.");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Could not remove '{item.Name}': {ex.Message}");
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
