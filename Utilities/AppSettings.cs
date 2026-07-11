using System;
using System.IO;
using System.Windows.Input;

namespace sWinShortcuts.Utilities;

public static class AppSettings
{
    public const string ColorToggleKeyName = "ColorToggleKey";

    public static string GetRootDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "sWinShortcuts");
    }

    public static string GetSettingsPath()
        => Path.Combine(GetRootDirectory(), "sWinShortcuts.ini");


    /// <summary>
    /// Reads the app-level color toggle key, falling back to the legacy Color.ini value during migration.
    /// An explicit [App] value of None wins over the legacy value so clearing the setting stays cleared.
    /// </summary>
    public static Key? LoadColorToggleKey(string settingsPath)
    {
        var document = IniDocument.Load(settingsPath);
        var stored = document.GetValue("App", ColorToggleKeyName);
        if (stored is not null)
        {
            return KeySerializer.Deserialize(stored);
        }

        return IniDocument.Load(GetLegacyColorProfilePath(settingsPath))
            .GetKey("Color", "ToggleKey");
    }

    /// <summary>
    /// Copies a legacy [Color] ToggleKey into [App] once. The old value remains readable until the next
    /// normal Color.ini save, while the explicit app-level key prevents it from resurfacing after clear.
    /// </summary>
    public static void MigrateLegacyColorToggleKey(string settingsPath)
    {
        var document = IniDocument.Load(settingsPath);
        if (document.GetValue("App", ColorToggleKeyName) is not null)
        {
            return;
        }

        var legacyKey = IniDocument.Load(GetLegacyColorProfilePath(settingsPath))
            .GetKey("Color", "ToggleKey");
        if (!legacyKey.HasValue)
        {
            return;
        }

        SetColorToggleKey(document, legacyKey);
        document.Save(settingsPath);
    }

    public static void SetColorToggleKey(IniDocument document, Key? key)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Persist None instead of removing the key. That explicit marker prevents a legacy Color.ini
        // setting from being selected again on a later launch after the user intentionally clears it.
        var serialized = !key.HasValue || key.Value == Key.None
            ? "None"
            : KeySerializer.Serialize(key);
        document.SetValue("App", ColorToggleKeyName, serialized);
    }

    private static string GetLegacyColorProfilePath(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        return string.IsNullOrWhiteSpace(directory)
            ? "Color.ini"
            : Path.Combine(directory, "Color.ini");
    }
}
