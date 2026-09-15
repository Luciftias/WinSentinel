using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinSentinel.Helpers;
using WinSentinel.ViewModels;

namespace WinSentinel.Views;

public partial class DashboardWindow : Window
{
    public DashboardWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += OnWindowStateChanged;

        SourceInitialized += (_, _) => ApplyNativeEffects();
        ThemeManager.Applied += OnThemeApplied;
        Closed += (_, _) => ThemeManager.Applied -= OnThemeApplied;
    }

    private DashboardViewModel? Vm => DataContext as DashboardViewModel;

    // ---------------------------------------------------------------- native chrome

    /// <summary>
    /// Applies the DWM treatment (rounded corners, immersive dark, optional Mica) and switches the
    /// window background to transparent when Mica is actually active so the backdrop shows through
    /// the layout gaps.
    /// </summary>
    private void ApplyNativeEffects()
    {
        try
        {
            bool dark = !ThemeManager.CurrentThemeIsLight;
            bool translucent = Vm?.TranslucentBackdrop ?? false;
            bool mica = WindowEffects.Apply(this, dark, translucent);

            Background = mica
                ? Brushes.Transparent
                : TryFindResource("BgBrush") as Brush ?? Brushes.Transparent;
        }
        catch
        {
            // Cosmetic only.
        }
    }

    private void OnThemeApplied(object? sender, EventArgs e)
    {
        if (IsVisible) ApplyNativeEffects();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // With WindowChrome the client area overflows the screen edges when maximized; inset it.
        RootGrid.Margin = WindowState == WindowState.Maximized
            ? new Thickness(
                SystemParameters.WindowResizeBorderThickness.Left + 1,
                SystemParameters.WindowResizeBorderThickness.Top + 1,
                SystemParameters.WindowResizeBorderThickness.Right + 1,
                SystemParameters.WindowResizeBorderThickness.Bottom + 1)
            : new Thickness(18);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- motion

    /// <summary>Fades/slides the page content on tab changes (when motion is allowed).</summary>
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!Motion.UseAnimations || !ReferenceEquals(e.Source, MainTabs)) return;

        try
        {
            if (MainTabs.Template.FindName("PART_SelectedContentHost", MainTabs) is not ContentPresenter host)
                return;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            host.Opacity = 0;
            var slide = new TranslateTransform(0, 6);
            host.RenderTransform = slide;

            host.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Motion.Fast) { EasingFunction = ease });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Motion.Fast) { EasingFunction = ease });
        }
        catch
        {
            // Never let polish break navigation.
        }
    }

    // ---------------------------------------------------------------- keyboard

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

    // ---------------------------------------------------------------- dialogs

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
