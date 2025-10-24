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

        if (Enum.TryParse<Key>(trimmed, true, out var parsed))
        {
            return parsed;
        }

        if (trimmed.Length == 1)
        {
            var ch = trimmed[0];

            if (char.IsDigit(ch))
            {
                var keyName = $"D{ch}";
                if (Enum.TryParse<Key>(keyName, true, out parsed))
                {
                    return parsed;
                }
            }

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
