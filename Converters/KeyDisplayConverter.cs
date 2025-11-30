using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;

namespace sWinShortcuts.Converters;

public sealed class KeyDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // System.Diagnostics.Debug.WriteLine($"[DEBUG] KeyDisplayConverter.Convert called: value={value?.ToString() ?? "NULL"}, targetType={targetType.Name}");
        
        if (value is Key key)
        {
            if (key == Key.None)
            {
                return "None";
            }
            if (key is >= Key.D0 and <= Key.D9)
            {
                var digitIndex = (int)key - (int)Key.D0;
                return ((char)('0' + digitIndex)).ToString();
            }

            // Friendly symbols for OEM keys (US layout defaults)
            switch (key)
            {
                case Key.Oem3: return "`";             // ` ~
                case Key.OemMinus: return "-";         // - _
                case Key.OemPlus: return "=";          // = +
                case Key.OemOpenBrackets: return "[";  // [ {
                case Key.OemCloseBrackets: return "]"; // ] }
                case Key.OemPipe: return "\\";         // \ |
                case Key.OemSemicolon: return ";";     // ; :
                case Key.OemQuotes: return "'";        // ' "
                case Key.OemComma: return ",";         // , <
                case Key.OemPeriod: return ".";        // . >
                case Key.OemQuestion: return "/";      // / ?
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
        // System.Diagnostics.Debug.WriteLine($"[DEBUG] KeyDisplayConverter.ConvertBack called: value={value?.ToString() ?? "NULL"}, targetType={targetType.Name}");
        if (value is string text)
        {
            if (text == "None")
            {
                // System.Diagnostics.Debug.WriteLine($"[DEBUG] KeyDisplayConverter.ConvertBack returning Key.None");
                return Key.None;
            }

            if (text.Length == 1 && char.IsDigit(text[0]))
            {
                var digitKey = (Key)((int)Key.D0 + (text[0] - '0'));
                // System.Diagnostics.Debug.WriteLine($"[DEBUG] KeyDisplayConverter.ConvertBack returning digit Key: {digitKey}");
                return digitKey;
            }
            
            if (Enum.TryParse<Key>(text, true, out var key))
            {
                // System.Diagnostics.Debug.WriteLine($"[DEBUG] KeyDisplayConverter.ConvertBack returning parsed Key: {key}");
                return key;
            }
        }

        // System.Diagnostics.Debug.WriteLine($"[DEBUG] KeyDisplayConverter.ConvertBack returning DoNothing");
        return System.Windows.Data.Binding.DoNothing;
    }
}