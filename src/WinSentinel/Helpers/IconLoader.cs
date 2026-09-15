using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSentinel.Helpers;

/// <summary>
/// Extracts and caches small process icons for the process table. Icons are frozen bitmaps so they
/// can be produced on the snapshot thread and consumed by the UI without marshalling concerns.
/// </summary>
public static class IconLoader
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;

        lock (Gate)
        {
            if (Cache.TryGetValue(executablePath, out var cached)) return cached;
        }

        ImageSource? image = null;
        try
        {
            using Icon? icon = Icon.ExtractAssociatedIcon(executablePath);
            if (icon is not null)
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
                source.Freeze();
                image = source;
            }
        }
        catch
        {
            // Protected executables, network paths, or non-PE files — simply no icon.
        }

        lock (Gate)
        {
            if (Cache.Count > 512) Cache.Clear();
            Cache[executablePath] = image;
        }
        return image;
    }
}
