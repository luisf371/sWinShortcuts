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

        // current selected SourceKey
        if (values[1] is not Key currentKey)
            return availableKeys.ToList();

        // Include the current key in the list even if it's filtered out
        var result = availableKeys.ToList();
        if (!result.Contains(currentKey))
        {
            result.Insert(0, currentKey);
        }

        return result;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

