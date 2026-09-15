using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using WinSentinel.ViewModels;

namespace WinSentinel.Views;

public partial class DashboardWindow : Window
{
    public DashboardWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private DashboardViewModel? Vm => DataContext as DashboardViewModel;

    /// <summary>
    /// Window-level shortcuts. Ctrl+F focuses search; Delete ends the selected task and Alt+E
    /// toggles efficiency mode — but only when focus is NOT inside a text box, so typing in the
    /// search box can never kill a process by accident.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            Vm?.RefreshProcessesCommand.Execute(null);
            e.Handled = true;
            return;
        }

        bool typing = Keyboard.FocusedElement is System.Windows.Controls.TextBox
                      or System.Windows.Controls.Primitives.TextBoxBase;
        if (typing) return;

        if (e.Key == Key.Delete)
        {
            Vm?.KillSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.E && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            Vm?.ToggleEcoSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnAffinityClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        var target = Vm.SelectedProcess;
        if (target is null)
        {
            MessageBox.Show("Select a process first.", "WinSentinel",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (target.IsProtected)
        {
            MessageBox.Show($"'{target.Name}' is a protected system process; affinity cannot be changed.",
                "WinSentinel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long current = Vm.GetSelectedAffinity();
        var dialog = new AffinityWindow(target.Name, current) { Owner = this };
        if (dialog.ShowDialog() == true)
            Vm.ApplyAffinity(dialog.Mask);
    }

    private void OnAddStartupClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        string name = StartupNameBox.Text.Trim();
        string command = StartupCommandBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command))
        {
            MessageBox.Show("Enter both a name and a command/path.", "WinSentinel",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Vm.AddStartup(name, command);
        StartupNameBox.Clear();
        StartupCommandBox.Clear();
    }
}
