using System;
using System.IO;
using System.Windows.Input;

namespace sWinShortcuts.Utilities;

public static class AppSettings
{
    private static readonly object StorageGate = new();
    private static Task _pendingStorage = Task.CompletedTask;
    public const string ColorToggleKeyName = "ColorToggleKey";
    public const string RapidFireToggleKeyName = "RapidFireToggleKey";
    public const string CheckForUpdatesKeyName = "CheckForUpdates";

    public static string GetRootDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "sWinShortcuts");
    }

    public static string GetSettingsPath()
        => Path.Combine(GetRootDirectory(), "sWinShortcuts.ini");

    public static Task<IniDocument> LoadAsync(string settingsPath)
        => RunStorageAsync(() => IniDocument.Load(settingsPath));

    public static Task UpdateAsync(string settingsPath, Action<IniDocument> update)
        => RunStorageAsync(() =>
        {
            var document = IniDocument.Load(settingsPath);
            update(document);
            document.Save(settingsPath);
            return true;
        });

    public static Task FlushAsync()
    {
        lock (StorageGate)
        {
            return _pendingStorage;
        }
    }

    private static Task<T> RunStorageAsync<T>(Func<T> operation)
    {
        // One app-settings file: preserve enqueue order across complete read/modify/write operations.
        lock (StorageGate)
        {
            var previous = _pendingStorage;
            var pending = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); }
                catch { /* Each caller receives its own failure; later operations can retry. */ }
                return operation();
            });
            _pendingStorage = pending;
            return pending;
        }
    }

    public static Key? LoadColorToggleKey(string settingsPath)
        => IniDocument.Load(settingsPath).GetKey("App", ColorToggleKeyName);

    public static void SetColorToggleKey(IniDocument document, Key? key)
    {
        SetToggleKey(document, ColorToggleKeyName, key);
    }

    public static Key? LoadRapidFireToggleKey(string settingsPath)
    {
        return IniDocument.Load(settingsPath).GetKey("App", RapidFireToggleKeyName);
    }

    /// <summary>[App] CheckForUpdates — default OFF: only the literal "true" enables the GitHub update check.</summary>
    public static bool LoadCheckForUpdatesEnabled(string settingsPath)
        => IniDocument.Load(settingsPath).GetValue("App", CheckForUpdatesKeyName) == "true";

    public static void SetRapidFireToggleKey(IniDocument document, Key? key)
    {
        SetToggleKey(document, RapidFireToggleKeyName, key);
    }

    private static void SetToggleKey(IniDocument document, string name, Key? key)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Persist an explicit unassigned value instead of removing the current setting.
        var serialized = !key.HasValue || key.Value == Key.None
            ? "None"
            : KeySerializer.Serialize(key);
        document.SetValue("App", name, serialized);
    }
}
