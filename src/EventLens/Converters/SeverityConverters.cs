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

/// <summary>EventItem -> brush: grey when harmless (knowledge base or user), otherwise by severity. "soft" = tint.</summary>
public sealed class EventToBrushConverter : IValueConverter
{
    private static readonly SeverityToBrushConverter Sev = new();
    private static readonly Color Harmless = Color.FromRgb(0x8A, 0x8A, 0x8A);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var soft = parameter as string == "soft";
        return value switch
        {
            EventItem { IsHarmless: true } => Palette.Brush(Harmless, soft ? (byte)0x33 : (byte)0xFF),
            EventItem e => Sev.Convert(e.Severity, targetType, parameter, culture),
            EventRow { IsHarmless: true } => Palette.Brush(Harmless, soft ? (byte)0x33 : (byte)0xFF),
            EventRow r => Sev.Convert(r.Severity, targetType, parameter, culture),
            _ => Palette.Brush(Harmless, soft ? (byte)0x33 : (byte)0xFF),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

public sealed class PlusOneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i ? (i + 1).ToString(culture) + "." : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>FindingSeverity -> brush.</summary>
public sealed class FindingToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var c = value switch
        {
            EventLens.Core.Findings.FindingSeverity.Critical => Palette.Bad,
            EventLens.Core.Findings.FindingSeverity.Attention => Palette.Warn,
            _ => Palette.Neutral,
        };
        return Palette.Brush(c, parameter as string == "soft" ? (byte)0x2E : (byte)0xFF);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>DateTime -> "2 h ago".</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTime t ? EventLens.ViewModels.EventDetailViewModel.Relative(t) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>DateTime -> "Today 14:32" / "Yesterday 09:10" / "3 Nov 10:00".</summary>
public sealed class FriendlyTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime t) return "";
        if (t.Date == DateTime.Today) return "Today " + t.ToString("HH:mm", culture);
        if (t.Date == DateTime.Today.AddDays(-1)) return "Yesterday " + t.ToString("HH:mm", culture);
        return t.ToString(t.Year == DateTime.Today.Year ? "d MMM HH:mm" : "d MMM yyyy HH:mm", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
