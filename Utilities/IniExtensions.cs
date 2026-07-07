using System;
using System.Globalization;
using System.Windows.Input;
using sWinShortcuts.Models;

namespace sWinShortcuts.Utilities;

public static class IniExtensions
{
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

    public static void SetString(this IniDocument doc, string section, string key, string value)
    {
        doc.SetValue(section, key, value);
    }
}
