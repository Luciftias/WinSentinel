using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using WinSentinel.Helpers;
using WinSentinel.ViewModels;

namespace WinSentinel.Views;

/// <summary>
/// The floating balloon: frameless, translucent, always-on-top, draggable. Left-drag moves it
/// (position is persisted), double-click opens the dashboard, right-click offers quick actions.
/// </summary>
public partial class BalloonWindow : Window
{
    /// <summary>Raised on double-click; the App opens the dashboard.</summary>
    public event Action? OpenDashboardRequested;

    /// <summary>Raised by the context menu; the App navigates the dashboard to Settings.</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised by the context menu "Trim Memory Now".</summary>
    public event Action? TrimRequested;

    /// <summary>Raised by the context menu "Hide Balloon".</summary>
    public event Action? HideRequested;

    /// <summary>Raised by the context menu "Exit".</summary>
    public event Action? ExitRequested;

    public BalloonWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => (DataContext as BalloonViewModel)?.Dispose();
    }

    private BalloonViewModel? Vm => DataContext as BalloonViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        // Restore the remembered position, clamped into the current virtual screen so the
        // balloon can never end up off-screen after a monitor change.
        double minX = SystemParameters.VirtualScreenLeft;
        double minY = SystemParameters.VirtualScreenTop;
        double maxX = Math.Max(minX, minX + SystemParameters.VirtualScreenWidth - ActualWidth);
        double maxY = Math.Max(minY, minY + SystemParameters.VirtualScreenHeight - ActualHeight);

        Left = Math.Clamp(Vm.PositionX, minX, maxX);
        Top = Math.Clamp(Vm.PositionY, minY, maxY);

        // Gentle fade-in for a premium feel (skipped when motion is disabled).
        if (Motion.UseAnimations)
        {
            RootCard.Opacity = 0;
            RootCard.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, Motion.Medium) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            OpenDashboardRequested?.Invoke();
            return;
        }

        try { DragMove(); } catch { /* DragMove throws if the button was already released */ }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        Vm?.SavePosition(Left, Top);
    }

    private void OnOpenDashboardClick(object sender, RoutedEventArgs e) => OpenDashboardRequested?.Invoke();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnTrimClick(object sender, RoutedEventArgs e) => TrimRequested?.Invoke();

    private void OnHideClick(object sender, RoutedEventArgs e) => HideRequested?.Invoke();

    private void OnExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
}
