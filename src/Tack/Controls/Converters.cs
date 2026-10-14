using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Tack.Controls;

/// <summary>true → Visible. Pass ConverterParameter="invert" for the opposite.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool v = value is true || (value is int i && i > 0);
        if (parameter is "invert") v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
