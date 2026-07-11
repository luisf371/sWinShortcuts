using System;
using System.Globalization;
using System.Windows.Data;
using sWinShortcuts.Models;

namespace sWinShortcuts.Converters;

public sealed class InputTriggerDisplayConverter : IValueConverter
{
    private static readonly KeyDisplayConverter KeyConverter = new();
    private static readonly MouseButtonDisplayConverter MouseButtonConverter = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not InputTrigger trigger)
        {
            return "None";
        }

        return trigger.Kind switch
        {
            InputTriggerKind.None => "None",
            InputTriggerKind.KeyboardKey => KeyConverter.Convert(trigger.Key, targetType, parameter ?? string.Empty, culture) ?? "None",
            InputTriggerKind.MouseButton => MouseButtonConverter.Convert(trigger.MouseButton, targetType, parameter, culture) ?? "None",
            _ => "None"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
