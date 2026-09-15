using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace WinSentinel.Converters;

/// <summary>Greys out protected (system-critical) rows in the process table.</summary>
public sealed class ProtectedToBrushConverter : IValueConverter
{
    private static readonly Brush Protected = new SolidColorBrush(Color.FromRgb(120, 128, 138));
    private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(230, 237, 243));

    static ProtectedToBrushConverter()
    {
        Protected.Freeze();
        Normal.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Protected : Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
