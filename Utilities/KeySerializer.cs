using System;
using System.Globalization;
using System.Windows.Input;

namespace sWinShortcuts.Utilities;

public static class KeySerializer
{
    public static string Serialize(Key? key)
    {
        return key?.ToString() ?? string.Empty;
    }

    public static Key? Deserialize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A bare single digit means the top-row key (D0..D9), NOT the numeric enum value: "5" would
        // otherwise parse via Enum.TryParse as the enum member 5 (Key.Clear). Handle it first.
        if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
        {
            if (Enum.TryParse<Key>($"D{trimmed[0]}", true, out var digitKey))
            {
                return digitKey;
            }
        }

        if (Enum.TryParse<Key>(trimmed, true, out var parsed))
        {
            return parsed;
        }

        if (trimmed.Length == 1)
        {
            var ch = trimmed[0];

            if (char.IsLetter(ch))
            {
                var keyName = char.ToUpperInvariant(ch).ToString(CultureInfo.InvariantCulture);
                if (Enum.TryParse<Key>(keyName, true, out parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }
}
