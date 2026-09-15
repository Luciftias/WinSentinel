namespace WinSentinel.Models;

/// <summary>Rate formatting shared by the services and the UI (bytes/second, SI-ish units).</summary>
public static class ByteFormat
{
    public static string Rate(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1_073_741_824 => $"{bytesPerSecond / 1_073_741_824:0.0} GB/s",
        >= 1_048_576 => $"{bytesPerSecond / 1_048_576:0.0} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:0} KB/s",
        > 0 => $"{bytesPerSecond:0} B/s",
        _ => "0"
    };

    public static string Size(double bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824:0.00} GB",
        >= 1_048_576 => $"{bytes / 1_048_576:0.0} MB",
        >= 1024 => $"{bytes / 1024:0} KB",
        _ => $"{bytes:0} B"
    };
}

/// <summary>One physical/virtual network adapter row for the Network page.</summary>
public sealed class AdapterInfo
{
    public required uint Index { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; } = "Other";
    public string Status { get; init; } = "—";
    public double LinkSpeedBps { get; init; }
    public double DownBps { get; init; }
    public double UpBps { get; init; }
    public double TotalInBytes { get; init; }
    public double TotalOutBytes { get; init; }

    public string DownDisplay => ByteFormat.Rate(DownBps);
    public string UpDisplay => ByteFormat.Rate(UpBps);

    /// <summary>dwSpeed is bits/second.</summary>
    public string LinkDisplay => LinkSpeedBps switch
    {
        >= 1_000_000_000 => $"{LinkSpeedBps / 1_000_000_000:0.#} Gbps",
        >= 1_000_000 => $"{LinkSpeedBps / 1_000_000:0.#} Mbps",
        >= 1_000 => $"{LinkSpeedBps / 1_000:0} Kbps",
        > 0 => $"{LinkSpeedBps:0} bps",
        _ => "—"
    };

    public string TotalDisplay => $"{ByteFormat.Size(TotalInBytes)} / {ByteFormat.Size(TotalOutBytes)}";
}

/// <summary>One active TCP/UDP endpoint row for the Network page.</summary>
public sealed class ConnectionInfo
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>TCP4 | TCP6 | UDP4 | UDP6</summary>
    public string Protocol { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;
    public string Local { get; init; } = string.Empty;
    public string Remote { get; init; } = string.Empty;

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        query = query.Trim();
        return ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase)
               || Pid.ToString().Contains(query, StringComparison.Ordinal)
               || Remote.Contains(query, StringComparison.OrdinalIgnoreCase)
               || Local.Contains(query, StringComparison.OrdinalIgnoreCase)
               || Protocol.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
