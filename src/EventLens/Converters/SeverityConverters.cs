using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EventLens.Models;

namespace EventLens.Converters;

/// <summary>Severity -> brush. ConverterParameter "soft" returns a translucent tint (for pills/backgrounds).</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    private static Color ColorFor(Severity s) => s switch
    {
        Severity.Critical => Color.FromRgb(0xD1, 0x34, 0x38),
        Severity.Error => Color.FromRgb(0xFF, 0x5A, 0x5F),
        Severity.Warning => Color.FromRgb(0xF7, 0xA4, 0x00),
        Severity.Verbose => Color.FromRgb(0x8A, 0x8A, 0x8A),
        _ => Color.FromRgb(0x4C, 0xA8, 0xF5),
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var sev = value is Severity s ? s : Severity.Information;
        return Palette.Brush(ColorFor(sev), parameter as string == "soft" ? (byte)0x33 : (byte)0xFF);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
