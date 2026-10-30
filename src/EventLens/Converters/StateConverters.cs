using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EventLens.ViewModels;
using Wpf.Ui.Controls;

namespace EventLens.Converters;

public static class Palette
{
    public static readonly Color Good = Color.FromRgb(0x4C, 0xC2, 0x6E);
    public static readonly Color Warn = Color.FromRgb(0xF7, 0xA4, 0x00);
    public static readonly Color Bad = Color.FromRgb(0xFF, 0x5A, 0x5F);
    public static readonly Color Neutral = Color.FromRgb(0x4C, 0xA8, 0xF5);

    public static Color For(CardState s) => s switch
    {
        CardState.Good => Good,
        CardState.Warn => Warn,
        CardState.Bad => Bad,
        _ => Neutral,
    };

    public static SolidColorBrush Brush(Color c, byte alpha = 0xFF)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}

/// <summary>CardState -> brush. ConverterParameter "soft" returns a translucent tint.</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = value switch
        {
            CardState s => s,
            string str when Enum.TryParse<CardState>(str, out var parsed) => parsed,
            _ => CardState.Neutral,
        };
        return Palette.Brush(Palette.For(state), parameter as string == "soft" ? (byte)0x2E : (byte)0xFF);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>Health score (0-100) -> brush.</summary>
public sealed class ScoreToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = value is int i ? i : value is double d ? d : 0;
        var c = v >= 85 ? Palette.Good : v >= 60 ? Palette.Warn : Palette.Bad;
        return Palette.Brush(c, parameter as string == "soft" ? (byte)0x2E : (byte)0xFF);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>Heatmap level 0-4 -> brush.</summary>
public sealed class HeatLevelToBrushConverter : IValueConverter
{
    private static readonly Brush[] Levels =
    [
        Palette.Brush(Color.FromRgb(0x80, 0x80, 0x80), 0x26),
        Palette.Brush(Palette.Warn, 0x55),
        Palette.Brush(Palette.Warn, 0xAA),
        Palette.Brush(Palette.Bad, 0xBB),
        Palette.Brush(Palette.Bad, 0xFF),
    ];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value switch
        {
            int i => i,
            string str when int.TryParse(str, out var p) => p,
            _ => 0,
        };
        return Levels[Math.Clamp(level, 0, 4)];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>Icon name (string) or event category -> SymbolRegular, with safe fallback.</summary>
public sealed class SymbolConverter : IValueConverter
{
    private static readonly Dictionary<string, string> CategoryIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Power"] = "Power24",
        ["Crash"] = "ErrorCircle24",
        ["Service"] = "Wrench24",
        ["Storage"] = "Storage24",
        ["Network"] = "Globe24",
        ["Security"] = "Shield24",
        ["Update"] = "ArrowSync24",
        ["Noise"] = "Info24",
    };

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var name = value as string ?? "";
        if (CategoryIcons.TryGetValue(name, out var mapped)) name = mapped;
        return Enum.TryParse<SymbolRegular>(name, out var sym) ? sym : SymbolRegular.Info24;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>0 -> Visible, anything else -> Collapsed (empty-state placeholders).</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>RestartKind -> brush (planned = blue, update = green, unexpected = orange, BSOD = red).</summary>
public sealed class RestartKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var c = value switch
        {
            EventLens.Services.RestartKind.Planned => Palette.Neutral,
            EventLens.Services.RestartKind.WindowsUpdate => Palette.Good,
            EventLens.Services.RestartKind.Unexpected => Palette.Warn,
            EventLens.Services.RestartKind.BlueScreen => Palette.Bad,
            _ => Color.FromRgb(0x8A, 0x8A, 0x8A),
        };
        return Palette.Brush(c, parameter as string == "soft" ? (byte)0x2E : (byte)0xFF);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
