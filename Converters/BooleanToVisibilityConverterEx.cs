using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace sWinShortcuts.Converters;

public sealed class BooleanToVisibilityConverterEx : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            var flag = visibility == Visibility.Visible;
            return Invert ? !flag : flag;
        }

        return System.Windows.Data.Binding.DoNothing;
    }
}
