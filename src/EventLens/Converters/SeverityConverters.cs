using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EventLens.Models;

namespace EventLens.Converters;

public sealed class SeverityToBrushConverter : IValueConverter
{
    private static readonly Brush Critical = Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)));
    private static readonly Brush Error = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)));
    private static readonly Brush Warning = Freeze(new SolidColorBrush(Color.FromRgb(0xF7, 0xA4, 0x00)));
    private static readonly Brush Info = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)));
    private static readonly Brush Verbose = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Severity.Critical => Critical,
        Severity.Error => Error,
        Severity.Warning => Warning,
        Severity.Verbose => Verbose,
        _ => Info,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
