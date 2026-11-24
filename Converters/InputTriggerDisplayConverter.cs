using System;
using System.Globalization;
using System.Windows.Data;
using sWinShortcuts.Models;

namespace sWinShortcuts.Converters;

public sealed class InputTriggerDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is InputTrigger trigger ? ToDisplayText(trigger) : "None";
    }

    // Shared with KeyChord, which prefixes the modifiers.
    public static string ToDisplayText(InputTrigger trigger)
    {
        return trigger.Kind switch
        {
            InputTriggerKind.None => "None",
            InputTriggerKind.KeyboardKey => KeyDisplayConverter.ToDisplayText(trigger.Key),
            InputTriggerKind.MouseButton => MouseButtonDisplayConverter.ToDisplayText(trigger.MouseButton),
            InputTriggerKind.MouseWheel when trigger.Wheel == MouseWheelDirection.Up => "Wheel Up",
            InputTriggerKind.MouseWheel when trigger.Wheel == MouseWheelDirection.Down => "Wheel Down",
            _ => "None"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
