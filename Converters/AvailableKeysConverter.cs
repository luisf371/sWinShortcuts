using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;

namespace sWinShortcuts.Converters;

// Filters a provided list of keys and ensures the current selected key remains available.
public sealed class AvailableKeysConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2)
            return new List<Key>();

        if (values[0] is not IEnumerable<Key> availableKeys)
            return new List<Key>();

        // Handle UnsetValue or null
        if (values[1] == System.Windows.DependencyProperty.UnsetValue || values[1] is null)
            return availableKeys.ToList();

        if (values[1] is not Key currentKey)
            return availableKeys.ToList();

        var result = availableKeys.ToList();
        
        // Ensure the current key is present
        if (!result.Contains(currentKey))
        {
            result.Add(currentKey);
        }

        // Sort to maintain stable index positions (fixes UI alternating selection bug)
        result.Sort();

        return result;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

