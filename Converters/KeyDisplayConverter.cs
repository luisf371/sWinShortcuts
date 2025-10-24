using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;

namespace sWinShortcuts.Converters;

public sealed class KeyDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Key key)
        {
            return key.ToString();
        }

        return "None";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string text && Enum.TryParse<Key>(text, true, out var key))
        {
            return key;
        }

        return System.Windows.Data.Binding.DoNothing;
    }
}
