using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using sWinShortcuts.Models;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.Converters;

public sealed class AvailableMouseButtonsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2)
            return new List<MouseButton>();

        if (values[0] is not IEnumerable<MouseButton> availableButtons)
            return new List<MouseButton>();

        if (values[1] is not MouseButton currentButton)
            return availableButtons;

        // Include the current button in the list even if it's "used"
        var result = availableButtons.ToList();
        if (!result.Contains(currentButton))
        {
            result.Insert(0, currentButton);
        }

        return result;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}