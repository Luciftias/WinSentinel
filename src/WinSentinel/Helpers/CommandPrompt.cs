using System.Windows;
using WinSentinel.Views;

namespace WinSentinel.Helpers;

/// <summary>Shared prompt for plugin commands that require a parameter (target host etc.).</summary>
public static class CommandPrompt
{
    /// <summary>Returns the entered value, or null when the user cancelled.</summary>
    public static string? RequestParameter(string commandTitle, string label, string placeholder)
    {
        try
        {
            var dialog = new PluginParameterDialog(commandTitle, label, placeholder);
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
            if (owner is not null && !ReferenceEquals(owner, dialog)) dialog.Owner = owner;
            return dialog.ShowDialog() == true ? dialog.Value : null;
        }
        catch
        {
            return null;
        }
    }
}
