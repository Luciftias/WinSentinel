using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinSentinel.Helpers;

/// <summary>
/// Premium window treatments via DWM: rounded corners (Win11), immersive dark title/border colours
/// that follow the app theme, and an optional Mica backdrop with a safe fallback chain:
///   Mica (Win11 22H2+) → plain themed background everywhere else.
/// Every call is best-effort — cosmetic effects must never take the app down.
/// </summary>
public static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmWindowCornerRound = 2;
    private const int DwmSystemBackdropMica = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    /// <summary>True on Windows 11 22H2+, where DWMWA_SYSTEMBACKDROP_TYPE is supported.</summary>
    public static bool SupportsMica => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    /// <summary>
    /// Applies corner rounding + immersive dark mode, and (when requested and supported) the Mica
    /// backdrop via an extended frame. Returns true when Mica is actually active, so the caller can
    /// switch the WPF background to transparent.
    /// </summary>
    public static bool Apply(Window window, bool dark, bool translucent)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            int round = DwmWindowCornerRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));

            int darkValue = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkValue, sizeof(int));

            if (translucent && SupportsMica)
            {
                int backdrop = DwmSystemBackdropMica;
                if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0)
                {
                    var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
                    return true;
                }
            }

            var reset = new MARGINS { Left = 0, Right = 0, Top = 0, Bottom = 0 };
            _ = DwmExtendFrameIntoClientArea(hwnd, ref reset);
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Lightweight treatment for modal dialogs: rounding + dark caption, no backdrop.</summary>
    public static void ApplyDialog(Window window, bool dark) => Apply(window, dark, translucent: false);
}
