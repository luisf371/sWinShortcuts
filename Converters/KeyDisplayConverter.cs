using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;

namespace sWinShortcuts.Converters;

public sealed class KeyDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        System.Diagnostics.Debug.WriteLine($"[DEBUG] KeyDisplayConverter.Convert called: value={value?.ToString() ?? "NULL"}, targetType={targetType.Name}");
        if (value is Key key)
        {
            if (key == Key.None)
            {
                return "None";
            }
            return key.ToString();
        }

        if (value is null)
        {
            return "None";
        }

        return "None";
    }

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        System.Diagnostics.Debug.WriteLine($"[DEBUG] KeyDisplayConverter.ConvertBack called: value={value?.ToString() ?? "NULL"}, targetType={targetType.Name}");
        if (value is string text)
        {
            if (text == "None")
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG] KeyDisplayConverter.ConvertBack returning Key.None");
                return Key.None;
            }
            
            if (Enum.TryParse<Key>(text, true, out var key))
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG] KeyDisplayConverter.ConvertBack returning parsed Key: {key}");
                return key;
            }
        }

        System.Diagnostics.Debug.WriteLine($"[DEBUG] KeyDisplayConverter.ConvertBack returning DoNothing");
        return System.Windows.Data.Binding.DoNothing;
    }
}
