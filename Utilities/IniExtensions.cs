using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using sWinShortcuts.Models;

namespace sWinShortcuts.Utilities;

public static class IniExtensions
{
    public static bool TryGetBoolean(this IniDocument doc, string section, string key, out bool result)
    {
        result = default;
        return doc.TryGetSourceValue(section, key, out var value) && bool.TryParse(value, out result);
    }

    public static bool TryGetInt32(this IniDocument doc, string section, string key, out int result)
    {
        result = default;
        return doc.TryGetSourceValue(section, key, out var value) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    public static bool TryGetEnum<TEnum>(this IniDocument doc, string section, string key, out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;
        return doc.TryGetSourceValue(section, key, out var value) && value is not null && !value.Contains(',') &&
            Enum.TryParse(value, true, out result) && Enum.IsDefined(result);
    }

    public static bool TryGetKey(this IniDocument doc, string section, string key, out Key result)
    {
        result = Key.None;
        if (!doc.TryGetSourceValue(section, key, out var value) || string.IsNullOrWhiteSpace(value) || value.Contains(','))
        {
            return false;
        }

        if (value.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parsed = KeySerializer.Deserialize(value);
        if (!parsed.HasValue)
        {
            return false;
        }

        result = parsed.Value;
        return true;
    }

    public static bool GetBoolean(this IniDocument doc, string section, string key, bool defaultValue = false)
    {
        var value = doc.GetValue(section, key);
        if (value is null)
        {
            return defaultValue;
        }

        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        if (int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric != 0;
        }

        return defaultValue;
    }

    public static int GetInt32(this IniDocument doc, string section, string key, int defaultValue = 0)
    {
        var value = doc.GetValue(section, key);
        if (value is null)
        {
            return defaultValue;
        }

        return int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : defaultValue;
    }

    public static double GetDouble(this IniDocument doc, string section, string key, double defaultValue = 0)
    {
        var value = doc.GetValue(section, key);
        if (value is null)
        {
            return defaultValue;
        }

        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : defaultValue;
    }

    public static TEnum GetEnum<TEnum>(this IniDocument doc, string section, string key, TEnum defaultValue)
        where TEnum : struct
    {
        var value = doc.GetValue(section, key);
        if (value is null)
        {
            return defaultValue;
        }

        // Enum.TryParse accepts undefined numeric values ("7" -> (CapsLockMode)7); reject those so
        // a hand-edited/corrupt config degrades to the default instead of an undefined member.
        return Enum.TryParse(value, true, out TEnum result) && Enum.IsDefined(typeof(TEnum), result)
            ? result
            : defaultValue;
    }

    /// <summary>
    /// Reads side-agnostic modifier flags: a single name ("Control") as written by every build, or a
    /// "+"/","-joined combination ("Control+Alt"). Unknown tokens, numbers, empty tokens, and undefined
    /// bits fall back to <paramref name="defaultValue"/>.
    /// </summary>
    public static ModifierKeys GetModifierKeys(this IniDocument doc, string section, string key, ModifierKeys defaultValue)
    {
        var value = doc.GetValue(section, key);
        return value is not null && TryParseModifierKeys(value, out var result) ? result : defaultValue;
    }

    /// <summary>
    /// Writes one name for a single flag (byte-identical to the old SetEnum output) and "Control+Alt" style text,
    /// in Ctrl, Alt, Shift, Win order, for combinations. Older builds read a combination back as their default.
    /// </summary>
    public static void SetModifierKeys(this IniDocument doc, string section, string key, ModifierKeys value)
    {
        doc.SetValue(section, key, FormatModifierKeys(value));
    }

    internal static string FormatModifierKeys(ModifierKeys value)
    {
        if (value == ModifierKeys.None || (value & ~AllModifierKeys) != 0)
        {
            return value.ToString();
        }

        var parts = new List<string>(4);
        if ((value & ModifierKeys.Control) != 0) parts.Add(nameof(ModifierKeys.Control));
        if ((value & ModifierKeys.Alt) != 0) parts.Add(nameof(ModifierKeys.Alt));
        if ((value & ModifierKeys.Shift) != 0) parts.Add(nameof(ModifierKeys.Shift));
        if ((value & ModifierKeys.Windows) != 0) parts.Add(nameof(ModifierKeys.Windows));
        return string.Join("+", parts);
    }

    internal static bool TryParseModifierKeys(string value, out ModifierKeys result)
    {
        result = ModifierKeys.None;
        var tokens = value.Split(['+', ','], StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            ModifierKeys flag;
            if (token.Equals(nameof(ModifierKeys.None), StringComparison.OrdinalIgnoreCase))
            {
                // "None" is only valid on its own.
                if (tokens.Length != 1) return false;
                flag = ModifierKeys.None;
            }
            else if (token.Equals(nameof(ModifierKeys.Control), StringComparison.OrdinalIgnoreCase)) flag = ModifierKeys.Control;
            else if (token.Equals(nameof(ModifierKeys.Alt), StringComparison.OrdinalIgnoreCase)) flag = ModifierKeys.Alt;
            else if (token.Equals(nameof(ModifierKeys.Shift), StringComparison.OrdinalIgnoreCase)) flag = ModifierKeys.Shift;
            else if (token.Equals(nameof(ModifierKeys.Windows), StringComparison.OrdinalIgnoreCase)) flag = ModifierKeys.Windows;
            else return false;

            result |= flag;
        }

        return true;
    }

    private const ModifierKeys AllModifierKeys = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows;

    public static Key? GetKey(this IniDocument doc, string section, string key)
    {
        var value = doc.GetValue(section, key);
        return KeySerializer.Deserialize(value);
    }

    public static InputTrigger GetInputTrigger(this IniDocument doc, string section, string key, InputTrigger defaultValue)
    {
        var value = doc.GetValue(section, key);
        return value is null ? defaultValue : InputTriggerSerializer.Deserialize(value);
    }

    public static string GetString(this IniDocument doc, string section, string key, string defaultValue = "")
    {
        return doc.GetValue(section, key) ?? defaultValue;
    }

    public static void SetBoolean(this IniDocument doc, string section, string key, bool value)
    {
        doc.SetValue(section, key, value.ToString());
    }

    public static void SetInt32(this IniDocument doc, string section, string key, int value)
    {
        doc.SetValue(section, key, value.ToString(CultureInfo.InvariantCulture));
    }

    public static void SetDouble(this IniDocument doc, string section, string key, double value)
    {
        doc.SetValue(section, key, value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    public static void SetEnum<TEnum>(this IniDocument doc, string section, string key, TEnum value)
        where TEnum : struct
    {
        doc.SetValue(section, key, value.ToString());
    }

    public static void SetKey(this IniDocument doc, string section, string key, Key? value)
    {
        doc.SetValue(section, key, KeySerializer.Serialize(value));
    }

    public static void SetInputTrigger(this IniDocument doc, string section, string key, InputTrigger value)
    {
        doc.SetValue(section, key, InputTriggerSerializer.Serialize(value));
    }

    public static void SetString(this IniDocument doc, string section, string key, string value)
    {
        doc.SetValue(section, key, value);
    }
}
