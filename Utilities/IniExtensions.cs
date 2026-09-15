using System;
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
