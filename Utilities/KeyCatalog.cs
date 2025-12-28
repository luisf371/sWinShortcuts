using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace sWinShortcuts.Utilities;

public static class KeyCatalog
{
    private static readonly IReadOnlyList<Key> NumpadKeys = Enumerable.Range((int)Key.NumPad0, 10).Select(i => (Key)i).ToArray();
    private static readonly IReadOnlyList<Key> CommonKeys = BuildCommonKeys();

    public static IReadOnlyList<Key> GetCommonKeys() => CommonKeys;

    public static IReadOnlyList<Key> GetNumpadKeys() => NumpadKeys;

    public static IEnumerable<Key> SortKeys(IEnumerable<Key> keys)
    {
        return keys.OrderBy(GetSortPriority).ThenBy(k => k.ToString());
    }

    private static int GetSortPriority(Key key)
    {
        // Alphabet A-Z: Priority 0
        if (key >= Key.A && key <= Key.Z) return 0;
        
        // Numbers 0-9: Priority 1
        if (key >= Key.D0 && key <= Key.D9) return 1;
        
        // Function keys F1-F12: Priority 2
        if (key >= Key.F1 && key <= Key.F12) return 2;
        
        // Numpad: Priority 3
        if (key >= Key.NumPad0 && key <= Key.Divide) return 3;

        // None: Priority 99 (bottom)
        if (key == Key.None) return 99;

        // Everything else: Priority 10
        return 10;
    }

    private static IReadOnlyList<Key> BuildCommonKeys()
    {
        var keys = new List<Key>();

        // Letters A-Z
        keys.AddRange(Enumerable.Range((int)Key.A, 26).Select(i => (Key)i));

        // Number keys 0-9
        keys.AddRange(Enumerable.Range((int)Key.D0, 10).Select(i => (Key)i));

        // Function keys F1-F12
        keys.AddRange(Enumerable.Range((int)Key.F1, 12).Select(i => (Key)i));

        // Control keys
        keys.AddRange(new[]
        {
            Key.LeftShift, Key.RightShift,
            Key.LeftCtrl, Key.RightCtrl,
            Key.LeftAlt, Key.RightAlt,
            Key.Space, Key.Tab, Key.Escape,
            Key.Enter, Key.Back, Key.Delete,
            Key.Insert, Key.Home, Key.End,
            Key.PageUp, Key.PageDown,
            Key.Left, Key.Up, Key.Right, Key.Down,
            Key.CapsLock, Key.Apps
        });

        // Numpad keys
        keys.AddRange(GetNumpadKeys());
        keys.Add(Key.Add);
        keys.Add(Key.Subtract);
        keys.Add(Key.Multiply);
        keys.Add(Key.Divide);
        keys.Add(Key.Decimal);

        // OEM and punctuation keys (for games that restrict certain keys)
        keys.AddRange(new[]
        {
            Key.Oem3,            // ` ~
            Key.OemMinus,        // - _
            Key.OemPlus,         // = +
            Key.OemOpenBrackets, // [ {
            Key.OemCloseBrackets,// ] }
            Key.OemPipe,         // \ |
            Key.OemSemicolon,    // ; :
            Key.OemQuotes,       // ' "
            Key.OemComma,        // , <
            Key.OemPeriod,       // . >
            Key.OemQuestion      // / ?
        });

        return SortKeys(keys.Distinct()).ToArray();
    }
}
