using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WinSentinel.Services;

namespace WinSentinel.Helpers;

/// <summary>
/// Applies the runtime theme: swaps the colour-token dictionary (Dark/Light/System) and injects
/// a computed accent dictionary, both picked up by the whole UI through DynamicResource.
/// "System" follows the Windows app theme and accent colour and re-applies when they change.
/// </summary>
public sealed class ThemeManager : IDisposable
{
    public static readonly string[] ThemeOptions = { "System", "Dark", "Light" };

    public static readonly string[] AccentOptions = { "Cyan", "Blue", "Violet", "Green", "Orange", "Rose", "System" };

    private static readonly Dictionary<string, Color> AccentPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cyan"] = Color.FromRgb(0x22, 0xD3, 0xEE),
        ["Blue"] = Color.FromRgb(0x3B, 0x82, 0xF6),
        ["Violet"] = Color.FromRgb(0x8B, 0x5C, 0xF6),
        ["Green"] = Color.FromRgb(0x3F, 0xB9, 0x50),
        ["Orange"] = Color.FromRgb(0xF0, 0x88, 0x3E),
        ["Rose"] = Color.FromRgb(0xF7, 0x78, 0xBA),
    };

    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private bool _hooked;

    /// <summary>Raised after a theme/accent re-skin so windows can re-apply native effects.</summary>
    public static event EventHandler? Applied;

    /// <summary>True when the light dictionary is currently active (used for native dark mode too).</summary>
    public static bool CurrentThemeIsLight { get; private set; }

    public ThemeManager(SettingsService settings)
    {
        _settings = settings;
        _dispatcher = Application.Current.Dispatcher;
    }

    /// <summary>Applies the current settings; safe to call repeatedly and from any thread.</summary>
    public void Apply()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(Apply);
            return;
        }

        var s = _settings.Current;
        bool light = s.Theme == "Light" || (s.Theme == "System" && IsSystemLightTheme());
        CurrentThemeIsLight = light;

        var merged = Application.Current.Resources.MergedDictionaries;

        // Assembly-qualified pack URI: deterministic regardless of which assembly is the entry
        // point (the app, a test host, or a future plugin host).
        string assemblyName = typeof(ThemeManager).Assembly.GetName().Name!;
        var themeDict = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/{assemblyName};component/Themes/{(light ? "Light" : "Dark")}.xaml",
                UriKind.Absolute)
        };
        if (merged.Count > 1) merged[1] = themeDict;
        else merged.Add(themeDict);

        var accentDict = BuildAccentDictionary(ResolveAccentColor(s.Accent));
        if (merged.Count > 2) merged[2] = accentDict;
        else merged.Add(accentDict);

        HookSystemEvents();

        try { Applied?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Logger.Error("Theme applied broadcast", ex); }
    }

    private static ResourceDictionary BuildAccentDictionary(Color accent)
    {
        var dict = new ResourceDictionary
        {
            ["AccentColor"] = accent,
            ["AccentBrush"] = MakeBrush(accent, 0xFF),
            ["AccentHoverBrush"] = MakeBrush(Mix(accent, Colors.White, 0.18), 0xFF),
            ["AccentPressedBrush"] = MakeBrush(Mix(accent, Colors.Black, 0.15), 0xFF),
            ["AccentFillBrush"] = MakeBrush(accent, 0x26),
            ["AccentFillSoftBrush"] = MakeBrush(accent, 0x14),
            ["AccentBorderBrush"] = MakeBrush(accent, 0x88),
            // Text/icon colour that sits on top of an accent-filled surface: keep >= 4.5:1.
            ["AccentTextBrush"] = MakeBrush(
                RelativeLuminance(accent) > 0.2 ? Color.FromRgb(0x0B, 0x12, 0x1A) : Colors.White, 0xFF),
        };
        return dict;
    }

    private static SolidColorBrush MakeBrush(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static double RelativeLuminance(Color c) =>
        (0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B));

    private static double Channel(byte v)
    {
        double s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static Color ResolveAccentColor(string name)
    {
        if (name.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            var system = ReadSystemAccent();
            if (system is not null) return system.Value;
            return AccentPresets["Cyan"];
        }
        return AccentPresets.TryGetValue(name, out var preset) ? preset : AccentPresets["Cyan"];
    }

    /// <summary>Reads the Windows accent colour (stored as 0xAABBGGRR under DWM\AccentColor).</summary>
    private static Color? ReadSystemAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int value)
            {
                var c = Color.FromArgb(
                    (byte)((value >> 24) & 0xFF),
                    (byte)(value & 0xFF),
                    (byte)((value >> 8) & 0xFF),
                    (byte)((value >> 16) & 0xFF));
                if (c.A == 0) c.A = 0xFF;
                return c;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Read system accent", ex);
        }
        return null;
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value) return value != 0;
        }
        catch (Exception ex)
        {
            Logger.Error("Read system theme", ex);
        }
        return false;
    }

    private void HookSystemEvents()
    {
        if (_hooked) return;
        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _hooked = true;
        }
        catch (Exception ex)
        {
            Logger.Error("Hook system theme events", ex);
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var s = _settings.Current;
        if (s.Theme.Equals("System", StringComparison.OrdinalIgnoreCase) ||
            s.Accent.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            Apply();
        }
    }

    public void Dispose()
    {
        if (!_hooked) return;
        try
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _hooked = false;
        }
        catch
        {
            // ignore
        }
    }
}
