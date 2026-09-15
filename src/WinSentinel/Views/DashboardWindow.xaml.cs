using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WinSentinel.ViewModels;

namespace WinSentinel.Views;

public partial class DashboardWindow : Window
{
    private static readonly ProcessPriorityClass[] PriorityChoices =
    {
        ProcessPriorityClass.Idle,
        ProcessPriorityClass.BelowNormal,
        ProcessPriorityClass.Normal,
        ProcessPriorityClass.AboveNormal,
        ProcessPriorityClass.High,
        ProcessPriorityClass.RealTime
    };

    public DashboardWindow()
    {
        InitializeComponent();

        foreach (var p in PriorityChoices) PriorityCombo.Items.Add(p.ToString());
        PriorityCombo.SelectedItem = ProcessPriorityClass.Normal.ToString();
    }

    private DashboardViewModel? Vm => DataContext as DashboardViewModel;

    private void OnApplyPriorityClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (Vm.SelectedProcess is null)
        {
            MessageBox.Show("Select a process first.", "WinSentinel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (PriorityCombo.SelectedItem is string name &&
            Enum.TryParse<ProcessPriorityClass>(name, out var priorityClass))
        {
            Vm.ApplyPriority(priorityClass);
        }
    }

    private void OnAffinityClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var target = Vm.SelectedProcess;
        if (target is null)
        {
            MessageBox.Show("Select a process first.", "WinSentinel", MessageBoxButton.OK, MessageBoxImage.Information);
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
