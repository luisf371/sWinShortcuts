using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Input;

namespace sWinShortcuts.Converters;

public sealed partial class KeyDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Key key ? ToDisplayText(key) : "None";
    }

    // Shared with view models that compose key names into row summaries and shortcut text.
    public static string ToDisplayText(Key key)
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
            case Key.OemBackslash: return "\\ (102)"; // the extra ISO key beside Left Shift
            // Key names print the first alias of a shared value (Return, Capital, Next, Snapshot); use keycap names.
            case Key.Enter: return "Enter";
            case Key.Back: return "Backspace";
            case Key.CapsLock: return "Caps Lock";
            case Key.PageUp: return "Page Up";
            case Key.PageDown: return "Page Down";
            case Key.NumLock: return "Num Lock";
            case Key.PrintScreen: return "Print Screen";
            case Key.Scroll: return "Scroll Lock";
            case Key.Apps: return "Menu";
            // Sided modifiers read the same alone and as prefixes (LCtrl, LCtrl+S).
            case Key.LeftCtrl: return "LCtrl";
            case Key.RightCtrl: return "RCtrl";
            case Key.LeftAlt: return "LAlt";
            case Key.RightAlt: return "RAlt";
            case Key.LeftShift: return "LShift";
            case Key.RightShift: return "RShift";
            case Key.LWin: return "LWin";
            case Key.RWin: return "RWin";
            case Key.Multiply: return "Num *";
            case Key.Add: return "Num +";
            case Key.Subtract: return "Num -";
            case Key.Divide: return "Num /";
            case Key.Decimal: return "Num .";
        }
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return "Num " + (char)('0' + (key - Key.NumPad0));
        }

        // PageUp -> Page Up, VolumeUp -> Volume Up; F13 stays whole.
        return WordStart().Replace(key.ToString(), " ");
    }

    [GeneratedRegex("(?<=[a-z])(?=[A-Z0-9])")]
    private static partial Regex WordStart();

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string text)
        {
            if (text == "None")
            {
                return Key.None;
            }

            if (text.Length == 1 && char.IsDigit(text[0]))
            {
                var digitKey = (Key)((int)Key.D0 + (text[0] - '0'));
                return digitKey;
            }

            if (Enum.TryParse<Key>(text, true, out var key))
            {
                return key;
            }
        }

        return System.Windows.Data.Binding.DoNothing;
    }
}
