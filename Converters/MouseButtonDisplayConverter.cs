using System;
using System.Globalization;
using System.Windows.Data;
using sWinShortcuts.Models;

namespace sWinShortcuts.Converters;

public sealed class MouseButtonDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MouseButton button)
        {
            return value?.ToString() ?? string.Empty;
        }

        return button switch
        {
            MouseButton.Left => "Left Mouse Button",
            MouseButton.Right => "Right Mouse Button",
            MouseButton.Middle => "Middle Mouse Button",
            MouseButton.XButton1 => "Mouse Button 4",
            MouseButton.XButton2 => "Mouse Button 5",
            _ => button.ToString()
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}