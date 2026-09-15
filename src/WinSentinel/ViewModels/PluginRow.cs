using System.Collections.ObjectModel;

namespace WinSentinel.ViewModels;

/// <summary>One row per discovered plugin for the Settings → Plugins card.</summary>
public sealed class PluginRow : ViewModelBase
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public required string Description { get; init; }
    public required string Status { get; init; }
    public string? Error { get; init; }
    public string SourcePath { get; init; } = string.Empty;

    public bool IsError => Status != "Loaded";

    public string StatusDisplay => string.IsNullOrWhiteSpace(Error) ? Status : $"{Status}: {Error}";

    public ObservableCollection<PluginCommandRow> Commands { get; } = new();
}

/// <summary>A single plugin-registered command with its pre-wired run command.</summary>
public sealed class PluginCommandRow
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required RelayCommand RunCommand { get; init; }
}
