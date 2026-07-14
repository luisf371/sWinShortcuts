using System;
using System.IO;
using System.Windows.Input;
using sWinShortcuts.Utilities;
using Xunit;

namespace Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void SetColorToggleKey_UsesExplicitNoneMarker()
    {
        var document = new IniDocument();

        AppSettings.SetColorToggleKey(document, Key.None);

        Assert.Equal("None", document.GetValue("App", AppSettings.ColorToggleKeyName));
    }

    [Fact]
    public void LoadColorToggleKey_PrefersAppValueOverLegacyColorValue()
    {
        var root = CreateTempDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "sWinShortcuts.ini");
            var appSettings = new IniDocument();
            AppSettings.SetColorToggleKey(appSettings, Key.F8);
            appSettings.Save(settingsPath);

            var legacy = new IniDocument();
            legacy.SetKey("Color", "ToggleKey", Key.F9);
            legacy.Save(Path.Combine(root, "Color.ini"));

            Assert.Equal(Key.F8, AppSettings.LoadColorToggleKey(settingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    [Fact]
    public void LoadColorToggleKey_ExplicitNoneWinsOverLegacyColorValue()
    {
        var root = CreateTempDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "sWinShortcuts.ini");
            var appSettings = new IniDocument();
            AppSettings.SetColorToggleKey(appSettings, Key.None);
            appSettings.Save(settingsPath);

            var legacy = new IniDocument();
            legacy.SetKey("Color", "ToggleKey", Key.F9);
            legacy.Save(Path.Combine(root, "Color.ini"));

            Assert.Null(AppSettings.LoadColorToggleKey(settingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MigrateLegacyColorToggleKey_WritesAppValue()
    {
        var root = CreateTempDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "sWinShortcuts.ini");
            var legacy = new IniDocument();
            legacy.SetKey("Color", "ToggleKey", Key.F7);
            legacy.Save(Path.Combine(root, "Color.ini"));

            AppSettings.MigrateLegacyColorToggleKey(settingsPath);

            var migrated = IniDocument.Load(settingsPath);
            Assert.Equal("F7", migrated.GetValue("App", AppSettings.ColorToggleKeyName));
            Assert.Equal(Key.F7, AppSettings.LoadColorToggleKey(settingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sWinShortcutsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
