using System.Windows;
using System.Windows.Input;

namespace WinSentinel.Views;

public partial class BalloonWindow : Window
{
    /// <summary>Raised on double-click; the App opens the dashboard.</summary>
    public event Action? OpenDashboardRequested;

    public BalloonWindow()
    {
        InitializeComponent();
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
}
