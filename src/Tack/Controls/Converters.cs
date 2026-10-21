using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Tack.Controls;

/// <summary>true (or a positive number, or text that isn't empty) → Visible. Pass ConverterParameter="invert" for the opposite.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool v = value is true || (value is int i && i > 0) || (value is string s && s.Length > 0);
        if (parameter is "invert") v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A length times ConverterParameter ("0.55"): a share of another element's width, for a MaxWidth.</summary>
public sealed class Share : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double length && double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out double share)
            ? Math.Max(0, length * share)
            : DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
