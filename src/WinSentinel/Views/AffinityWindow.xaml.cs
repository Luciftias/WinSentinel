using System.Windows;
using System.Windows.Controls;

namespace WinSentinel.Views;

/// <summary>Modal dialog that edits a process's CPU-affinity bitmask, one checkbox per core.</summary>
public partial class AffinityWindow : Window
{
    private readonly List<CheckBox> _boxes = new();

    /// <summary>The resulting affinity mask after the user clicks Apply.</summary>
    public long Mask { get; private set; }

    public AffinityWindow(string processName, long currentMask)
    {
        InitializeComponent();
        HeaderText.Text = $"CPU affinity — {processName}";

        int cores = Environment.ProcessorCount;
        for (int i = 0; i < cores; i++)
        {
            var box = new CheckBox
            {
                Content = $"CPU {i}",
                Margin = new Thickness(8, 5, 8, 5),
                MinWidth = 70,
                IsChecked = (currentMask & (1L << i)) != 0
            };
            _boxes.Add(box);
            CoresPanel.Children.Add(box);
        }
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var b in _boxes) b.IsChecked = true;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        foreach (var b in _boxes) b.IsChecked = false;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        long mask = 0;
        for (int i = 0; i < _boxes.Count; i++)
            if (_boxes[i].IsChecked == true) mask |= (1L << i);

        if (mask == 0)
        {
            MessageBox.Show("Select at least one CPU core.", "CPU Affinity",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Mask = mask;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
