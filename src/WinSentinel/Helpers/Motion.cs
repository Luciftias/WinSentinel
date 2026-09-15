using System.Runtime.InteropServices;

namespace WinSentinel.Helpers;

/// <summary>
/// Central motion switch. Animations are only ever used for polish, so they honour both the user's
/// WinSentinel preference and the Windows "Animation effects" accessibility setting
/// (SPI_GETCLIENTAREAANIMATION). Nothing functional depends on an animation completing.
/// </summary>
public static class Motion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref int value, uint winIni);

    /// <summary>User preference from WinSentinel settings (default true).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Reads the Windows accessibility setting each time — it can change while running.</summary>
    public static bool SystemAnimationsEnabled
    {
        get
        {
            try
            {
                int enabled = 1;
                return !SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0) || enabled != 0;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>True when animations should be used at all.</summary>
    public static bool UseAnimations => Enabled && SystemAnimationsEnabled;

    /// <summary>Short, tasteful duration used across the app.</summary>
    public static TimeSpan Fast => TimeSpan.FromMilliseconds(140);

    public static TimeSpan Medium => TimeSpan.FromMilliseconds(220);
}
