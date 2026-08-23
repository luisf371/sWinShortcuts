using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;

namespace sWinShortcuts.Converters;

public sealed class AvailableTriggerKeysConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2)
            return new List<Key>();

        if (values[0] is not IEnumerable<Key> availableKeys)
            return new List<Key>();

        if (values[1] is not Key currentKey)
            return availableKeys;

        // Include the current trigger key in the list even if it's "used"
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
