using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace RAMSpeed.Converters;

public class BytesToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            return bytes switch
            {
                >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
                >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
                >= 1024L => $"{bytes / 1024.0:F0} KB",
                _ => $"{bytes} B"
            };
        }
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class PercentToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double percent)
        {
            // The parent container is sized dynamically; we use a relative approach
            // returning a pixel width based on a reference width.
            // The progress bar's parent width is unknown at convert-time,
            // so we return percent directly and let WPF handle it via MaxWidth binding.
            // For simplicity, cap at a reasonable max and scale.
            return Math.Max(0, Math.Min(percent, 100)) / 100.0 * 400;
        }
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

public class ThresholdConverter : IMultiValueConverter
{
    public double Threshold { get; set; } = 70;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length > 0 && values[0] is double val)
            return val >= Threshold;
        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class UsagePercentToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x2E, 0x8B, 0x57));
    private static readonly SolidColorBrush AmberBrush = new(Color.FromRgb(0xCC, 0x7A, 0x00));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xCC, 0x33, 0x33));

    static UsagePercentToBrushConverter()
    {
        GreenBrush.Freeze();
        AmberBrush.Freeze();
        RedBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double percent)
        {
            return percent switch
            {
                >= 85 => RedBrush,
                >= 60 => AmberBrush,
                _ => GreenBrush
            };
        }
        return GreenBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
